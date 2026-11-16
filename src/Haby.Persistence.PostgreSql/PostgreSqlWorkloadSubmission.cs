using System.Data;
using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed record WorkloadSubmissionOptions(int RequestMilliseconds = 30000)
{
    internal void Validate()
    {
        if (RequestMilliseconds is < 1 or > 300000) throw new ArgumentOutOfRangeException(nameof(RequestMilliseconds));
    }
}

internal sealed class PostgreSqlWorkloadSubmission(IDbContextFactory<ManifestCatalogDbContext> contexts,
    InternalWorkloadConfiguration configuration, InternalDurableActions actions, InternalRuntimeIdentityPreparation identity,
    InternalRuntimePublicationPreparation publication, DurableOperationSerialization serialization, WorkloadSubmissionOptions options,
    string deploymentAction) : IOperatorWorkloadSubmission
{
    private IDbContextFactory<ManifestCatalogDbContext> Contexts => contexts;
    private InternalWorkloadConfiguration Configuration => configuration;
    private InternalDurableActions Actions => actions;
    private InternalRuntimeIdentityPreparation Identity => identity;
    private InternalRuntimePublicationPreparation Publication => publication;
    private string DeploymentAction => deploymentAction;
    public async ValueTask<WorkloadSubmissionResult> SubmitAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, string actor, CancellationToken token)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actor); options.Validate();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(options.RequestMilliseconds);
        try
        {
            return await serialization.RunAsync(new(deployment, generation, OperationId.Create("submission")), async () =>
            {
                var steps = new Steps(this, deployment, generation, actor);
                var result = await new SubmitWorkloadGeneration(steps).ExecuteAsync(budget.Token);
                if (result.Progress is null)
                {
                    // Catalog-only closure has no effect/observation authority and cannot renew the request.
                    using var closure = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { result = result with { Progress = await ReadAsync(deployment, generation, closure.Token) }; }
                    catch (Exception error) when (error is OperationCanceledException or InvalidDataException or Npgsql.NpgsqlException or DbUpdateException) { }
                }
                return result;
            }, budget.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        { return new("cancelled", Reason: "cancelled", RequiresInspection: true); }
        catch (Exception error) when (error is Npgsql.NpgsqlException or DbUpdateException)
        { return new("unavailable", Reason: "storage-unavailable", RequiresInspection: true); }
    }

    public async ValueTask<WorkloadSubmissionProgress?> ReadAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == Guid.Parse(deployment.ToString()) && x.Generation == generation.Value, token);
        if (source is null || !source.InternalEffects) return null;
        try { return await actions.ReadSubmissionAsync(db, source, configuration, token); }
        catch (InvalidDataException)
        {
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == source.DeploymentId, token);
            return new(deployment.ToString(), generation.Value, owner.DesiredGeneration, owner.AppliedGeneration, "unknown", null, null, []);
        }
    }

    private sealed class Steps(PostgreSqlWorkloadSubmission owner, ApplicationDeploymentId deployment, DeploymentGeneration generation, string actor) : IWorkloadSubmissionSteps
    {
        private DurableChangeSetCommit accepted = null!;
        private ProviderInput.Workload[] subjects = [];
        public async ValueTask<WorkloadSubmissionStep> InspectAsync(CancellationToken token)
        {
            await using var db = await owner.Contexts.CreateDbContextAsync(token);
            var id = Guid.Parse(deployment.ToString());
            var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == id && x.Generation == generation.Value, token);
            if (source is null) return new("generation-not-found");
            var commit = await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == id && x.Generation == generation.Value, token);
            if (commit is null || !source.InternalEffects) return new("unsupported-shape");
            accepted = new(DeploymentChangeSetId.Parse(commit.ChangeSetId.ToString()), DeploymentPlanId.Parse(commit.PlanId.ToString()), deployment, generation, commit.CommittedAt, commit.ActorId);
            source = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
            var selection = await owner.Configuration.SelectAsync(db, source, token);
            await owner.Actions.RequireSubmissionSupportAsync(db, source, selection, owner.DeploymentAction, owner.Identity.ActionContract, token);
            subjects = selection.Keys.OrderBy(x => x.Component.Value, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal).ToArray();
            // All distinct item cardinalities are known before the first NEW write.
            if (subjects.Length is 0 or > 512 || selection.Values.Sum(x => (long)x.Count) > 512 || selection.Values.Any(x => x.Count > 64)) return new("target-bound");
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            if (graph.Baseline is { } baseline)
            {
                var previous = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == baseline.Generation, token);
                if (previous.InternalEffects || previous.Inputs().Configuration!.Manifest.Spec.Components.Values.Any(x => x.Workloads.Count > 0) || graph.ReusedResources.Any(x => x.Reintroduced))
                    return new("unsupported-shape");
            }
            return new();
        }
        public async ValueTask<WorkloadSubmissionStep> RecoverAsync(CancellationToken token)
        {
            // Saved immutable history is inspected before current-authority preparation.
            foreach (var subject in subjects)
            {
                token.ThrowIfCancellationRequested();
                string[] pending;
                await using (var db = await owner.Contexts.CreateDbContextAsync(token))
                {
                    pending = await (from intent in db.ActionIntents.AsNoTracking()
                                     join operation in db.DurableOperations.AsNoTracking() on new { intent.DeploymentId, intent.Generation, intent.OperationId }
                                         equals new { operation.DeploymentId, operation.Generation, operation.OperationId }
                                     where intent.DeploymentId == Guid.Parse(deployment.ToString()) && intent.Generation == generation.Value &&
                                         intent.Component == subject.Component.Value && intent.LocalId == subject.Id.Value &&
                                         (operation.State == "Dispatching" || operation.State == "NeedsAttention")
                                     orderby intent.CreatedAt, intent.OperationId
                                     select intent.OperationId).ToArrayAsync(token);
                }
                foreach (var operation in pending)
                {
                    var key = Key(operation);
                    token.ThrowIfCancellationRequested();
                    var result = await owner.Actions.ExecuteCoreAsync(key, true, token, actor);
                    if (result.Outcome != InternalActionOutcome.Confirmed) return new(result.Code, Subject(subject), true);
                }
            }
            return new();
        }
        public async ValueTask<WorkloadSubmissionStep> PrepareAsync(WorkloadSubmissionStage stage, CancellationToken token)
        {
            switch (stage)
            {
                case WorkloadSubmissionStage.Documents:
                    var prepared = await owner.Configuration.PrepareAsync(accepted, actor, token);
                    return prepared.Failure is null && !prepared.Cancelled ? new() : new(prepared.Cancelled ? "cancelled" : "configuration-incomplete", prepared.Failed is { } target ? target.Component.Value + "/" + target.Document.Value : null, true);
                case WorkloadSubmissionStage.Identity:
                    var identities = await owner.Identity.PrepareCoreAsync(accepted, actor, token);
                    var blocked = identities.FirstOrDefault(x => x.Result.Outcome != InternalActionOutcome.Confirmed);
                    return blocked is null ? new() : new(blocked.Result.Code, Subject(blocked.Workload), true);
                case WorkloadSubmissionStage.Publication:
                    var published = await owner.Publication.PrepareAsync(accepted, actor, token);
                    return new(published.Reason, published.Failed?.Origin is { } origin ? origin.Component + "/" + origin.Workload + "/" + origin.Document : null, published.RequiresInspection);
                case WorkloadSubmissionStage.Deployment:
                    foreach (var subject in subjects)
                    {
                        token.ThrowIfCancellationRequested();
                        string request;
                        await using (var db = await owner.Contexts.CreateDbContextAsync(token))
                        {
                            var existing = await db.ActionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == Guid.Parse(deployment.ToString()) && x.Generation == generation.Value &&
                                x.Component == subject.Component.Value && x.LocalId == subject.Id.Value && x.ActionContract == owner.DeploymentAction, token);
                            if (existing is not null)
                            {
                                var state = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == existing.DeploymentId && x.Generation == existing.Generation && x.OperationId == existing.OperationId, token);
                                if (state.State == "Succeeded") continue; // Validated by catalog-only whole-set projection.
                            }
                            request = existing?.RequestId ?? "submit-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("v1|" + subject.Component.Value + "|" + subject.Id.Value)));
                        }
                        var key = await owner.Actions.AcceptCoreAsync(accepted, subject, owner.DeploymentAction, request, actor, token);
                        token.ThrowIfCancellationRequested();
                        var result = await owner.Actions.ExecuteCoreAsync(key, false, token, actor);
                        if (result.Outcome != InternalActionOutcome.Confirmed) return new(result.Code, Subject(subject), true);
                    }
                    return new();
                default: throw new InvalidOperationException("Unknown submission stage.");
            }
        }
        public async ValueTask<WorkloadSubmissionProgress> ReadAsync(CancellationToken token) =>
            await owner.ReadAsync(deployment, generation, token) ?? throw new InvalidDataException("Generation unavailable.");
        private DurableOperationKey Key(string operation) => new(deployment, generation, OperationId.Create(operation));
        private static string Subject(ProviderInput.Workload subject) => subject.Component.Value + "/" + subject.Id.Value;
    }
}
