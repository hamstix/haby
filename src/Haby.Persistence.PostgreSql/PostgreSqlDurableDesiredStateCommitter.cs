using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.Persistence.PostgreSql;

/// <summary>Atomically accepts initial or bounded additive desired state; execution remains explicit.</summary>
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
        var actual = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(row =>
            row.EnvironmentRecordId == change.EnvironmentRecordId &&
            row.ApplicationRecordId == change.ApplicationRecordId, cancellationToken);
        if (!PostgreSqlDurableDeploymentPlanner.Matches(change, actual))
            return new DurableCommitResult.ChangeSetStale(actual is null ? null : DeploymentGeneration.Create(actual.DesiredGeneration));
        if (actual is not null)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_deployments WHERE id = {actual.Id} FOR UPDATE", cancellationToken);
            actual = await context.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == actual.Id, cancellationToken);
            if (actual.AppliedGeneration != actual.DesiredGeneration) return new DurableCommitResult.PlanInputUnavailable();
            await ResourceContinuity.LockAsync(context, actual.Id, cancellationToken);
        }

        DurablePlanningInputs inputs;
        DurableOperationGraph graph;
        ProviderConfigurationRevisionRow? revision = null;
        try
        {
            inputs = plan.Inputs();
            graph = DurableOperationGraphCodec.Decode(plan.GraphJson);
            var environment = await context.Environments.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == change.EnvironmentRecordId, cancellationToken);
            if (environment is null || environment.EnvironmentId != inputs.EnvironmentKey.Value)
                return new DurableCommitResult.PlanInputUnavailable();
            // Lock all aliases in a stable order. Naming capture shares the same alias lock.
            foreach (var frozen in inputs.Providers.Entries.Values.OrderBy(x => x.Alias.Value, StringComparer.Ordinal))
            {
                var aliasName = frozen.Alias.Value;
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT environment_record_id FROM provider_aliases WHERE environment_record_id = {change.EnvironmentRecordId} AND alias = {aliasName} FOR SHARE", cancellationToken);
                var alias = await context.ProviderAliases.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.EnvironmentRecordId == change.EnvironmentRecordId && x.Alias == aliasName, cancellationToken);
                if (alias is null) return new DurableCommitResult.PlanInputUnavailable();
                if (alias.ProviderInstanceId != Guid.Parse(frozen.Instance.ToString()) || alias.RevisionNumber != frozen.Configuration.Value)
                    return new DurableCommitResult.PlanStale();
                var exactRevision = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ProviderInstanceId == alias.ProviderInstanceId && x.RevisionNumber == alias.RevisionNumber &&
                    x.EnvironmentRecordId == change.EnvironmentRecordId && x.IsSealed, cancellationToken);
                if (exactRevision is null || exactRevision.ImplementationId != frozen.Implementation.Value)
                    return new DurableCommitResult.PlanInputUnavailable();
                var naming = await PostgreSqlResourceNamingStore.CaptureAsync(context, change.EnvironmentRecordId, aliasName, cancellationToken);
                if (naming.Snapshot != frozen.ResourceNaming()) return new DurableCommitResult.PlanStale();
            }
            // Existing executable pilot admits only one exact Resource context. The loop above
            // checks the complete set; this restriction is capability admission, not storage.
            var pilot = inputs.Providers.RequireUniformResourceProvider();
            var instance = Guid.Parse(pilot.Instance.ToString());
            revision = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.ProviderInstanceId == instance && x.RevisionNumber == pilot.Configuration.Value &&
                x.EnvironmentRecordId == change.EnvironmentRecordId && x.IsSealed, cancellationToken);
        }
        catch (InvalidDataException) { return new DurableCommitResult.PlanInputUnavailable(); }
        if (revision is null) return new DurableCommitResult.UnsupportedShape();
        if (actual is null ? graph.Baseline is not null : graph.Baseline is not { } baseline ||
            baseline.Deployment.ToString() != actual.Id.ToString() || baseline.Generation != actual.DesiredGeneration)
            return new DurableCommitResult.PlanInputUnavailable();
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
        if (DurableActionAdmission.SingleAlias(spec) != inputs.Providers.RequireUniformResourceProvider().Alias)
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
                return graph.Baseline is null ? new DurableCommitResult.PlanInputUnavailable() : new DurableCommitResult.PlanStale();
        }
        catch (InvalidDataException) { return new DurableCommitResult.PlanInputUnavailable(); }

        cancellationToken.ThrowIfCancellationRequested();
        var deploymentId = actual is null ? ApplicationDeploymentId.New() : ApplicationDeploymentId.Parse(actual.Id.ToString());
        var deploymentGuid = Guid.Parse(deploymentId.ToString());
        var committedAt = clock.GetUtcNow();
        var generation = actual is null ? 1 : checked(actual.DesiredGeneration + 1);
        var unchanged = false;
        if (actual is not null)
        {
            var previous = await context.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == actual.Id && x.Generation == actual.DesiredGeneration, cancellationToken);
            unchanged = previous.RevisionNumber == plan.RevisionNumber && previous.ConfigurationJson == plan.ConfigurationJson &&
                previous.Inputs().Providers.Equals(inputs.Providers);
            if (unchanged) generation = actual.DesiredGeneration;
            else
            {
                var changed = await context.ApplicationDeployments.Where(x => x.Id == actual.Id && x.DesiredGeneration == actual.DesiredGeneration && x.AppliedGeneration == actual.DesiredGeneration)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.DesiredGeneration, generation).SetProperty(x => x.ConcurrencyToken, x => x.ConcurrencyToken + 1), cancellationToken);
                if (changed != 1) return new DurableCommitResult.ChangeSetStale(DeploymentGeneration.Create(actual.DesiredGeneration));
            }
        }
        if (actual is null)
            context.ApplicationDeployments.Add(new ApplicationDeploymentRow
            {
                Id = deploymentGuid,
                EnvironmentRecordId = change.EnvironmentRecordId,
                ApplicationRecordId = change.ApplicationRecordId,
                DesiredGeneration = 1,
                AppliedGeneration = null,
                ConcurrencyToken = 1
            });
        if (!unchanged) context.DeploymentGenerations.Add(new DeploymentGenerationRow
        {
            DeploymentId = deploymentGuid,
            Generation = generation,
            Presence = nameof(DesiredPresence.Present),
            ChangeSetId = changeId,
            PlanId = requestedPlanId,
            ApplicationRecordId = change.ApplicationRecordId,
            RevisionNumber = change.RevisionNumber,
            EnvironmentRecordId = change.EnvironmentRecordId,
            EnvironmentKey = inputs.EnvironmentKey.Value,
            SnapshotFormatVersion = DurableInputStorage.Version,
            ProviderInputsJson = plan.ProviderInputsJson,
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
            Generation = generation,
            CommittedAt = committedAt,
            ActorId = actorId
        };
        context.ChangeSetCommits.Add(commit);
        if (!unchanged) foreach (var node in graph.Nodes)
            context.DurableOperations.Add(new DurableOperationRow
            {
                DeploymentId = deploymentGuid,
                Generation = generation,
                OperationId = node.Id.Value,
                State = nameof(DurableOperationState.Pending),
                CreatedAt = committedAt
            });
        if (!unchanged) foreach (var resource in spec.Resources.Keys.Where(r => !graph.ReusedResources.Any(x => x.Resource == r)))
            context.ResourceManagement.Add(new()
            {
                Id = Guid.CreateVersion7(),
                Environment = change.EnvironmentRecordId,
                Application = change.ApplicationRecordId,
                Resource = resource.Value,
                Deployment = deploymentGuid,
                SourceGeneration = generation
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
        var inputs = row.Inputs();
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
