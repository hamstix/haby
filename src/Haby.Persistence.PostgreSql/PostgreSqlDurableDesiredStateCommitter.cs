using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.Persistence.PostgreSql;

/// <summary>Accepts only an initial desired generation; external Provider work is separate.</summary>
public sealed class PostgreSqlDurableDesiredStateCommitter(
    IDbContextFactory<ManifestCatalogDbContext> contexts,
    PostgreSqlDurableDeploymentPlanner planner,
    TimeProvider clock) : IDurableDesiredStateCommitter
{
    public async ValueTask<DurableCommitResult> CommitAsync(DeploymentChangeSetId changeSetId,
        DeploymentPlanId planId, string actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSetId);
        ArgumentNullException.ThrowIfNull(planId);
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actorId);
        cancellationToken.ThrowIfCancellationRequested();
        var changeId = Guid.Parse(changeSetId.ToString());
        var requestedPlanId = Guid.Parse(planId.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // A row lock serializes retries of one ChangeSet. The advisory transaction lock
        // serializes different ExpectedAbsence ChangeSets for the same deployment key,
        // including the interval before the deployment row exists. The unique index is
        // a second database backstop. READ COMMITTED gives each post-lock read a fresh view.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM deployment_change_sets WHERE id = {changeId} FOR UPDATE", cancellationToken);
        var change = await context.DurableChangeSets.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == changeId, cancellationToken);
        if (change is null) return new DurableCommitResult.ChangeSetNotFound();
        var lockKey = $"haby-deployment:{change.EnvironmentRecordId:N}:{change.ApplicationRecordId:N}";
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var prior = await context.ChangeSetCommits.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ChangeSetId == changeId, cancellationToken);
        if (prior is not null) return new DurableCommitResult.Committed(ToCommit(prior));

        var plan = await context.DurablePlans.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == requestedPlanId, cancellationToken);
        if (plan is null) return new DurableCommitResult.PlanNotFound();
        if (plan.ChangeSetId != changeId) return new DurableCommitResult.PlanDoesNotBelongToChangeSet();
        // The closure insert takes a foreign-key KEY SHARE lock on this Plan;
        // FOR UPDATE orders closure against the commit's closure check.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM deployment_plans WHERE id = {requestedPlanId} FOR UPDATE", cancellationToken);
        if (clock.GetUtcNow() >= plan.ExpiresAt) return new DurableCommitResult.Expired();
        if (await context.DurablePlanClosures.AsNoTracking().AnyAsync(row => row.PlanId == requestedPlanId,
            cancellationToken)) return new DurableCommitResult.Closed();
        if (!change.ExpectedAbsence) return new DurableCommitResult.UnsupportedPrecondition();
        var actual = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(row =>
            row.EnvironmentRecordId == change.EnvironmentRecordId &&
            row.ApplicationRecordId == change.ApplicationRecordId, cancellationToken);
        if (actual is not null)
            return new DurableCommitResult.ChangeSetStale(DeploymentGeneration.Create(actual.DesiredGeneration));

        // FOR SHARE conflicts with the alias store's UPDATE. If a switch wins,
        // this read sees the new binding; if commit wins, the switch waits until
        // the immutable generation has pinned the old exact revision.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT environment_record_id FROM provider_aliases WHERE environment_record_id = {change.EnvironmentRecordId} AND alias = {plan.ProviderAlias} FOR SHARE",
            cancellationToken);
        var alias = await context.ProviderAliases.AsNoTracking().SingleOrDefaultAsync(row =>
            row.EnvironmentRecordId == change.EnvironmentRecordId && row.Alias == plan.ProviderAlias,
            cancellationToken);
        if (alias is null) return new DurableCommitResult.PlanInputUnavailable();
        if (alias.ProviderInstanceId != plan.ProviderInstanceId ||
            alias.RevisionNumber != plan.ConfigurationRevisionNumber)
            return new DurableCommitResult.PlanStale();
        var revision = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(row =>
            row.ProviderInstanceId == plan.ProviderInstanceId &&
            row.RevisionNumber == plan.ConfigurationRevisionNumber &&
            row.EnvironmentRecordId == change.EnvironmentRecordId && row.IsSealed, cancellationToken);
        if (revision is null || revision.ImplementationId != plan.ImplementationId)
            return new DurableCommitResult.PlanInputUnavailable();
        var environment = await context.Environments.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == change.EnvironmentRecordId, cancellationToken);
        if (environment is null) return new DurableCommitResult.PlanInputUnavailable();
        var inputs = new DurablePlanningInputs(EnvironmentRecordId.Parse(change.EnvironmentRecordId.ToString()),
            EnvironmentId.Create(environment.EnvironmentId),
            ManifestRevisionRef.Create(ApplicationRecordId.Parse(change.ApplicationRecordId.ToString()),
                ManifestRevisionNumber.Create(change.RevisionNumber)), ProviderAlias.Create(plan.ProviderAlias),
            ProviderInstanceId.Parse(plan.ProviderInstanceId.ToString()),
            ProviderConfigurationRevisionNumber.Create(plan.ConfigurationRevisionNumber), plan.ImplementationId);
        DurableOperationGraph graph;
        try
        {
            if (plan.SnapshotFormatVersion != 2) return new DurableCommitResult.PlanInputUnavailable();
            inputs = inputs with { Configuration = ConfigurationPlanningSnapshotCodec.Decode(plan.ConfigurationJson, inputs.Revision) };
            graph = DurableOperationGraphCodec.Decode(plan.GraphJson); graph.ValidateReferences(inputs);
        }
        catch (InvalidDataException) { return new DurableCommitResult.PlanInputUnavailable(); }
        if (plan.EnvironmentRecordId != change.EnvironmentRecordId ||
            plan.ApplicationRecordId != change.ApplicationRecordId || plan.RevisionNumber != change.RevisionNumber ||
            plan.EnvironmentKey != inputs.EnvironmentKey.Value ||
            plan.Fingerprint != DurableOperationGraphCodec.Fingerprint(inputs, graph))
            return new DurableCommitResult.PlanInputUnavailable();

        IReadOnlyList<ConfigurationPlanningScope> scopes;
        try
        {
            scopes = await ConfigurationPlanningCapture.ReadScopesAsync(context,
                new(inputs.Environment, inputs.EnvironmentKey), inputs.Configuration!.Manifest, cancellationToken);
        }
        catch (InvalidDataException) { return new DurableCommitResult.PlanInputUnavailable(); }
        if (!ConfigurationPlanningCapture.SameScopes(scopes, inputs.Configuration.Scopes)) return new DurableCommitResult.PlanStale();

        var manifestRow = await context.Revisions.AsNoTracking().SingleOrDefaultAsync(row =>
            row.ApplicationRecordId == change.ApplicationRecordId &&
            row.LocalRevisionNumber == change.RevisionNumber, cancellationToken);
        if (manifestRow is null) return new DurableCommitResult.PlanInputUnavailable();
        NormalizedApplicationManifest declaration;
        try
        {
            declaration = NormalizedSnapshotCodec.Decode(manifestRow.Snapshot, manifestRow.InputApiVersion,
                manifestRow.NormalizedModelVersion, manifestRow.ApplicationId, manifestRow.SnapshotFormatVersion);
        }
        catch (InvalidDataException) { return new DurableCommitResult.PlanInputUnavailable(); }
        var spec = declaration.Spec;
        if (!DurableActionAdmission.SupportsPilotShape(spec)) return new DurableCommitResult.UnsupportedShape();
        if (DurableActionAdmission.SingleAlias(spec)?.Value != plan.ProviderAlias)
            return new DurableCommitResult.PlanStale();
        var admitted = await planner.AdmitAsync(context, spec, revision, inputs, cancellationToken);
        if (admitted == DurablePlanStatus.UnsupportedShape) return new DurableCommitResult.UnsupportedShape();
        if (admitted != DurablePlanStatus.Planned) return new DurableCommitResult.PlanInputUnavailable();

        if (!planner.SupportsRendering(inputs.Configuration) || !declaration.Equals(inputs.Configuration.Manifest)) return new DurableCommitResult.PlanInputUnavailable();
        try
        {
            var captured = await ConfigurationPlanningCapture.CaptureAsync(context,
                new(inputs.Environment, inputs.EnvironmentKey), inputs.Revision, declaration, cancellationToken, ConfigurationRenderingCatalog.Empty, inputs.Configuration.RenderPins);
            if (captured.Snapshot is null || !captured.Snapshot.Equals(inputs.Configuration)) return new DurableCommitResult.PlanInputUnavailable();
        }
        catch (InvalidDataException) { return new DurableCommitResult.PlanInputUnavailable(); }

        try
        {
            if (!await planner.ValidateGraphAsync(context, spec, revision, inputs, graph, cancellationToken))
                return new DurableCommitResult.PlanInputUnavailable();
        }
        catch (InvalidDataException) { return new DurableCommitResult.PlanInputUnavailable(); }

        cancellationToken.ThrowIfCancellationRequested();
        var deploymentId = ApplicationDeploymentId.New();
        var deploymentGuid = Guid.Parse(deploymentId.ToString());
        var committedAt = clock.GetUtcNow();
        context.ApplicationDeployments.Add(new ApplicationDeploymentRow
        {
            Id = deploymentGuid,
            EnvironmentRecordId = change.EnvironmentRecordId,
            ApplicationRecordId = change.ApplicationRecordId,
            DesiredGeneration = 1,
            AppliedGeneration = null,
            ConcurrencyToken = 1
        });
        context.DeploymentGenerations.Add(new DeploymentGenerationRow
        {
            DeploymentId = deploymentGuid,
            Generation = 1,
            Presence = nameof(DesiredPresence.Present),
            ChangeSetId = changeId,
            PlanId = requestedPlanId,
            ApplicationRecordId = change.ApplicationRecordId,
            RevisionNumber = change.RevisionNumber,
            EnvironmentRecordId = change.EnvironmentRecordId,
            EnvironmentKey = inputs.EnvironmentKey.Value,
            ProviderAlias = plan.ProviderAlias,
            ProviderInstanceId = plan.ProviderInstanceId,
            ConfigurationRevisionNumber = plan.ConfigurationRevisionNumber,
            ImplementationId = plan.ImplementationId,
            Fingerprint = plan.Fingerprint,
            GraphJson = plan.GraphJson,
            ConfigurationJson = plan.ConfigurationJson,
            AcceptedAt = committedAt,
            ActorId = actorId
        });
        var commit = new ChangeSetCommitRow
        {
            ChangeSetId = changeId,
            PlanId = requestedPlanId,
            DeploymentId = deploymentGuid,
            Generation = 1,
            CommittedAt = committedAt,
            ActorId = actorId
        };
        context.ChangeSetCommits.Add(commit);
        foreach (var node in graph.Nodes)
            context.DurableOperations.Add(new DurableOperationRow
            {
                DeploymentId = deploymentGuid,
                Generation = 1,
                OperationId = node.Id.Value,
                State = nameof(DurableOperationState.Pending),
                CreatedAt = committedAt
            });
        await context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return new DurableCommitResult.Committed(ToCommit(commit));
    }

    public async ValueTask<DurableChangeSetCommit?> FindCommitAsync(DeploymentChangeSetId changeSetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSetId);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var id = Guid.Parse(changeSetId.ToString());
        var row = await context.ChangeSetCommits.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ChangeSetId == id, cancellationToken);
        return row is null ? null : ToCommit(row);
    }

    public async ValueTask<DurableDesiredGeneration?> FindGenerationAsync(ApplicationDeploymentId deploymentId,
        DeploymentGeneration generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deploymentId);
        ArgumentNullException.ThrowIfNull(generation);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var id = Guid.Parse(deploymentId.ToString());
        var row = await context.DeploymentGenerations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.DeploymentId == id && item.Generation == generation.Value,
                cancellationToken);
        if (row is null) return null;
        if (row.Presence != nameof(DesiredPresence.Present))
            throw new InvalidDataException("Generation presence is unsupported.");
        var inputs = new DurablePlanningInputs(EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString()),
            EnvironmentId.Create(row.EnvironmentKey),
            ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
                ManifestRevisionNumber.Create(row.RevisionNumber)), ProviderAlias.Create(row.ProviderAlias),
            ProviderInstanceId.Parse(row.ProviderInstanceId.ToString()),
            ProviderConfigurationRevisionNumber.Create(row.ConfigurationRevisionNumber), row.ImplementationId);
        inputs = inputs with { Configuration = ConfigurationPlanningSnapshotCodec.Decode(row.ConfigurationJson, inputs.Revision) };
        var graph = DurableOperationGraphCodec.Decode(row.GraphJson);
        if (row.Fingerprint != DurableOperationGraphCodec.Fingerprint(inputs, graph))
            throw new InvalidDataException("Generation snapshot is inconsistent.");
        return new(deploymentId, generation, DesiredPresence.Present, inputs, DeploymentPlanId.Parse(row.PlanId.ToString()),
            DeploymentChangeSetId.Parse(row.ChangeSetId.ToString()), row.AcceptedAt, row.ActorId)
        { Graph = graph };
    }

    private static DurableChangeSetCommit ToCommit(ChangeSetCommitRow row) => new(
        DeploymentChangeSetId.Parse(row.ChangeSetId.ToString()), DeploymentPlanId.Parse(row.PlanId.ToString()),
        ApplicationDeploymentId.Parse(row.DeploymentId.ToString()), DeploymentGeneration.Create(row.Generation),
        row.CommittedAt, row.ActorId);
}

public static class DurableDesiredStateCommitterServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDurableDesiredStateCommitter(this IServiceCollection services)
    {
        services.AddPostgreSqlDurableDeploymentPlanner();
        services.AddPostgreSqlDurableOperationQuery();
        services.TryAddSingleton<IDurableDesiredStateCommitter, PostgreSqlDurableDesiredStateCommitter>();
        return services;
    }
}
