using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    private static readonly DurableActionExecutionKey SiblingKey = new(PostgreSqlDatabaseCreateAction.RegistrationKey.Admission,
        OperationContractId.Create("test.graph.sibling/v1"));
    public sealed record SiblingInput(int Count);
    [JsonSerializable(typeof(SiblingInput))]
    internal partial class SiblingJsonContext : JsonSerializerContext;
    private sealed class SiblingBoundary { public int Effects; }
    private sealed class SiblingAction(IDurableOperationJournal journal, SiblingBoundary boundary) : DurableActionHandler<SiblingInput>
    {
        public override DurableActionExecutionKey Key => SiblingKey;
        public override IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) =>
            [PrepareNode(OperationId.Create("z-sibling"), resourceId, resource, inputs)];
        public override string InputFormat => "test.graph.sibling-input/v1";
        public override JsonTypeInfo<SiblingInput> InputTypeInfo => SiblingJsonContext.Default.SiblingInput;
        public override SiblingInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) => new(7);
        public override async ValueTask<DurableExecutionResult> ExecuteTypedAsync(DurableOperation operation,
            SiblingInput? input, CancellationToken cancellationToken)
        {
            Assert.Equal(7, input!.Count);
            var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(operation.Key, DateTimeOffset.UtcNow, cancellationToken));
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(operation.Key, started.AttemptId, DateTimeOffset.UtcNow, cancellationToken));
            Interlocked.Increment(ref boundary.Effects);
            Assert.True(await journal.TryRecordSuccessAsync(operation.Key, started.AttemptId, DateTimeOffset.UtcNow, default));
            return new(DurableExecutionOutcome.Succeeded, await journal.FindAsync(operation.Key, default));
        }
    }
    private sealed class DatabaseAndSiblingPlanner : IDurableActionAdmissionHandler
    {
        private readonly PostgreSqlDatabaseCreateAdmission database = new();
        public DurableActionExecutionKey Key => database.Key;
        public bool Validate(NormalizedResource resource, DurablePlanningInputs inputs) => database.Validate(resource, inputs);
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
        {
            var node = database.Plan(resourceId, resource, inputs).Single();
            return [node, node with { Id = OperationId.Create("z-sibling"), Action = SiblingKey,
                Input = new("test.graph.sibling-input/v1", "{\"Count\":7}"), Dependencies = [node.Id] }];
        }
    }
    private static async Task<WebApplication> GraphHost(ExecutionScenario scenario, SiblingBoundary boundary,
        string? connection = null, bool crash = false, RestoreProviderTrap? trap = null)
    {
        var host = OperatorHostApplication.Build(Arguments(connection ?? scenario.Connection, scenario.CertificatePath), services =>
        {
            services.RemoveAll<IDurableActionAdmissionHandler>().AddSingleton<IDurableActionAdmissionHandler, DatabaseAndSiblingPlanner>();
            services.AddSingleton(boundary).AddSingleton<IDurableActionHandler, SiblingAction>();
            if (crash) services.AddSingleton<IDurableCreateDispatchFaultHooks>(new ExecutionHooks
            { BeforeSuccess = _ => throw new IOException("Test-only lost confirmation.") });
            if (trap is not null) services.AddSingleton<IPostgreSqlDatabaseCreateTarget>(trap);
        });
        await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Multi_node_recovery_refusal_and_uncertainty_survive_dump_restore_without_replay()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var boundary = new SiblingBoundary();
        OperationRequest request;
        AssignExistingOperationRequest assignment;
        await using (var source = await GraphHost(scenario, boundary, crash: true))
        {
            using var channel = Channel(source, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            request = await CommitExecutionAsync(client, scenario.Database);
            var generationReply = await client.GetGenerationAsync(new GenerationRequest { DeploymentId = request.DeploymentId, Generation = 1 });
            Assert.Equal(new[] { "db-create", "z-sibling" }, generationReply.DesiredGeneration.Operations.Select(n => n.OperationId).ToArray());
            Assert.Equal("db-create", Assert.Single(generationReply.DesiredGeneration.Operations[1].Dependencies));
            await Assert.ThrowsAsync<IOException>(() => source.Services.GetRequiredService<IDurableOperationExecutor>()
                .ExecuteAsync(ExecutionKey(request), default).AsTask());
            Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(request)).Status);
            var progress = await source.Services.GetRequiredService<DurableGenerationRunner>().ProgressAsync(
                ExecutionKey(request).DeploymentId, DeploymentGeneration.Create(1), default);
            Assert.Equal(0, progress!.Completed); Assert.Null(progress.AppliedGeneration);
            var preview = await client.InspectOperationRecoveryAsync(request);
            Assert.False(preview.Preview.HasUncertainAttemptId);
            var operation = await client.GetOperationAsync(request);
            assignment = RecoveryAssignment(preview);
            assignment.ExpectedAttemptId = Assert.Single(operation.Attempts).AttemptId;
            var assigned = await client.AssignExistingOperationAsync(assignment);
            Assert.Equal(RecoveryStatus.Rejected, assigned.Status);
            Assert.Null(assigned.Receipt);
            var partial = await source.Services.GetRequiredService<DurableGenerationRunner>().ProgressAsync(
                ExecutionKey(request).DeploymentId, DeploymentGeneration.Create(1), default);
            Assert.Equal(0, partial!.Completed); Assert.Equal(2, partial.Total); Assert.Null(partial.AppliedGeneration);
            Assert.Equal(0, boundary.Effects);
        }
        var before = await RestoreCatalogDigests(scenario.Connection, false);
        var restored = await NewDatabaseAsync();
        var directory = "/tmp/haby-graph-restore-" + Guid.NewGuid().ToString("N");
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + directory + "/catalog.dump",
                "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, directory + "/catalog.dump");
            Assert.Equal(before, await RestoreCatalogDigests(restored, false));
            var trap = new RestoreProviderTrap();
            await using var fresh = await GraphHost(scenario, boundary, restored, trap: trap);
            using var channel = Channel(fresh, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var runner = fresh.Services.GetRequiredService<DurableGenerationRunner>();
            var key = ExecutionKey(request);
            Assert.Equal(0, (await runner.ProgressAsync(key.DeploymentId, key.Generation, default))!.Completed);
            Assert.Null((await client.InspectOperationRecoveryAsync(request)).Receipt);
            var replay = await client.AssignExistingOperationAsync(assignment);
            Assert.Equal(RecoveryStatus.Rejected, replay.Status); Assert.Null(replay.Receipt);
            Assert.Equal(before, await RestoreCatalogDigests(restored, false));
            Assert.Equal(0, trap.Calls); Assert.Equal(0, boundary.Effects);
            var completed = await runner.RunAsync(key.DeploymentId, key.Generation, default);
            Assert.Equal(DurableExecutionOutcome.NeedsAttention, completed.StopOutcome);
            Assert.Equal(0, completed.Progress!.Completed); Assert.Null(completed.Progress.AppliedGeneration);
            Assert.Equal(0, boundary.Effects); Assert.Equal(0, trap.Calls);
            Assert.Equal(RecoveryStatus.Rejected, (await client.AssignExistingOperationAsync(assignment)).Status);
            Assert.Equal(before, await RestoreCatalogDigests(restored, false));
            await using var verify = await fresh.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Empty(await verify.PostgreSqlDatabaseRecoveryDecisions.AsNoTracking().ToArrayAsync());
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-rf", directory); }
    }
}
