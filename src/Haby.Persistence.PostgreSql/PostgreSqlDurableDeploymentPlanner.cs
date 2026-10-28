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

/// <summary>Durable, side-effect-free one-database planning. It never accepts desired state.</summary>
public sealed class PostgreSqlDurableDeploymentPlanner(
    IDbContextFactory<ManifestCatalogDbContext> contexts, IManifestRevisionQuery revisions,
    IProviderAliasResolver aliases, IEnumerable<IProviderConfigurationCodec> codecs,
    ICheckpointProviderCapability capability, TimeProvider clock)
    : IDurableDeploymentPlanner
{
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
        if (precondition is DeploymentPrecondition.ExpectedGeneration)
            return new(DurableChangeSetCreateStatus.ExpectedGenerationUnsupported);
        if (precondition is not DeploymentPrecondition.ExpectedAbsence)
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
            ExpectedAbsence = true,
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
        if (await context.ApplicationDeployments.AsNoTracking().AnyAsync(item =>
            item.EnvironmentRecordId == row.EnvironmentRecordId &&
            item.ApplicationRecordId == row.ApplicationRecordId, cancellationToken))
            return new(DurablePlanStatus.ChangeSetStale);
        // Defensive until durable deployments and ExpectedGeneration are implemented.
        if (!row.ExpectedAbsence) return new(DurablePlanStatus.ChangeSetStale);
        var reference = ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
            ManifestRevisionNumber.Create(row.RevisionNumber));
        var revision = await revisions.FindAsync(reference, cancellationToken);
        if (revision is null) return new(DurablePlanStatus.RevisionNotFound);
        var environment = await context.Environments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == row.EnvironmentRecordId, cancellationToken);
        if (environment is null) return new(DurablePlanStatus.ChangeSetStale);
        var spec = revision.Declaration.Spec;
        if (!CheckpointDatabaseShape.IsSupported(spec)) return new(DurablePlanStatus.UnsupportedShape);
        var resource = spec.Resources.Single().Value;
        var environmentId = EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString());
        var resolved = await aliases.ResolveAsync(environmentId, resource.ProviderRef, cancellationToken);
        if (resolved is null) return new(DurablePlanStatus.AliasNotResolved);
        if (!capability.Supports(new ProviderResolution(resolved.Binding.InstanceId.ToString(),
            resolved.Binding.RevisionNumber.Value, resolved.ImplementationId), resource.Type))
            return new(DurablePlanStatus.ProviderCapabilityMismatch);
        var revisionRow = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ProviderInstanceId == Guid.Parse(resolved.Binding.InstanceId.ToString()) &&
            item.RevisionNumber == resolved.Binding.RevisionNumber.Value &&
            item.EnvironmentRecordId == row.EnvironmentRecordId && item.IsSealed, cancellationToken);
        if (revisionRow is null || revisionRow.ImplementationId != resolved.ImplementationId ||
            !await CodecAvailableAsync(context, revisionRow, cancellationToken))
            return new(DurablePlanStatus.ProviderRevisionUnavailable);
        var inputs = new DurablePlanningInputs(environmentId, EnvironmentId.Create(environment.EnvironmentId),
            reference, resource.ProviderRef, resolved.Binding.InstanceId, resolved.Binding.RevisionNumber,
            resolved.ImplementationId);
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
            SnapshotFormatVersion = 1,
            Fingerprint = Fingerprint(inputs),
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

    internal static bool IsSupportedDatabaseResource(NormalizedResource resource) =>
        CheckpointDatabaseShape.IsSupportedResource(resource);

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
        var plan = await FindPlanAsync(id, cancellationToken);
        if (plan is null) return DurablePlanCurrentStatus.PlanNotFound;
        if (clock.GetUtcNow() >= plan.ExpiresAt) return DurablePlanCurrentStatus.Expired;
        if (plan.ClosedAt is not null) return DurablePlanCurrentStatus.Closed;
        (DurablePlanStatus Status, DurablePlanningInputs? Inputs) result;
        try { result = await CreateCurrentInputsAsync(plan.ChangeSetId, cancellationToken); }
        catch (InvalidDataException) { return DurablePlanCurrentStatus.PlanInputUnavailable; }
        if (result.Status == DurablePlanStatus.ChangeSetStale) return DurablePlanCurrentStatus.ChangeSetStale;
        if (result.Status != DurablePlanStatus.Planned || result.Inputs is null)
            return DurablePlanCurrentStatus.PlanInputUnavailable;
        if (result.Inputs != plan.Inputs || Fingerprint(result.Inputs) != plan.Fingerprint)
            return DurablePlanCurrentStatus.PlanStale;
        return DurablePlanCurrentStatus.Current;
    }

    private async ValueTask<(DurablePlanStatus Status, DurablePlanningInputs? Inputs)> CreateCurrentInputsAsync(
        DeploymentChangeSetId id, CancellationToken cancellationToken)
    {
        // Re-evaluate without writing a replacement Plan.
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == Guid.Parse(id.ToString()), cancellationToken);
        if (row is null || !row.ExpectedAbsence) return (DurablePlanStatus.ChangeSetStale, null);
        if (await context.ApplicationDeployments.AsNoTracking().AnyAsync(item =>
            item.EnvironmentRecordId == row.EnvironmentRecordId &&
            item.ApplicationRecordId == row.ApplicationRecordId, cancellationToken))
            return (DurablePlanStatus.ChangeSetStale, null);
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
            ManifestRevisionNumber.Create(row.RevisionNumber));
        var manifest = await revisions.FindAsync(revision, cancellationToken);
        if (manifest is null) return (DurablePlanStatus.RevisionNotFound, null);
        if (manifest.Declaration.Spec.Resources.Count != 1)
            return (DurablePlanStatus.UnsupportedShape, null);
        var alias = manifest.Declaration.Spec.Resources.Single().Value.ProviderRef;
        var environment = await context.Environments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == row.EnvironmentRecordId, cancellationToken);
        if (environment is null) return (DurablePlanStatus.ChangeSetStale, null);
        var environmentId = EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString());
        var resolution = await aliases.ResolveAsync(environmentId, alias, cancellationToken);
        if (resolution is null) return (DurablePlanStatus.AliasNotResolved, null);
        if (!capability.Supports(new ProviderResolution(resolution.Binding.InstanceId.ToString(),
            resolution.Binding.RevisionNumber.Value, resolution.ImplementationId), PostgreSqlDatabaseContract.Id))
            return (DurablePlanStatus.ProviderCapabilityMismatch, null);
        var providerRevision = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ProviderInstanceId == Guid.Parse(resolution.Binding.InstanceId.ToString()) &&
            item.RevisionNumber == resolution.Binding.RevisionNumber.Value &&
            item.EnvironmentRecordId == row.EnvironmentRecordId && item.IsSealed, cancellationToken);
        if (providerRevision is null || providerRevision.ImplementationId != resolution.ImplementationId ||
            !await CodecAvailableAsync(context, providerRevision, cancellationToken))
            return (DurablePlanStatus.ProviderRevisionUnavailable, null);
        return (DurablePlanStatus.Planned, new DurablePlanningInputs(environmentId,
            EnvironmentId.Create(environment.EnvironmentId), revision, alias, resolution.Binding.InstanceId,
            resolution.Binding.RevisionNumber, resolution.ImplementationId));
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
                prepared.TargetIdentity.FormatVersion == instance.TargetFormatVersion &&
                prepared.TargetIdentity.Value == instance.TargetIdentity &&
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
            new DeploymentChangeSetMember(application, revision, new DeploymentPrecondition.ExpectedAbsence()),
            row.CreatedAt, row.ActorId);
    }

    private static DurablePlan ToPlan(DurablePlanRow row, DateTimeOffset? closedAt, string? closedByActorId)
    {
        if (row.SnapshotFormatVersion != 1) throw new InvalidDataException("Planning snapshot version is unavailable.");
        var application = ApplicationRecordId.Parse(row.ApplicationRecordId.ToString());
        var inputs = new DurablePlanningInputs(EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString()),
            EnvironmentId.Create(row.EnvironmentKey),
            ManifestRevisionRef.Create(application, ManifestRevisionNumber.Create(row.RevisionNumber)),
            ProviderAlias.Create(row.ProviderAlias), ProviderInstanceId.Parse(row.ProviderInstanceId.ToString()),
            ProviderConfigurationRevisionNumber.Create(row.ConfigurationRevisionNumber), row.ImplementationId);
        if (Fingerprint(inputs) != row.Fingerprint) throw new InvalidDataException("Planning snapshot is inconsistent.");
        return new(DeploymentPlanId.Parse(row.Id.ToString()), DeploymentChangeSetId.Parse(row.ChangeSetId.ToString()),
            inputs, row.Fingerprint, row.CreatedAt, row.ExpiresAt, row.ActorId, closedAt, closedByActorId);
    }

    internal static string Fingerprint(DurablePlanningInputs inputs)
    {
        var text = string.Join('\n', "haby-durable-plan-inputs/v1", inputs.Environment.ToString(),
            inputs.EnvironmentKey.Value, inputs.Revision.ApplicationRecordId.ToString(),
            inputs.Revision.RevisionNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inputs.Alias.Value, inputs.ProviderInstance.ToString(),
            inputs.ConfigurationRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inputs.ImplementationId);
        return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
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
        services.TryAddSingleton<PostgreSqlDurableDeploymentPlanner>();
        services.TryAddSingleton<IDurableDeploymentPlanner>(provider =>
            provider.GetRequiredService<PostgreSqlDurableDeploymentPlanner>());
        return services;
    }
}
