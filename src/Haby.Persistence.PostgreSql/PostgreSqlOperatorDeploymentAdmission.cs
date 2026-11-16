using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal enum DeploymentAdmissionMode { ResourceOnly, Internal, Operator }

// The trusted composition owns handlers. No public request can supply an internal capability.
internal sealed class PostgreSqlOperatorDeploymentAdmission(
    PostgreSqlDurableDeploymentPlanner planner, PostgreSqlDurableDesiredStateCommitter committer,
    IDbContextFactory<ManifestCatalogDbContext> contexts, IEnumerable<IInternalActionHandler> registrations) : IOperatorDeploymentAdmission
{
    private readonly IInternalActionHandler[] handlers = Unique(registrations);
    private static IInternalActionHandler[] Unique(IEnumerable<IInternalActionHandler> registrations)
    {
        var values = registrations.ToArray();
        if (values.GroupBy(x => (x.Contract.Subject, x.Contract.ProviderKind, x.Contract.Configuration, x.Contract.Implementation)).Any(x => x.Count() != 1))
            throw new InvalidOperationException("Ambiguous operator admission registration.");
        return values;
    }
    public ValueTask<DurablePlanResult> CreatePlanAsync(DeploymentChangeSetId changeSet, string actor, CancellationToken token) =>
        planner.CreateOperatorPlanAsync(changeSet, actor, token, handlers);
    public ValueTask<DurablePlanCurrentStatus> CheckPlanAsync(DeploymentPlanId plan, CancellationToken token) =>
        planner.CheckOperatorPlanAsync(plan, handlers, token);
    public ValueTask<DurableCommitResult> CommitAsync(DeploymentChangeSetId changeSet, DeploymentPlanId plan, string actor, CancellationToken token) =>
        committer.CommitOperatorAsync(changeSet, plan, actor, token, handlers);

    public async ValueTask<OperatorGenerationStage?> ReadStageAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        var id = Guid.Parse(deployment.ToString());
        var source = await db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == id && x.Generation == generation.Value)
            .Select(x => new { x.InternalEffects }).SingleOrDefaultAsync(token);
        if (source is not { InternalEffects: true }) return null;
        var applied = await db.ApplicationDeployments.AsNoTracking().Where(x => x.Id == id).Select(x => x.AppliedGeneration).SingleAsync(token);
        // Absence is checked across the exact generation, including orphan operation evidence.
        // Never load payloads/receipts, run an observer or infer success from graph node counts.
        var evidence = await db.ActionIntents.AnyAsync(x => x.DeploymentId == id && x.Generation == generation.Value, token) ||
            await db.DurableOperations.AnyAsync(x => x.DeploymentId == id && x.Generation == generation.Value, token) ||
            await db.ActionObservations.AnyAsync(x => x.DeploymentId == id && x.Generation == generation.Value, token);
        return new(applied, evidence ? OperatorWorkloadSubmission.Unknown : OperatorWorkloadSubmission.NotSubmitted);
    }
}

public sealed partial class PostgreSqlDurableDeploymentPlanner
{
    internal ValueTask<DurablePlanResult> CreateOperatorPlanAsync(DeploymentChangeSetId change, string actor, CancellationToken token, IReadOnlyList<IInternalActionHandler> handlers) =>
        CreatePlanCoreAsync(change, actor, DeploymentAdmissionMode.Operator, token, handlers);

    internal static async Task<bool> ValidateOperatorPinsAsync(ManifestCatalogDbContext db, Guid plan, DurablePlanningInputs inputs, CancellationToken token)
    {
        var pins = await db.InternalPlanInputs.AsNoTracking().Where(x => x.PlanId == plan).ToArrayAsync(token);
        return pins.Length == inputs.Providers.Entries.Count && inputs.Providers.Entries.Values.All(exact =>
            pins.Any(pin => pin.Subject == (exact.Input is ProviderInput.Workload w ? "Workload/" + w.Component.Value + "/" + w.Id.Value :
                "Resource/" + ((ProviderInput.Resource)exact.Input).Id.Value) && pin.ProviderId.ToString() == exact.Instance.ToString() &&
                pin.ConfigurationRevision == exact.Configuration.Value && pin.PlacementId == (exact.Placement is { } placement ? InternalPlacementInput.Decode(placement).Id : (Guid?)null)));
    }

    internal async Task<bool> OperatorWorkloadShapeAsync(ManifestCatalogDbContext db, DurableChangeSetRow change,
        ApplicationDeploymentRow? owner, DurablePlanningInputs inputs, DurableOperationGraph graph, CancellationToken token)
    {
        var spec = inputs.Providers.Declaration.Spec;
        if (!spec.Components.Values.Any(x => x.Workloads.Count > 0) || graph.Nodes.Count != 0 || graph.RetainedResources.Count != 0) return false;
        if (owner is null)
            return change.ExpectedAbsence && change.ExpectedGeneration is null && InternalResourceStage.IsEmpty(inputs, graph) &&
                !await db.ResourceManagement.AnyAsync(x => x.Environment == change.EnvironmentRecordId && x.Application == change.ApplicationRecordId, token);
        if (change.ExpectedAbsence || change.ExpectedGeneration != owner.DesiredGeneration || owner.AppliedGeneration != owner.DesiredGeneration ||
            graph.Baseline is not { } baseline || baseline.Deployment.ToString() != owner.Id.ToString() || baseline.Generation != owner.DesiredGeneration ||
            spec.Resources.Count == 0 || graph.ReusedResources.Count != spec.Resources.Count || graph.ReusedResources.Any(x => x.Reintroduced)) return false;
        var previous = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == owner.DesiredGeneration, token);
        if (previous is null || previous.InternalEffects || baseline.Fingerprint != previous.Fingerprint) return false;
        var old = previous.Inputs().Providers.Declaration.Spec;
        var oldGraph = DurableOperationGraphCodec.Decode(previous.GraphJson);
        return old.Components.Values.All(x => x.Workloads.Count == 0) && oldGraph.RetainedResources.Count == 0 &&
            old.Resources.Keys.ToHashSet().SetEquals(spec.Resources.Keys) &&
            graph.ReusedResources.Select(x => x.Resource).ToHashSet().SetEquals(spec.Resources.Keys) &&
            await DurableAggregateCompletion.HasProofAsync(db, owner.Id, owner.DesiredGeneration, token);
    }

    internal async ValueTask<DurablePlanCurrentStatus> CheckOperatorPlanAsync(DeploymentPlanId id, IReadOnlyList<IInternalActionHandler> handlers, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.DurablePlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Guid.Parse(id.ToString()), token);
        if (row is null) return DurablePlanCurrentStatus.PlanNotFound;
        if (!row.InternalEffects) return await CheckPlanAsync(id, token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (clock.GetUtcNow() >= row.ExpiresAt) return DurablePlanCurrentStatus.Expired;
        if (await db.DurablePlanClosures.AnyAsync(x => x.PlanId == row.Id, token)) return DurablePlanCurrentStatus.Closed;
        try
        {
            var plan = ToPlan(row, null, null);
            var change = await db.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ChangeSetId, token);
            var gate = $"haby-deployment:{row.EnvironmentRecordId:N}:{row.ApplicationRecordId:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({gate}, 0))", token);
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == row.EnvironmentRecordId && x.ApplicationRecordId == row.ApplicationRecordId, token);
            if (change is null || !Matches(change, owner) || await db.ChangeSetCommits.AnyAsync(x => x.ChangeSetId == row.ChangeSetId, token)) return DurablePlanCurrentStatus.ChangeSetStale;
            if (change.EnvironmentRecordId != row.EnvironmentRecordId || change.ApplicationRecordId != row.ApplicationRecordId || change.RevisionNumber != row.RevisionNumber) return DurablePlanCurrentStatus.PlanInputUnavailable;
            var inputs = plan.Inputs;
            if (!await ValidateOperatorPinsAsync(db, row.Id, inputs, token)) return DurablePlanCurrentStatus.PlanInputUnavailable;
            if (!await OperatorWorkloadShapeAsync(db, change, owner, inputs, plan.Graph, token)) return DurablePlanCurrentStatus.PlanInputUnavailable;
            var revision = await revisions.FindAsync(inputs.Revision, token);
            var environment = await db.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.EnvironmentRecordId, token);
            if (revision is null || environment?.EnvironmentId != inputs.EnvironmentKey.Value || !revision.Declaration.Equals(inputs.Providers.Declaration)) return DurablePlanCurrentStatus.PlanInputUnavailable;
            foreach (var exact in inputs.Providers.Entries.Values.OrderBy(x => x.Alias.Value, StringComparer.Ordinal))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT environment_record_id FROM provider_aliases WHERE environment_record_id = {row.EnvironmentRecordId} AND alias = {exact.Alias.Value} FOR SHARE", token);
                var alias = await db.ProviderAliases.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == row.EnvironmentRecordId && x.Alias == exact.Alias.Value, token);
                if (alias is null) return DurablePlanCurrentStatus.PlanInputUnavailable;
                if (alias.ProviderInstanceId.ToString() != exact.Instance.ToString() || alias.RevisionNumber != exact.Configuration.Value) return DurablePlanCurrentStatus.PlanStale;
                if (exact.Input is ProviderInput.Resource && (await PostgreSqlResourceNamingStore.CaptureAsync(db, row.EnvironmentRecordId, exact.Alias.Value, token)).Snapshot != exact.ResourceNaming()) return DurablePlanCurrentStatus.PlanStale;
            }
            if (!await ValidateInternalProvidersAsync(db, inputs, token)) return DurablePlanCurrentStatus.PlanInputUnavailable;
            var frozen = inputs.Configuration!;
            var scopes = await ConfigurationPlanningCapture.ReadScopesAsync(db, new(inputs.Environment, inputs.EnvironmentKey), frozen.Manifest, token);
            if (!ConfigurationPlanningCapture.SameScopes(scopes, frozen.Scopes)) return DurablePlanCurrentStatus.PlanStale;
            if (!SupportsRendering(frozen)) return DurablePlanCurrentStatus.PlanInputUnavailable;
            var capture = await ConfigurationPlanningCapture.CaptureAsync(db, new(inputs.Environment, inputs.EnvironmentKey), inputs.Revision, frozen.Manifest, token, _rendering, frozen.RenderPins);
            if (capture.Snapshot is null || !capture.Snapshot.Equals(frozen) || !await AdmitInternalWorkloadsAsync(db, inputs, revision.ApplicationVersion.Value, handlers, token)) return DurablePlanCurrentStatus.PlanInputUnavailable;
            if (!InternalResourceStage.IsEmpty(inputs, plan.Graph))
            {
                var resource = InternalResourceStage.Project(inputs);
                var exact = resource.Providers.RequireUniformResourceProvider();
                var provider = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == Guid.Parse(exact.Instance.ToString()) && x.RevisionNumber == exact.Configuration.Value, token);
                if (provider is null || await AdmitAsync(db, resource.Providers.Declaration.Spec, provider, resource, token) != DurablePlanStatus.Planned ||
                    !await ValidateGraphAsync(db, resource.Providers.Declaration.Spec, provider, resource, plan.Graph, token)) return DurablePlanCurrentStatus.PlanInputUnavailable;
            }
            var selected = await SelectInternalDocumentsAsync(db, inputs, revision.ApplicationVersion.Value, handlers, token);
            var inventory = await InitialRetirementAuthority.CaptureAsync(db, inputs, plan.Graph, owner, selected, token);
            if (plan.Graph.Retirement is null || !InitialRetirementAuthority.Same(inventory, plan.Graph.Retirement)) return DurablePlanCurrentStatus.PlanInputUnavailable;
            return DurablePlanCurrentStatus.Current;
        }
        catch (InvalidDataException) { return DurablePlanCurrentStatus.PlanInputUnavailable; }
    }
}
