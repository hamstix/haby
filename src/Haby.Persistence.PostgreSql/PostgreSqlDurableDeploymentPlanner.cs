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
public sealed partial class PostgreSqlDurableDeploymentPlanner(
    IDbContextFactory<ManifestCatalogDbContext> contexts, IManifestRevisionQuery revisions,
    IProviderAliasResolver aliases, IEnumerable<IProviderConfigurationCodec> codecs,
    DurableActionAdmission admission, TimeProvider clock, ConfigurationRenderingCatalog? rendering = null,
    IEnumerable<IExternalOperationOutputContract>? outputContracts = null)
    : IDurableDeploymentPlanner, IOperatorImportRequestStore
{
    private readonly ConfigurationRenderingCatalog _rendering = rendering ?? ConfigurationRenderingCatalog.Empty;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly IProviderConfigurationCodec[] _codecs = codecs.ToArray();

    public async ValueTask<DurableChangeSetCreateResult> CreateChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentPrecondition precondition,
        string actorId, CancellationToken cancellationToken) =>
        await CreateChangeSetCoreAsync(environment, application, revision, precondition, null, actorId, cancellationToken);

    internal ValueTask<DurableChangeSetCreateResult> CreateAcquisitionChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, ResourceAcquisitionIntent acquisitions,
        string actor, CancellationToken token, DeploymentPrecondition? precondition = null) => CreateChangeSetCoreAsync(environment, application, revision,
            precondition ?? new DeploymentPrecondition.ExpectedAbsence(), null, actor, token, acquisitions);

    public ValueTask<DurableChangeSetCreateResult> CreateImportChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, ResourceImportIntent intent,
        string actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return CreateChangeSetCoreAsync(environment, application, revision, new DeploymentPrecondition.ExpectedAbsence(), intent, actorId, cancellationToken);
    }

    private async ValueTask<DurableChangeSetCreateResult> CreateChangeSetCoreAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentPrecondition precondition,
        ResourceImportIntent? intent, string actorId, CancellationToken cancellationToken, ResourceAcquisitionIntent? acquisitions = null)
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
        var validation = await ValidateChangeSetInputAsync(context, environment, application, revision, intent, false, cancellationToken);
        if (validation is { } refused) return new(refused);
        var change = await AddChangeSetAsync(context, environment, application, revision, precondition, intent, actorId, cancellationToken, acquisitions);
        await context.SaveChangesAsync(cancellationToken);
        return new(DurableChangeSetCreateStatus.Created, change);
    }

    private async Task<DurableChangeSetCreateStatus?> ValidateChangeSetInputAsync(ManifestCatalogDbContext context,
        EnvironmentRecordId environment, ApplicationRecordId application, ManifestRevisionRef revision,
        ResourceImportIntent? intent, bool localRevision, CancellationToken token, bool importGroup = false)
    {
        var environmentId = Guid.Parse(environment.ToString());
        var applicationId = Guid.Parse(application.ToString());
        if (!await context.Environments.AsNoTracking().AnyAsync(row => row.Id == environmentId, token))
            return DurableChangeSetCreateStatus.EnvironmentNotFound;
        if (revision.ApplicationRecordId != application)
            return DurableChangeSetCreateStatus.RevisionNotOwnedByApplication;
        if (!await context.Revisions.AsNoTracking().AnyAsync(row => row.ApplicationRecordId == applicationId &&
            row.LocalRevisionNumber == revision.RevisionNumber.Value, token)) return DurableChangeSetCreateStatus.RevisionNotFound;
        if (intent is not null)
        {
            // A request-owned transaction uses its own connection, including a single-connection catalog pool.
            var accepted = localRevision
                ? PostgreSqlManifestImportStore.Rehydrate(await context.Revisions.AsNoTracking().SingleAsync(row =>
                    row.ApplicationRecordId == applicationId && row.LocalRevisionNumber == revision.RevisionNumber.Value, token))
                : await revisions.FindAsync(revision, token);
            if (accepted is null) return DurableChangeSetCreateStatus.RevisionNotFound;
            try
            {
                if (importGroup)
                {
                    if (intent.Resources.Any(x => !accepted.Declaration.Spec.Resources.ContainsKey(x.Resource)))
                        throw new InvalidDataException("Import group Resource is outside its target revision.");
                }
                else intent.Validate(accepted.Declaration.Spec);
            }
            catch (InvalidDataException) { return DurableChangeSetCreateStatus.ImportNotSupported; }
        }
        return null;
    }

    private async Task<DeploymentChangeSet> AddChangeSetAsync(ManifestCatalogDbContext context, EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentPrecondition precondition,
        ResourceImportIntent? intent, string actorId, CancellationToken token, ResourceAcquisitionIntent? acquisitions = null, CorrectiveChangeSetIntent? correction = null, Guid? requestId = null)
    {
        var accepted = PostgreSqlManifestImportStore.Rehydrate(await context.Revisions.AsNoTracking().SingleAsync(x =>
            x.ApplicationRecordId == Guid.Parse(application.ToString()) && x.LocalRevisionNumber == revision.RevisionNumber.Value, token));
        if (acquisitions is null && intent is null && precondition is DeploymentPrecondition.ExpectedGeneration expected)
        {
            var environmentId = Guid.Parse(environment.ToString());
            var applicationId = Guid.Parse(application.ToString());
            var owner = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == environmentId && x.ApplicationRecordId == applicationId, token);
            var previous = owner is null ? null : await context.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == expected.Generation.Value, token);
            if (previous is not null)
            {
                var retained = previous.Inputs().Acquisitions.Resources.Where(x => accepted.Declaration.Spec.Resources.ContainsKey(x.Resource)).ToArray();
                acquisitions = new(retained.Concat(accepted.Declaration.Spec.Resources.Keys.Where(x => !retained.Any(r => r.Resource == x))
                    .Select(x => new ResourceAcquisitionRequest(x, ResourceAcquisitionMode.Provision))));
            }
        }
        acquisitions ??= intent is null ? ResourceAcquisitionIntent.Provision(accepted.Declaration.Spec.Resources.Keys) : ResourceAcquisitionIntent.Import(intent);
        acquisitions.Validate(accepted.Declaration.Spec);
        var id = requestId is { } request ? DeploymentChangeSetId.Parse(request.ToString()) : DeploymentChangeSetId.New();
        var createdAt = clock.GetUtcNow();
        context.DurableChangeSets.Add(new DurableChangeSetRow
        {
            Id = Guid.Parse(id.ToString()),
            CorrectiveIntentJson = CorrectiveIntentCodec.Encode(correction),
            ImportIntentJson = ResourceImportIntentCodec.Encode(intent),
            AcquisitionIntentJson = ResourceAcquisitionIntentCodec.Encode(acquisitions),
            EnvironmentRecordId = Guid.Parse(environment.ToString()),
            ApplicationRecordId = Guid.Parse(application.ToString()),
            RevisionNumber = revision.RevisionNumber.Value,
            ExpectedAbsence = precondition is DeploymentPrecondition.ExpectedAbsence,
            ExpectedGeneration = (precondition as DeploymentPrecondition.ExpectedGeneration)?.Generation.Value,
            CreatedAt = createdAt,
            ActorId = actorId
        });
        return new DeploymentChangeSet(id, environment,
            new DeploymentChangeSetMember(application, revision, precondition), createdAt, actorId)
        { Correction = correction, Import = intent, Acquisitions = acquisitions };
    }

    public ValueTask<DurablePlanResult> CreatePlanAsync(DeploymentChangeSetId changeSetId,
        string actorId, CancellationToken cancellationToken) => CreatePlanCoreAsync(changeSetId, actorId, DeploymentAdmissionMode.ResourceOnly, cancellationToken);

    internal ValueTask<DurablePlanResult> CreateInternalPlanAsync(DeploymentChangeSetId id, string actor, CancellationToken token, IReadOnlyList<IInternalActionHandler>? handlers = null) =>
        CreatePlanCoreAsync(id, actor, DeploymentAdmissionMode.Internal, token, handlers);

    private async ValueTask<DurablePlanResult> CreatePlanCoreAsync(DeploymentChangeSetId changeSetId, string actorId,
        DeploymentAdmissionMode mode, CancellationToken cancellationToken, IReadOnlyList<IInternalActionHandler>? handlers = null)
    {
        ArgumentNullException.ThrowIfNull(changeSetId);
        ValidateActor(actorId);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var changeId = Guid.Parse(changeSetId.ToString());
        var identity = await context.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == changeId, cancellationToken);
        if (identity is null) return new(DurablePlanStatus.ChangeSetNotFound);
        if (identity.ImportIntentJson is not null || identity.AcquisitionIntentJson is { } acquisitionJson &&
            ResourceAcquisitionIntentCodec.Decode(acquisitionJson).Resources.Any(x => x.Mode == ResourceAcquisitionMode.ImportUseAndRetain))
            await ImportMutationGate.LockAsync(context, identity.EnvironmentRecordId, identity.ApplicationRecordId, cancellationToken);
        // Commit takes FOR UPDATE on this row. Planning holds FOR SHARE through
        // insertion so a Plan cannot be created after this ChangeSet commits.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM deployment_change_sets WHERE id = {changeId} FOR SHARE", cancellationToken);
        var row = await context.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == changeId, cancellationToken);
        if (row is null) return new(DurablePlanStatus.ChangeSetNotFound);
        if (await context.ChangeSetCommits.AsNoTracking().AnyAsync(item => item.ChangeSetId == changeId,
            cancellationToken)) return new(DurablePlanStatus.ChangeSetStale);
        var retirementGate = $"haby-deployment:{row.EnvironmentRecordId:N}:{row.ApplicationRecordId:N}";
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({retirementGate}, 0))", cancellationToken);
        var baseline = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.EnvironmentRecordId == row.EnvironmentRecordId &&
            item.ApplicationRecordId == row.ApplicationRecordId, cancellationToken);
        if (!Matches(row, baseline))
            return new(DurablePlanStatus.ChangeSetStale);
        if (row.ImportIntentJson is null && !await GenericImportIntake.ValidateAsync(context, row, cancellationToken))
            return new(DurablePlanStatus.ChangeSetStale);
        var correction = row.ImportIntentJson is null ? null : await ImportAdmissionAuthority.CorrectionAsync(context, row.Id, cancellationToken);
        if (row.ImportIntentJson is not null && !await ImportAdmissionAuthority.CanPlanAsync(context, row, correction, cancellationToken))
            return new(DurablePlanStatus.ChangeSetStale);
        if (correction is not null) baseline = null;
        if (row.CorrectiveIntentJson is not null)
            return await CreateCorrectivePlanAsync(context, transaction, row, baseline, actorId, handlers ?? [], cancellationToken);
        if (baseline is not null && baseline.AppliedGeneration != baseline.DesiredGeneration)
            return new(DurablePlanStatus.PlanInputUnavailable);
        if (baseline is not null && !await HasAppliedBaselineProofAsync(context, baseline.Id, baseline.DesiredGeneration, cancellationToken))
            return new(DurablePlanStatus.PlanInputUnavailable);
        var reference = ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
            ManifestRevisionNumber.Create(row.RevisionNumber));
        var revision = await revisions.FindAsync(reference, cancellationToken);
        if (revision is null) return new(DurablePlanStatus.RevisionNotFound);
        var environment = await context.Environments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == row.EnvironmentRecordId, cancellationToken);
        if (environment is null) return new(DurablePlanStatus.ChangeSetStale);
        var spec = revision.Declaration.Spec;
        var import = ResourceImportIntentCodec.Decode(row.ImportIntentJson);
        if (import is not null && (baseline is not null || mode == DeploymentAdmissionMode.Internal ||
            spec.Components.Values.Any(x => x.Workloads.Count > 0))) return new(DurablePlanStatus.ImportNotSupported);
        var internalEffects = mode == DeploymentAdmissionMode.Internal || mode == DeploymentAdmissionMode.Operator && spec.Components.Values.Any(x => x.Workloads.Count > 0);
        var workloadUpdate = await HasWorkloadBaselineAsync(context, baseline, cancellationToken);
        if (workloadUpdate && mode == DeploymentAdmissionMode.ResourceOnly) return new(DurablePlanStatus.UnsupportedShape);
        if (workloadUpdate)
        {
            // Publication/revocation writers lock this aggregate, too. Freeze one
            // coherent baseline inventory until the immutable Plan is stored.
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_deployments WHERE id = {baseline!.Id} FOR SHARE", cancellationToken);
            baseline = await context.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == baseline.Id, cancellationToken);
            if (!Matches(row, baseline) || baseline.AppliedGeneration != baseline.DesiredGeneration) return new(DurablePlanStatus.PlanInputUnavailable);
        }
        if (!internalEffects && !DurableActionAdmission.SupportsPilotShape(spec)) return new(DurablePlanStatus.UnsupportedShape);
        if (internalEffects && (!spec.Components.Values.Any(x => x.Workloads.Count > 0) ||
            baseline is null && !row.ExpectedAbsence ||
            baseline is not null && spec.Resources.Count == 0 && !workloadUpdate))
            return new(DurablePlanStatus.UnsupportedShape);
        var emptyStage = internalEffects && spec.Resources.Count == 0;
        ProviderConfigurationRevisionRow? revisionRow = null;
        ExactProviderResolution[] resourceProviders = [];
        var environmentId = EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString());
        if (!emptyStage)
        {
            // Every Resource keeps its exact Provider; initial observation composition uses the same capability admission.
            var acquisitions = ResourceAcquisitionIntentCodec.Decode(row.AcquisitionIntentJson ?? throw new InvalidDataException());
            if (DurableActionAdmission.SingleAlias(spec) is null && (baseline is null
                ? !SupportsInitialObservationComposition(spec, acquisitions)
                : !acquisitions.Resources.Any(x => x.Mode == ResourceAcquisitionMode.Reference)))
                return new(DurablePlanStatus.UnsupportedShape);
            var capturedProviders = await CaptureResourceProvidersAsync(context, revision.Declaration, environmentId, cancellationToken);
            if (capturedProviders.Status != DurablePlanStatus.Planned) return new(capturedProviders.Status);
            revisionRow = capturedProviders.Revision;
            resourceProviders = capturedProviders.Resources;
        }
        ExactProviderResolutionSet providers;
        try
        {
            providers = internalEffects ? await CaptureInternalProvidersAsync(context, revision.Declaration, environmentId, resourceProviders, cancellationToken)
                : new ExactProviderResolutionSet(revision.Declaration, environmentId, resourceProviders);
        }
        catch (InvalidDataException) { return new(DurablePlanStatus.PlanInputUnavailable); }
        var inputs = new DurablePlanningInputs(environmentId, EnvironmentId.Create(environment.EnvironmentId), reference, providers) { Import = import, Acquisitions = ResourceAcquisitionIntentCodec.Decode(row.AcquisitionIntentJson ?? throw new InvalidDataException()) };
        // Describe supported Resource producers before capturing the complete configuration.
        // Only graph planning uses the final Resource-stage view; accepted inputs stay complete.
        DurablePlanStatus admitted = DurablePlanStatus.Planned;
        var resourceInputs = inputs;
        try
        {
            if (internalEffects) resourceInputs = InternalResourceStage.Structure(inputs);
            admitted = emptyStage ? DurablePlanStatus.Planned : await AdmitAsync(context, internalEffects ? resourceInputs.Providers.Declaration.Spec : spec, revisionRow!, resourceInputs, cancellationToken);
            var producers = !emptyStage && admitted == DurablePlanStatus.Planned
                ? await DescribeProducersAsync(context, internalEffects ? resourceInputs.Providers.Declaration.Spec : spec, revisionRow!, resourceInputs, cancellationToken) : [];
            var configuration = await ConfigurationPlanningCapture.CaptureAsync(context,
                new(environmentId, inputs.EnvironmentKey), reference, revision.Declaration, cancellationToken, _rendering, producers: producers);
            if (configuration.Snapshot is null)
            {
                var report = configuration.Expansion;
                if (admitted != DurablePlanStatus.Planned)
                {
                    // No producer inventory exists for a refused shape. Preserve configuration
                    // diagnostics before admission without reporting synthetic missing producers.
                    var errors = report.Diagnostics.Where(d => d.Code != "render.producer.unavailable").ToArray();
                    if (errors.Length == 0) return new(admitted);
                    report = new([], errors);
                }
                return new(DurablePlanStatus.ConfigurationRefused, ConfigurationReport: report);
            }
            if (admitted != DurablePlanStatus.Planned) return new(admitted);
            inputs = inputs with { Configuration = configuration.Snapshot };
            if (internalEffects && !await AdmitInternalWorkloadsAsync(context, inputs, revision.ApplicationVersion.Value, handlers ?? [], cancellationToken))
                return new(DurablePlanStatus.UnsupportedShape);
        }
        catch (InvalidDataException) { return new(DurablePlanStatus.PlanInputUnavailable); }
        resourceInputs = internalEffects ? InternalResourceStage.Project(inputs) : inputs;
        if (inputs.Acquisitions.Resources.Any(x => x.Mode == ResourceAcquisitionMode.Reference) &&
            !(internalEffects && baseline is not null) &&
            internalEffects) return new(DurablePlanStatus.ReferenceNotSupported);
        var resourceSpec = internalEffects ? resourceInputs.Providers.Declaration.Spec : spec;
        DurableOperationGraph graph;
        try
        {
            if (emptyStage)
            {
                var previous = baseline is null ? null : await context.DeploymentGenerations.AsNoTracking()
                    .SingleAsync(x => x.DeploymentId == baseline.Id && x.Generation == baseline.DesiredGeneration, cancellationToken);
                graph = new([], baseline: previous is null ? null : new(ApplicationDeploymentId.Parse(previous.DeploymentId.ToString()), previous.Generation, previous.Fingerprint));
            }
            else graph = baseline is null ? await PlanGraphAsync(context, resourceSpec, revisionRow!, resourceInputs, cancellationToken) :
                await ReuseGraphAsync(context, baseline, resourceSpec, revisionRow!, resourceInputs, cancellationToken)
                    ?? throw new NotSupportedException();
        }
        catch (NotSupportedException) { return new(DurablePlanStatus.UnsupportedShape); }
        catch (InvalidDataException) { return new(DurablePlanStatus.PlanInputUnavailable); }
        if (internalEffects && !InternalResourceStage.IsInitialCreate(inputs, graph) && (graph.Nodes.Count != 0 || graph.RetainedResources.Count != 0 ||
            graph.ReusedResources.Count != spec.Resources.Count)) return new(DurablePlanStatus.UnsupportedShape);
        if (mode == DeploymentAdmissionMode.Operator && internalEffects &&
            !await OperatorWorkloadShapeAsync(context, row, baseline, inputs, graph, cancellationToken))
            return new(DurablePlanStatus.UnsupportedShape);
        if (internalEffects)
        {
            try
            {
                var selected = await SelectInternalDocumentsAsync(context, inputs, revision.ApplicationVersion.Value, handlers ?? [], cancellationToken);
                graph = workloadUpdate ? graph.WithWorkloadUpdate(await CaptureWorkloadUpdateAsync(context, baseline!, inputs, graph, revision.ApplicationVersion.Value, handlers ?? [], selected, cancellationToken)) :
                    graph.WithRetirement(await InitialRetirementAuthority.CaptureAsync(context, inputs, graph, baseline, selected, cancellationToken));
            }
            catch (WorkloadUpdateRefusedException error) { return new(DurablePlanStatus.UnsupportedShape) { UpdateRefusal = error.Reason }; }
            catch (InvalidDataException) { return new(DurablePlanStatus.PlanInputUnavailable); }
        }
        var id = DeploymentPlanId.New();
        var createdAt = clock.GetUtcNow();
        var planRow = new DurablePlanRow
        {
            InternalEffects = internalEffects,
            Id = Guid.Parse(id.ToString()),
            ChangeSetId = row.Id,
            EnvironmentRecordId = row.EnvironmentRecordId,
            EnvironmentKey = environment.EnvironmentId,
            ApplicationRecordId = row.ApplicationRecordId,
            RevisionNumber = row.RevisionNumber,
            SnapshotFormatVersion = DurableInputStorage.Version,
            ProviderInputsJson = ExactProviderResolutionSetCodec.Encode(inputs.Providers),
            GraphJson = DurableOperationGraphCodec.Encode(graph),
            ConfigurationJson = ConfigurationPlanningSnapshotCodec.Encode(inputs.Configuration!),
            Fingerprint = DurableOperationGraphCodec.Fingerprint(inputs, graph),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(Lifetime),
            ActorId = actorId
        };
        context.DurablePlans.Add(planRow);
        if (internalEffects) foreach (var exact in inputs.Providers.Entries.Values)
        {
            var subject = exact.Input switch { ProviderInput.Workload w => "Workload/" + w.Component.Value + "/" + w.Id.Value, ProviderInput.Resource r => "Resource/" + r.Id.Value, _ => throw new InvalidDataException() };
            context.InternalPlanInputs.Add(new() { PlanId = planRow.Id, Subject = subject, ProviderId = Guid.Parse(exact.Instance.ToString()), ConfigurationRevision = exact.Configuration.Value, PlacementId = exact.Placement is { } placement ? InternalPlacementInput.Decode(placement).Id : null });
        }
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
        if (plan.Graph.Correction is not null) return DurablePlanCurrentStatus.PlanInputUnavailable;
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
        var exactResource = plan.Inputs.Providers.Entries.Values.First(x => x.Input is ProviderInput.Resource);
        var exactInstance = Guid.Parse(exactResource.Instance.ToString());
        var exactRevision = exactResource.Configuration.Value;
        var revisionRow = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(r =>
            r.ProviderInstanceId == exactInstance && r.RevisionNumber == exactRevision,
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
        if (row.ImportIntentJson is not null)
        {
            await ImportMutationGate.LockAsync(context, row.EnvironmentRecordId, row.ApplicationRecordId, cancellationToken);
            row = await context.DurableChangeSets.AsNoTracking().SingleAsync(x => x.Id == row.Id, cancellationToken);
        }
        var baseline = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.EnvironmentRecordId == row.EnvironmentRecordId &&
            item.ApplicationRecordId == row.ApplicationRecordId, cancellationToken);
        if (!Matches(row, baseline))
            return (DurablePlanStatus.ChangeSetStale, null);
        if (row.ImportIntentJson is null && !await GenericImportIntake.ValidateAsync(context, row, cancellationToken))
            return (DurablePlanStatus.ChangeSetStale, null);
        var correction = row.ImportIntentJson is null ? null : await ImportAdmissionAuthority.CorrectionAsync(context, row.Id, cancellationToken);
        if (row.ImportIntentJson is not null && !await ImportAdmissionAuthority.CanPlanAsync(context, row, correction, cancellationToken))
            return (DurablePlanStatus.ChangeSetStale, null);
        if (correction is not null) baseline = null;
        if (baseline is not null && baseline.AppliedGeneration != baseline.DesiredGeneration)
            return (DurablePlanStatus.PlanInputUnavailable, null);
        if (baseline is not null && !await HasAppliedBaselineProofAsync(context, baseline.Id, baseline.DesiredGeneration, cancellationToken))
            return (DurablePlanStatus.PlanInputUnavailable, null);
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
            ManifestRevisionNumber.Create(row.RevisionNumber));
        var manifest = await revisions.FindAsync(revision, cancellationToken);
        if (manifest is null) return (DurablePlanStatus.RevisionNotFound, null);
        if (!DurableActionAdmission.SupportsPilotShape(manifest.Declaration.Spec))
            return (DurablePlanStatus.UnsupportedShape, null);
        var environment = await context.Environments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == row.EnvironmentRecordId, cancellationToken);
        if (environment is null) return (DurablePlanStatus.ChangeSetStale, null);
        var environmentId = EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString());
        var acquisitions = ResourceAcquisitionIntentCodec.Decode(row.AcquisitionIntentJson ?? throw new InvalidDataException());
        if (DurableActionAdmission.SingleAlias(manifest.Declaration.Spec) is null && (baseline is null
            ? !SupportsInitialObservationComposition(manifest.Declaration.Spec, acquisitions)
            : !acquisitions.Resources.Any(x => x.Mode == ResourceAcquisitionMode.Reference)))
            return (DurablePlanStatus.UnsupportedShape, null);
        var capturedProviders = await CaptureResourceProvidersAsync(context, manifest.Declaration, environmentId, cancellationToken);
        if (capturedProviders.Status != DurablePlanStatus.Planned) return (capturedProviders.Status, null);
        var providerRevision = capturedProviders.Revision!;
        var inputs = new DurablePlanningInputs(environmentId, EnvironmentId.Create(environment.EnvironmentId), revision,
            new(manifest.Declaration, environmentId, capturedProviders.Resources))
        { Import = ResourceImportIntentCodec.Decode(row.ImportIntentJson), Acquisitions = ResourceAcquisitionIntentCodec.Decode(row.AcquisitionIntentJson ?? throw new InvalidDataException()) };
        var frozen = plan.Inputs.Configuration ?? throw new InvalidDataException();
        if (!manifest.Declaration.Equals(frozen.Manifest)) return (DurablePlanStatus.PlanInputUnavailable, null);
        if (!inputs.Providers.Equals(plan.Inputs.Providers)) return (DurablePlanStatus.Planned, inputs with { Configuration = frozen });
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
        try
        {
            var keys = await ResourceProviderDispatch.CaptureKeysAsync(context, inputs, cancellationToken);
            var imported = (await ImportedProducersAsync(context, inputs, cancellationToken)).Select(x => x.ResourceId).ToHashSet();
            var support = admission.PlanningSupportStatus(spec, keys.Values.First(), inputs, imported, keys);
            if (support != DurablePlanStatus.Planned) return support;
            var exact = await ResourceProviderDispatch.CaptureAsync(context, inputs, _codecs, cancellationToken);
            var representative = exact.Values.First();
            return admission.Admit(spec, representative.Provider, inputs, representative.Configuration, imported, exact);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        { return DurablePlanStatus.ProviderRevisionUnavailable; }
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
            row.CreatedAt, row.ActorId)
        { Correction = CorrectiveIntentCodec.Decode(row.CorrectiveIntentJson), Import = ResourceImportIntentCodec.Decode(row.ImportIntentJson), Acquisitions = ResourceAcquisitionIntentCodec.Decode(row.AcquisitionIntentJson ?? throw new InvalidDataException()) };
    }

    private static DurablePlan ToPlan(DurablePlanRow row, DateTimeOffset? closedAt, string? closedByActorId)
    {
        var inputs = row.Inputs();
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
        var producers = await DescribeProducersAsync(context, spec, revision, inputs, token);
        if (inputs.Configuration is null || inputs.Configuration.RenderPins.Any(p =>
            producers.Count(n => n.ResourceId == p.ResourceLocalId && n.Action == p.ProducerAction) != 1 ||
            !producers.Any(n => n.ResourceId == p.ResourceLocalId && n.Action == p.ProducerAction && n.Id == p.Producer))) return false;
        if (graph.Baseline is { } baseline)
        {
            var owner = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Guid.Parse(baseline.Deployment.ToString()), token);
            if (graph.Correction?.Document is { } correction)
                return owner is not null && owner.EnvironmentRecordId == Guid.Parse(inputs.Environment.ToString()) &&
                    owner.ApplicationRecordId == Guid.Parse(inputs.Revision.ApplicationRecordId.ToString()) && owner.DesiredGeneration == correction.ExpectedDesired &&
                    owner.AppliedGeneration == correction.LastApplied && baseline.Generation == correction.ExpectedDesired && baseline.Fingerprint == correction.OriginalFingerprint &&
                    graph.Nodes.Count == 0 && graph.RetainedResources.Count == 0 && graph.ReusedResources.Count == spec.Resources.Count &&
                    !graph.ReusedResources.Any(x => x.Reintroduced) && await ResourceContinuity.ValidateAsync(context, owner.Id, graph, token);
            return owner is not null && owner.EnvironmentRecordId == Guid.Parse(inputs.Environment.ToString()) && owner.ApplicationRecordId == Guid.Parse(inputs.Revision.ApplicationRecordId.ToString()) &&
                owner.DesiredGeneration == baseline.Generation && owner.AppliedGeneration == baseline.Generation &&
                (await ReuseGraphAsync(context, owner, spec, revision, inputs, token))?.Equals(new DurableOperationGraph(graph.Nodes, graph.ReusedResources, graph.Baseline, graph.RetainedResources)
                { Import = graph.Import, Acquisitions = graph.Acquisitions, ObservationGroups = graph.ObservationGroups }) == true;
        }
        var exact = await ResourceProviderDispatch.CaptureAsync(context, inputs, _codecs, token);
        var representative = exact.Values.First();
        return admission.ValidateGraph(spec, representative.Provider, inputs, representative.Configuration, graph, exact);
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
        if (!await HasAppliedBaselineProofAsync(db, owner.Id, baseline.Generation, token)) throw new InvalidDataException();
        var baselineInputs = baseline.InternalEffects ? InternalResourceStage.Project(baseline.Inputs()) : ResourceContinuity.Inputs(baseline);
        var exact = await ResourceProviderDispatch.CaptureAsync(db, inputs, _codecs, token);
        var representative = exact.Values.First();
        var accepted = baselineInputs.Configuration!;
        var delta = new ResourceDelta(accepted.Manifest.Spec, target);
        var newReference = delta.Entries.Any(x => x.Kind == ResourceDeltaKind.Added && inputs.Acquisitions.Resource(x.Resource).Mode == ResourceAcquisitionMode.Reference);
        if (newReference || DurableActionAdmission.SingleAlias(target) is null)
        {
            if (delta.Entries.Any(x => x.Kind is ResourceDeltaKind.Changed or ResourceDeltaKind.Removed) ||
                delta.Entries.Where(x => x.Kind == ResourceDeltaKind.Added).Any(x => inputs.Acquisitions.Resource(x.Resource).Mode is not (ResourceAcquisitionMode.Reference or ResourceAcquisitionMode.ImportUseAndRetain))) return null;
        }
        var baselineGraph = DurableOperationGraphCodec.Decode(baseline.GraphJson);
        if (baselineGraph.Baseline is not null && !await ResourceContinuity.ValidateAsync(db, owner.Id, baselineGraph, token)) throw new InvalidDataException();
        // Component/Binding membership belongs to the target configuration, not
        // Resource identity. CaptureAsync has expanded the complete target layout,
        // scopes (including absence), pins and provenance; Commit revalidates it.
        // Adapter reuse policies below still own Resource compatibility and targets.
        // The same immutable revision may accept different exact configuration inputs.
        // Commit compares the full captured configuration to preserve exact-input no-ops;
        // revision equality never bypasses Provider, management or original-evidence checks.

        var managedResources = await db.CurrentResourceManagement.AsNoTracking().Where(x => x.Environment == owner.EnvironmentRecordId &&
            x.Application == owner.ApplicationRecordId).ToArrayAsync(token);
        var expected = accepted.Manifest.Spec.Resources.Keys.Select(x => x.Value)
            .Concat(baselineGraph.RetainedResources.Select(x => x.Resource.Value)).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(managedResources.Select(x => x.Resource))) throw new InvalidDataException();
        var reuse = new List<ResourceReuse>();
        var retained = new List<ResourceReuse>();
        foreach (var managed in managedResources.OrderBy(x => x.Resource, StringComparer.Ordinal))
        {
            var resource = ManifestLocalId.Create(managed.Resource);
            if (managed.Deployment != owner.Id) throw new InvalidDataException();
            var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == managed.SourceGeneration, token)
                ?? throw new InvalidDataException();
            var originalInputs = ResourceContinuity.Inputs(source);
            var originalProvider = originalInputs.Providers.Resource(resource);
            var targetProvider = target.Resources.ContainsKey(resource) ? inputs.Providers.Resource(resource) : originalProvider;
            if (target.Resources.ContainsKey(resource) && inputs.Acquisitions.Resource(resource) != originalInputs.Acquisitions.Resource(resource)) return null;
            if (originalProvider.Instance != targetProvider.Instance || originalProvider.Configuration != targetProvider.Configuration ||
                originalProvider.Implementation != targetProvider.Implementation || originalProvider.Alias != targetProvider.Alias) return null;
            var sourceContexts = await ResourceProviderDispatch.CaptureAsync(db, originalInputs, _codecs, token);
            var adapter = sourceContexts[resource].Provider;
            var configuration = sourceContexts[resource].Configuration;
            var evidence = await ResourceContinuity.EvidenceAsync(db, managed, token) ?? throw new InvalidDataException();
            var original = DurableOperationGraphCodec.Decode(source.GraphJson);
            // Missing registrations cannot turn a lost mandatory terminal output into
            // an action that supposedly never required confirmed evidence.
            if (!admission.Supports(original)) throw new InvalidDataException();
            var originalResourceInputs = source.InternalEffects ? InternalResourceStage.Project(originalInputs) : originalInputs;
            var originalResourceGraph = source.InternalEffects ? InternalResourceStage.Project(original) : original;
            if (!admission.ValidateCreationGraph(originalResourceInputs.Configuration!.Manifest.Spec, adapter, originalResourceInputs, configuration,
                originalResourceGraph, sourceContexts)) throw new InvalidDataException();
            var proposedOriginal = originalResourceInputs with
            {
                Providers = new(originalResourceInputs.Providers.Declaration, originalResourceInputs.Environment,
                originalResourceInputs.Providers.Entries.Values.Select(x => x.Alias == originalProvider.Alias && x.Instance == originalProvider.Instance &&
                    x.Configuration == originalProvider.Configuration && x.Implementation == originalProvider.Implementation ? x with { Naming = targetProvider.Naming } : x))
            };
            if (!admission.SameResourceNames(originalResourceInputs.Configuration!.Manifest.Spec, resource, adapter, originalResourceInputs, proposedOriginal, configuration)) return null;
            if (target.Resources.ContainsKey(resource))
            {
                if (managed.Authority == ResourceLifecycleAuthority.UseAndRetain)
                {
                    var a = originalInputs.Configuration!.Manifest.Spec.Resources[resource];
                    var b = target.Resources[resource];
                    if (a.Type != b.Type || a.ProviderRef != b.ProviderRef || a.DeletionPolicy != b.DeletionPolicy || !a.Properties.Equals(b.Properties)) return null;
                }
                if (!admission.CanReuseResource(originalResourceInputs.Configuration.Manifest.Spec, target, resource, adapter, inputs, configuration, originalResourceInputs)) return null;
                reuse.Add(new(resource, managed.Id, managed.SourceGeneration, evidence)
                { Reintroduced = baselineGraph.RetainedResources.Any(x => x.Resource == resource) });
            }
            else
            {
                if (!admission.CanRetainResource(originalResourceInputs.Configuration.Manifest.Spec, target, resource, adapter, inputs, configuration, originalResourceInputs)) return null;
                retained.Add(new(resource, managed.Id, managed.SourceGeneration, evidence));
            }
        }
        foreach (var added in target.Resources.Keys.Where(k => !expected.Contains(k.Value)))
            if (!admission.CanAddResource(accepted.Manifest.Spec, target, added, exact[added].Provider, inputs, exact[added].Configuration)) return null;
        var result = admission.PlanUpdate(target, representative.Provider, inputs, representative.Configuration, reuse,
            new(ApplicationDeploymentId.Parse(owner.Id.ToString()), baseline.Generation, baseline.Fingerprint), retained, exact);
        if (!await ResourceContinuity.ValidateOutputsAsync(db, owner.Id, result, outputContracts ?? [], _codecs, admission, inputs, token)) throw new InvalidDataException();
        return result;
    }
    // Reuse describes original imported producers without requiring Provision support
    // or planning a second import against target inputs with no initial import intent.
    private async Task<IReadOnlyList<DurableOperationNode>> ImportedProducersAsync(ManifestCatalogDbContext db,
        DurablePlanningInputs inputs, CancellationToken token)
    {
        if (inputs.Import is not null) return [];
        var environment = Guid.Parse(inputs.Environment.ToString());
        var application = Guid.Parse(inputs.Revision.ApplicationRecordId.ToString());
        var resources = inputs.Providers.Declaration.Spec.Resources.Keys.Select(x => x.Value).ToArray();
        var managed = await db.CurrentResourceManagement.AsNoTracking().Where(x => x.Environment == environment &&
            x.Application == application && x.Origin == ResourceOrigin.Imported && resources.Contains(x.Resource)).ToArrayAsync(token);
        var producers = new List<DurableOperationNode>();
        foreach (var group in managed.GroupBy(x => new { x.Deployment, x.SourceGeneration }))
        {
            var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x =>
                x.DeploymentId == group.Key.Deployment && x.Generation == group.Key.SourceGeneration, token)
                ?? throw new InvalidDataException("Imported Resource source is unavailable.");
            var original = source.Inputs();
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            if (!admission.Supports(graph) || group.Any(x => !ResourceContinuity.HasAcquisitionAuthority(x, original)))
                throw new InvalidDataException("Imported Resource authority is unavailable.");
            foreach (var resource in group)
            {
                var nodes = graph.Nodes.Where(x => x.ResourceId.Value == resource.Resource).ToArray();
                if (nodes.Length == 0) throw new InvalidDataException("Imported Resource producer is unavailable.");
                producers.AddRange(nodes);
            }
        }
        return producers;
    }

    private async Task<IReadOnlyList<DurableOperationNode>> DescribeProducersAsync(ManifestCatalogDbContext context,
        NormalizedManifestSpec spec, ProviderConfigurationRevisionRow revision, DurablePlanningInputs inputs, CancellationToken token)
    {
        var exact = await ResourceProviderDispatch.CaptureAsync(context, inputs, _codecs, token);
        var representative = exact.Values.First();
        var imported = await ImportedProducersAsync(context, inputs, token);
        return admission.DescribeProducers(spec, representative.Provider, inputs, representative.Configuration,
            imported.Select(x => x.ResourceId).ToHashSet(), exact).Concat(imported).ToArray();
    }

    internal async Task<DurableOperationGraph> PlanGraphAsync(ManifestCatalogDbContext context,
        NormalizedManifestSpec spec, ProviderConfigurationRevisionRow revision, DurablePlanningInputs inputs,
        CancellationToken cancellationToken)
    {
        var exact = await ResourceProviderDispatch.CaptureAsync(context, inputs, _codecs, cancellationToken);
        var representative = exact.Values.First();
        return admission.Plan(spec, representative.Provider, inputs, representative.Configuration, exact);
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
