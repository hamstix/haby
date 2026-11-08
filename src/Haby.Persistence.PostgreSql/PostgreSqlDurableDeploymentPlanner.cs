using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Durable, side-effect-free planning of initial work or bounded Resource reuse.</summary>
public sealed class PostgreSqlDurableDeploymentPlanner(
    IDbContextFactory<ManifestCatalogDbContext> contexts, IManifestRevisionQuery revisions,
    IProviderAliasResolver aliases, IEnumerable<IProviderConfigurationCodec> codecs,
    DurableActionAdmission admission, TimeProvider clock, ConfigurationRenderingCatalog? rendering = null,
    IEnumerable<IExternalOperationOutputContract>? outputContracts = null)
    : IDurableDeploymentPlanner
{
    private readonly ConfigurationRenderingCatalog _rendering = rendering ?? ConfigurationRenderingCatalog.Empty;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly IProviderConfigurationCodec[] _codecs = codecs.ToArray();

    public async ValueTask<DurableChangeSetCreateResult> CreateChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentPrecondition precondition,
        string actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(precondition);
        ValidateActor(actorId);
        cancellationToken.ThrowIfCancellationRequested();
        if (precondition is not (DeploymentPrecondition.ExpectedAbsence or DeploymentPrecondition.ExpectedGeneration))
            throw new ArgumentException("Unsupported precondition.", nameof(precondition));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var environmentId = Guid.Parse(environment.ToString());
        var applicationId = Guid.Parse(application.ToString());
        if (!await context.Environments.AsNoTracking().AnyAsync(row => row.Id == environmentId, cancellationToken))
            return new(DurableChangeSetCreateStatus.EnvironmentNotFound);
        if (revision.ApplicationRecordId != application)
            return new(DurableChangeSetCreateStatus.RevisionNotOwnedByApplication);
        if (!await context.Revisions.AsNoTracking().AnyAsync(row => row.ApplicationRecordId == applicationId &&
            row.LocalRevisionNumber == revision.RevisionNumber.Value, cancellationToken))
            return new(DurableChangeSetCreateStatus.RevisionNotFound);
        var id = DeploymentChangeSetId.New();
        var createdAt = clock.GetUtcNow();
        context.DurableChangeSets.Add(new DurableChangeSetRow
        {
            Id = Guid.Parse(id.ToString()),
            EnvironmentRecordId = environmentId,
            ApplicationRecordId = applicationId,
            RevisionNumber = revision.RevisionNumber.Value,
            ExpectedAbsence = precondition is DeploymentPrecondition.ExpectedAbsence,
            ExpectedGeneration = (precondition as DeploymentPrecondition.ExpectedGeneration)?.Generation.Value,
            CreatedAt = createdAt,
            ActorId = actorId
        });
        await context.SaveChangesAsync(cancellationToken);
        return new(DurableChangeSetCreateStatus.Created, new DeploymentChangeSet(id, environment,
            new DeploymentChangeSetMember(application, revision, precondition), createdAt, actorId));
    }

    public async ValueTask<DurablePlanResult> CreatePlanAsync(DeploymentChangeSetId changeSetId,
        string actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSetId);
        ValidateActor(actorId);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var changeId = Guid.Parse(changeSetId.ToString());
        // Commit takes FOR UPDATE on this row. Planning holds FOR SHARE through
        // insertion so a Plan cannot be created after this ChangeSet commits.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM deployment_change_sets WHERE id = {changeId} FOR SHARE", cancellationToken);
        var row = await context.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == changeId, cancellationToken);
        if (row is null) return new(DurablePlanStatus.ChangeSetNotFound);
        if (await context.ChangeSetCommits.AsNoTracking().AnyAsync(item => item.ChangeSetId == changeId,
            cancellationToken)) return new(DurablePlanStatus.ChangeSetStale);
        var baseline = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.EnvironmentRecordId == row.EnvironmentRecordId &&
            item.ApplicationRecordId == row.ApplicationRecordId, cancellationToken);
        if (!Matches(row, baseline))
            return new(DurablePlanStatus.ChangeSetStale);
        if (baseline is not null && baseline.AppliedGeneration != baseline.DesiredGeneration)
            return new(DurablePlanStatus.PlanInputUnavailable);
        var reference = ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
            ManifestRevisionNumber.Create(row.RevisionNumber));
        var revision = await revisions.FindAsync(reference, cancellationToken);
        if (revision is null) return new(DurablePlanStatus.RevisionNotFound);
        var environment = await context.Environments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == row.EnvironmentRecordId, cancellationToken);
        if (environment is null) return new(DurablePlanStatus.ChangeSetStale);
        var spec = revision.Declaration.Spec;
        if (!DurableActionAdmission.SupportsPilotShape(spec)) return new(DurablePlanStatus.UnsupportedShape);
        var alias = DurableActionAdmission.SingleAlias(spec);
        if (alias is null) return new(DurablePlanStatus.UnsupportedShape);
        var environmentId = EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString());
        var resolved = await aliases.ResolveAsync(environmentId, alias, cancellationToken);
        if (resolved is null) return new(DurablePlanStatus.AliasNotResolved);
        var revisionRow = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ProviderInstanceId == Guid.Parse(resolved.Binding.InstanceId.ToString()) &&
            item.RevisionNumber == resolved.Binding.RevisionNumber.Value &&
            item.EnvironmentRecordId == row.EnvironmentRecordId && item.IsSealed, cancellationToken);
        if (revisionRow is null || revisionRow.ImplementationId != resolved.ImplementationId)
            return new(DurablePlanStatus.ProviderRevisionUnavailable);
        var inputs = new DurablePlanningInputs(environmentId, EnvironmentId.Create(environment.EnvironmentId),
            reference, alias, resolved.Binding.InstanceId, resolved.Binding.RevisionNumber,
            resolved.ImplementationId);
        try
        {
            var configuration = await ConfigurationPlanningCapture.CaptureAsync(context,
                new(environmentId, inputs.EnvironmentKey), reference, revision.Declaration, cancellationToken, _rendering);
            if (configuration.Snapshot is null) return new(DurablePlanStatus.ConfigurationRefused, ConfigurationReport: configuration.Expansion);
            inputs = inputs with { Configuration = configuration.Snapshot };
        }
        catch (InvalidDataException) { return new(DurablePlanStatus.PlanInputUnavailable); }
        var admitted = await AdmitAsync(context, spec, revisionRow, inputs, cancellationToken);
        if (admitted != DurablePlanStatus.Planned) return new(admitted);
        DurableOperationGraph graph;
        try
        {
            graph = baseline is null ? await PlanGraphAsync(context, spec, revisionRow, inputs, cancellationToken) :
                await ReuseGraphAsync(context, baseline, spec, revisionRow, inputs, cancellationToken)
                    ?? throw new NotSupportedException();
        }
        catch (NotSupportedException) { return new(DurablePlanStatus.UnsupportedShape); }
        catch (InvalidDataException) { return new(DurablePlanStatus.PlanInputUnavailable); }
        var id = DeploymentPlanId.New();
        var createdAt = clock.GetUtcNow();
        var planRow = new DurablePlanRow
        {
            Id = Guid.Parse(id.ToString()),
            ChangeSetId = row.Id,
            EnvironmentRecordId = row.EnvironmentRecordId,
            EnvironmentKey = environment.EnvironmentId,
            ApplicationRecordId = row.ApplicationRecordId,
            RevisionNumber = row.RevisionNumber,
            ProviderAlias = resolved.Alias.Value,
            ProviderInstanceId = Guid.Parse(resolved.Binding.InstanceId.ToString()),
            ConfigurationRevisionNumber = resolved.Binding.RevisionNumber.Value,
            ImplementationId = resolved.ImplementationId,
            SnapshotFormatVersion = 2,
            ConfigurationJson = ConfigurationPlanningSnapshotCodec.Encode(inputs.Configuration!),
            GraphJson = DurableOperationGraphCodec.Encode(graph),
            Fingerprint = DurableOperationGraphCodec.Fingerprint(inputs, graph),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(Lifetime),
            ActorId = actorId
        };
        context.DurablePlans.Add(planRow);
        await context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return new(DurablePlanStatus.Planned, ToPlan(planRow, null, null));
    }

    public async ValueTask<DeploymentChangeSet?> FindChangeSetAsync(DeploymentChangeSetId id,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == Guid.Parse(id.ToString()), cancellationToken);
        return row is null ? null : ToChangeSet(row);
    }

    public async ValueTask<DurablePlan?> FindPlanAsync(DeploymentPlanId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.DurablePlans.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == Guid.Parse(id.ToString()), cancellationToken);
        if (row is null) return null;
        var closure = await context.DurablePlanClosures.AsNoTracking().SingleOrDefaultAsync(item =>
            item.PlanId == row.Id, cancellationToken);
        return ToPlan(row, closure?.ClosedAt, closure?.ActorId);
    }

    public async ValueTask<DurablePlanCloseStatus> ClosePlanAsync(DeploymentPlanId id, string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ValidateActor(actorId);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var planId = Guid.Parse(id.ToString());
        if (!await context.DurablePlans.AsNoTracking().AnyAsync(item => item.Id == planId, cancellationToken))
            return DurablePlanCloseStatus.PlanNotFound;
        context.DurablePlanClosures.Add(new DurablePlanClosureRow
        {
            PlanId = planId,
            ClosedAt = clock.GetUtcNow(),
            ActorId = actorId
        });
        try { await context.SaveChangesAsync(cancellationToken); return DurablePlanCloseStatus.Closed; }
        catch (DbUpdateException error) when (!cancellationToken.IsCancellationRequested &&
            Postgres(error)?.SqlState == PostgresErrorCodes.UniqueViolation)
        { return DurablePlanCloseStatus.AlreadyClosed; }
    }

    public async ValueTask<DurablePlanCurrentStatus> CheckPlanAsync(DeploymentPlanId id,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        DurablePlan? plan;
        try { plan = await FindPlanAsync(id, cancellationToken); }
        catch (InvalidDataException) { return DurablePlanCurrentStatus.PlanInputUnavailable; }
        if (plan is null) return DurablePlanCurrentStatus.PlanNotFound;
        if (clock.GetUtcNow() >= plan.ExpiresAt) return DurablePlanCurrentStatus.Expired;
        if (plan.ClosedAt is not null) return DurablePlanCurrentStatus.Closed;
        (DurablePlanStatus Status, DurablePlanningInputs? Inputs) result;
        try { result = await CreateCurrentInputsAsync(plan, cancellationToken); }
        catch (InvalidDataException) { return DurablePlanCurrentStatus.PlanInputUnavailable; }
        if (result.Status == DurablePlanStatus.ChangeSetStale) return DurablePlanCurrentStatus.ChangeSetStale;
        if (result.Status != DurablePlanStatus.Planned || result.Inputs is null)
            return DurablePlanCurrentStatus.PlanInputUnavailable;
        if (result.Inputs != plan.Inputs || DurableOperationGraphCodec.Fingerprint(result.Inputs, plan.Graph) != plan.Fingerprint)
            return DurablePlanCurrentStatus.PlanStale;
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var revisionRow = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(r =>
            r.ProviderInstanceId == Guid.Parse(plan.Inputs.ProviderInstance.ToString()) && r.RevisionNumber == plan.Inputs.ConfigurationRevision.Value,
            cancellationToken);
        var manifest = await revisions.FindAsync(plan.Inputs.Revision, cancellationToken);
        try
        {
            return revisionRow is not null && manifest is not null &&
                await ValidateGraphAsync(context, manifest.Declaration.Spec, revisionRow, plan.Inputs, plan.Graph, cancellationToken)
                ? DurablePlanCurrentStatus.Current : plan.Graph.Baseline is null ? DurablePlanCurrentStatus.PlanInputUnavailable : DurablePlanCurrentStatus.PlanStale;
        }
        catch (InvalidDataException) { return DurablePlanCurrentStatus.PlanInputUnavailable; }
    }

    private async ValueTask<(DurablePlanStatus Status, DurablePlanningInputs? Inputs)> CreateCurrentInputsAsync(
        DurablePlan plan, CancellationToken cancellationToken)
    {
        var id = plan.ChangeSetId;
        // Re-evaluate without writing a replacement Plan.
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var row = await context.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == Guid.Parse(id.ToString()), cancellationToken);
        if (row is null) return (DurablePlanStatus.ChangeSetStale, null);
        var baseline = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.EnvironmentRecordId == row.EnvironmentRecordId &&
            item.ApplicationRecordId == row.ApplicationRecordId, cancellationToken);
        if (!Matches(row, baseline))
            return (DurablePlanStatus.ChangeSetStale, null);
        if (baseline is not null && baseline.AppliedGeneration != baseline.DesiredGeneration)
            return (DurablePlanStatus.PlanInputUnavailable, null);
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
            ManifestRevisionNumber.Create(row.RevisionNumber));
        var manifest = await revisions.FindAsync(revision, cancellationToken);
        if (manifest is null) return (DurablePlanStatus.RevisionNotFound, null);
        if (!DurableActionAdmission.SupportsPilotShape(manifest.Declaration.Spec))
            return (DurablePlanStatus.UnsupportedShape, null);
        var alias = DurableActionAdmission.SingleAlias(manifest.Declaration.Spec);
        if (alias is null) return (DurablePlanStatus.UnsupportedShape, null);
        var environment = await context.Environments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == row.EnvironmentRecordId, cancellationToken);
        if (environment is null) return (DurablePlanStatus.ChangeSetStale, null);
        var environmentId = EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString());
        var resolution = await aliases.ResolveAsync(environmentId, alias, cancellationToken);
        if (resolution is null) return (DurablePlanStatus.AliasNotResolved, null);
        var providerRevision = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ProviderInstanceId == Guid.Parse(resolution.Binding.InstanceId.ToString()) &&
            item.RevisionNumber == resolution.Binding.RevisionNumber.Value &&
            item.EnvironmentRecordId == row.EnvironmentRecordId && item.IsSealed, cancellationToken);
        if (providerRevision is null || providerRevision.ImplementationId != resolution.ImplementationId)
            return (DurablePlanStatus.ProviderRevisionUnavailable, null);
        var inputs = new DurablePlanningInputs(environmentId,
            EnvironmentId.Create(environment.EnvironmentId), revision, alias, resolution.Binding.InstanceId,
            resolution.Binding.RevisionNumber, resolution.ImplementationId);
        var frozen = plan.Inputs.Configuration ?? throw new InvalidDataException();
        var scopes = await ConfigurationPlanningCapture.ReadScopesAsync(context,
            new(environmentId, inputs.EnvironmentKey), frozen.Manifest, cancellationToken);
        if (!ConfigurationPlanningCapture.SameScopes(scopes, frozen.Scopes)) return (DurablePlanStatus.Planned, inputs);
        if (!manifest.Declaration.Equals(frozen.Manifest)) return (DurablePlanStatus.PlanInputUnavailable, null);
        if (frozen.RenderPins.Any(p => !_rendering.Supports(p))) return (DurablePlanStatus.PlanInputUnavailable, null);
        var captured = await ConfigurationPlanningCapture.CaptureAsync(context,
            new(environmentId, inputs.EnvironmentKey), revision, manifest.Declaration, cancellationToken, _rendering, frozen.RenderPins);
        if (captured.Snapshot is null || !captured.Snapshot.Equals(frozen)) return (DurablePlanStatus.PlanInputUnavailable, null);
        inputs = inputs with { Configuration = frozen };
        var admitted = await AdmitAsync(context, manifest.Declaration.Spec, providerRevision, inputs, cancellationToken);
        return (admitted, admitted == DurablePlanStatus.Planned ? inputs : null);
    }

    internal bool SupportsRendering(ConfigurationPlanningSnapshot snapshot) => snapshot.RenderPins.All(_rendering.Supports);

    // The caller owns the context and transaction. Commit passes its already-loaded exact
    // revision, retaining its row/advisory locks through all admission and persistence.
    internal async Task<DurablePlanStatus> AdmitAsync(ManifestCatalogDbContext context,
        NormalizedManifestSpec spec, ProviderConfigurationRevisionRow revision, DurablePlanningInputs inputs,
        CancellationToken cancellationToken)
    {
        if (!revision.IsSealed || revision.EnvironmentRecordId != Guid.Parse(inputs.Environment.ToString()) ||
            revision.ProviderInstanceId != Guid.Parse(inputs.ProviderInstance.ToString()) ||
            revision.RevisionNumber != inputs.ConfigurationRevision.Value || revision.ImplementationId != inputs.ImplementationId)
            return DurablePlanStatus.ProviderRevisionUnavailable;
        var instance = await context.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == revision.ProviderInstanceId && row.EnvironmentRecordId == revision.EnvironmentRecordId,
            cancellationToken);
        if (instance is null) return DurablePlanStatus.ProviderRevisionUnavailable;
        DurablePlanStatus result;
        try
        {
            var provider = new ProviderAdapterKey(instance.ProviderKindId, revision.ConfigurationShapeId, revision.ImplementationId);
            if (!admission.HasPlanningSupport(spec, provider)) return DurablePlanStatus.ProviderCapabilityMismatch;
            if (!await CodecAvailableAsync(context, revision, cancellationToken)) return DurablePlanStatus.ProviderRevisionUnavailable;
            result = admission.Admit(spec, provider, inputs, PreparedConfiguration(instance.ProviderKindId, revision));
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        { return DurablePlanStatus.ProviderRevisionUnavailable; }
        return result;
    }

    internal async Task<bool> CodecAvailableAsync(ManifestCatalogDbContext context,
        ProviderConfigurationRevisionRow revision, CancellationToken cancellationToken)
    {
        var instance = await context.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == revision.ProviderInstanceId && item.EnvironmentRecordId == revision.EnvironmentRecordId,
            cancellationToken);
        if (instance is null) return false;
        var matches = _codecs.Where(item => item.ProviderKindId == instance.ProviderKindId &&
            item.ShapeId == revision.ConfigurationShapeId && item.PayloadFormatVersion == revision.PayloadFormatVersion)
            .Take(2).ToArray();
        if (matches.Length != 1) return false;
        var codec = matches[0];
        var references = await context.ProviderRevisionSecretRefs.AsNoTracking().Where(item =>
            item.ProviderInstanceId == revision.ProviderInstanceId && item.RevisionNumber == revision.RevisionNumber)
            .ToListAsync(cancellationToken);
        try
        {
            var prepared = codec.Encode(codec.Decode(revision.PayloadJson));
            return prepared.ProviderKindId == instance.ProviderKindId &&
                prepared.ShapeId == revision.ConfigurationShapeId &&
                prepared.PayloadFormatVersion == revision.PayloadFormatVersion &&
                prepared.ImplementationId == revision.ImplementationId &&
                prepared.PayloadJson == revision.PayloadJson &&
                prepared.SecretReferences.Count == references.Count &&
                prepared.SecretReferences.All(reference => references.Any(item => item.Slot == reference.Slot &&
                    item.SecretId == Guid.Parse(reference.Reference.SecretId.ToString()) &&
                    item.SecretVersionId == Guid.Parse(reference.Reference.SecretVersionId.ToString())));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or FormatException or System.Text.Json.JsonException)
        { return false; }
    }

    private static DeploymentChangeSet ToChangeSet(DurableChangeSetRow row)
    {
        var application = ApplicationRecordId.Parse(row.ApplicationRecordId.ToString());
        var revision = ManifestRevisionRef.Create(application, ManifestRevisionNumber.Create(row.RevisionNumber));
        return new(DeploymentChangeSetId.Parse(row.Id.ToString()),
            EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString()),
            new DeploymentChangeSetMember(application, revision, row.ExpectedAbsence ? new DeploymentPrecondition.ExpectedAbsence() :
                new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(row.ExpectedGeneration ?? throw new InvalidDataException()))),
            row.CreatedAt, row.ActorId);
    }

    private static DurablePlan ToPlan(DurablePlanRow row, DateTimeOffset? closedAt, string? closedByActorId)
    {
        if (row.SnapshotFormatVersion != 2) throw new InvalidDataException("Planning snapshot version is unavailable.");
        var application = ApplicationRecordId.Parse(row.ApplicationRecordId.ToString());
        var inputs = new DurablePlanningInputs(EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString()),
            EnvironmentId.Create(row.EnvironmentKey),
            ManifestRevisionRef.Create(application, ManifestRevisionNumber.Create(row.RevisionNumber)),
            ProviderAlias.Create(row.ProviderAlias), ProviderInstanceId.Parse(row.ProviderInstanceId.ToString()),
            ProviderConfigurationRevisionNumber.Create(row.ConfigurationRevisionNumber), row.ImplementationId);
        inputs = inputs with { Configuration = ConfigurationPlanningSnapshotCodec.Decode(row.ConfigurationJson, inputs.Revision) };
        var graph = DurableOperationGraphCodec.Decode(row.GraphJson);
        if (DurableOperationGraphCodec.Fingerprint(inputs, graph) != row.Fingerprint) throw new InvalidDataException("Planning snapshot is inconsistent.");
        return new(DeploymentPlanId.Parse(row.Id.ToString()), DeploymentChangeSetId.Parse(row.ChangeSetId.ToString()),
            inputs, row.Fingerprint, row.CreatedAt, row.ExpiresAt, row.ActorId, closedAt, closedByActorId)
        { Graph = graph };
    }

    private PreparedProviderConfiguration PreparedConfiguration(string kind, ProviderConfigurationRevisionRow revision)
    {
        var matches = _codecs.Where(c => c.ProviderKindId == kind && c.ShapeId == revision.ConfigurationShapeId &&
            c.PayloadFormatVersion == revision.PayloadFormatVersion).Take(2).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Provider codec unavailable.");
        return matches[0].Encode(matches[0].Decode(revision.PayloadJson));
    }

    internal async Task<bool> ValidateGraphAsync(ManifestCatalogDbContext context, NormalizedManifestSpec spec,
        ProviderConfigurationRevisionRow revision, DurablePlanningInputs inputs, DurableOperationGraph graph, CancellationToken token)
    {
        if (graph.Baseline is { } baseline)
        {
            var owner = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Guid.Parse(baseline.Deployment.ToString()), token);
            return owner is not null && owner.EnvironmentRecordId == Guid.Parse(inputs.Environment.ToString()) && owner.ApplicationRecordId == Guid.Parse(inputs.Revision.ApplicationRecordId.ToString()) &&
                owner.DesiredGeneration == baseline.Generation && owner.AppliedGeneration == baseline.Generation &&
                (await ReuseGraphAsync(context, owner, spec, revision, inputs, token))?.Equals(graph) == true;
        }
        var instance = await context.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(r => r.Id == revision.ProviderInstanceId, token);
        return instance is not null && admission.ValidateGraph(spec,
            new(instance.ProviderKindId, revision.ConfigurationShapeId, revision.ImplementationId), inputs,
            PreparedConfiguration(instance.ProviderKindId, revision), graph);
    }

    internal static bool Matches(DurableChangeSetRow change, ApplicationDeploymentRow? actual) =>
        change.ExpectedAbsence ? change.ExpectedGeneration is null && actual is null :
        change.ExpectedGeneration is > 0 && actual?.DesiredGeneration == change.ExpectedGeneration;

    internal async Task<DurableOperationGraph?> ReuseGraphAsync(ManifestCatalogDbContext db, ApplicationDeploymentRow owner,
        NormalizedManifestSpec target, ProviderConfigurationRevisionRow provider, DurablePlanningInputs inputs, CancellationToken token)
    {
        var baseline = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == owner.DesiredGeneration, token)
            ?? throw new InvalidDataException();
        if (owner.AppliedGeneration != owner.DesiredGeneration || baseline.EnvironmentRecordId != owner.EnvironmentRecordId ||
            baseline.ApplicationRecordId != owner.ApplicationRecordId) throw new InvalidDataException();
        if (baseline.ProviderInstanceId != provider.ProviderInstanceId || baseline.ConfigurationRevisionNumber != provider.RevisionNumber ||
            baseline.ImplementationId != inputs.ImplementationId) return null;
        var accepted = ConfigurationPlanningSnapshotCodec.Decode(baseline.ConfigurationJson,
            ManifestRevisionRef.Create(inputs.Revision.ApplicationRecordId, ManifestRevisionNumber.Create(baseline.RevisionNumber)));
        _ = ResourceContinuity.Inputs(baseline);
        var baselineGraph = DurableOperationGraphCodec.Decode(baseline.GraphJson);
        if (baselineGraph.Baseline is not null && (!await ResourceContinuity.ValidateAsync(db, owner.Id, baselineGraph, token) ||
            await db.DurableOperations.AnyAsync(x => x.DeploymentId == owner.Id && x.Generation == baseline.Generation, token))) throw new InvalidDataException();
        if (!accepted.Manifest.Spec.Components.Keys.ToHashSet().SetEquals(target.Components.Keys) ||
            target.Components.Any(c => !accepted.Manifest.Spec.Components[c.Key].Bindings.Keys.ToHashSet().SetEquals(c.Value.Bindings.Keys))) return null;
        // Same-revision configuration-only intent is deliberately outside this slice.
        if (baseline.RevisionNumber == inputs.Revision.RevisionNumber.Value && !accepted.Equals(inputs.Configuration)) return null;
        var instance = await db.ProviderInstances.AsNoTracking().SingleAsync(x => x.Id == provider.ProviderInstanceId, token);
        if (!admission.CanReuse(accepted.Manifest.Spec, target, new(instance.ProviderKindId, provider.ConfigurationShapeId, provider.ImplementationId),
            inputs, PreparedConfiguration(instance.ProviderKindId, provider))) return null;
        var reuse = new List<ResourceReuse>();
        foreach (var resource in target.Resources.Keys)
        {
            var managed = await db.ResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Environment == owner.EnvironmentRecordId &&
                x.Application == owner.ApplicationRecordId && x.Resource == resource.Value, token);
            if (managed is null || managed.Deployment != owner.Id) throw new InvalidDataException();
            var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == managed.SourceGeneration, token)
                ?? throw new InvalidDataException();
            if (source.ProviderInstanceId != provider.ProviderInstanceId || source.ConfigurationRevisionNumber != provider.RevisionNumber ||
                source.ImplementationId != inputs.ImplementationId) throw new InvalidDataException();
            var evidence = await ResourceContinuity.EvidenceAsync(db, managed, token) ?? throw new InvalidDataException();
            var original = DurableOperationGraphCodec.Decode(source.GraphJson);
            // Missing registrations cannot turn a lost mandatory terminal output into
            // an action that supposedly never required confirmed evidence.
            if (!admission.Supports(original)) throw new InvalidDataException();
            if (!await ValidateGraphAsync(db, target, provider, inputs, original, token)) throw new InvalidDataException();
            reuse.Add(new(resource, managed.Id, managed.SourceGeneration, evidence));
        }
        var result = new DurableOperationGraph([], reuse, new(ApplicationDeploymentId.Parse(owner.Id.ToString()), baseline.Generation, baseline.Fingerprint));
        if (!await ResourceContinuity.ValidateOutputsAsync(db, owner.Id, result, outputContracts ?? [], _codecs, token)) throw new InvalidDataException();
        return result;
    }
    internal async Task<DurableOperationGraph> PlanGraphAsync(ManifestCatalogDbContext context,
        NormalizedManifestSpec spec, ProviderConfigurationRevisionRow revision, DurablePlanningInputs inputs,
        CancellationToken cancellationToken)
    {
        var instance = await context.ProviderInstances.AsNoTracking().SingleAsync(row => row.Id == revision.ProviderInstanceId, cancellationToken);
        return admission.Plan(spec, new(instance.ProviderKindId, revision.ConfigurationShapeId, revision.ImplementationId), inputs,
            PreparedConfiguration(instance.ProviderKindId, revision));
    }

    internal static void ValidateActor(string actorId)
    {
        ArgumentNullException.ThrowIfNull(actorId);
        if (actorId.Length is 0 or > 256 || actorId.Any(char.IsControl))
            throw new ArgumentException("Actor identity must be bounded and nonempty.", nameof(actorId));
    }

    private static PostgresException? Postgres(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PostgresException postgres) return postgres;
        return null;
    }
}

public static class DurableDeploymentPlannerServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDurableDeploymentPlanner(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<DurableActionRegistry>();
        services.TryAddSingleton<DurableActionAdmission>();
        services.TryAddSingleton<PostgreSqlDurableDeploymentPlanner>();
        services.TryAddSingleton<IDurableDeploymentPlanner>(provider =>
            provider.GetRequiredService<PostgreSqlDurableDeploymentPlanner>());
        return services;
    }
}
