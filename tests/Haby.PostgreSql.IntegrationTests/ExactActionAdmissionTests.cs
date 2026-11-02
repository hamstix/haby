using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class ExactActionAdmissionTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly ResourceTypeId QueueContract = ResourceTypeId.Create("sample.queue/v1");
    private static readonly OperationContractId QueueCreate = OperationContractId.Create("sample.queue.create/v1");
    private static readonly DurableActionExecutionKey Key = new(new(QueueContract,
        new("sample/v1", "sample.configuration/v1", "sample.impl/v1")), QueueCreate);

    [Fact]
    public async Task Different_typed_queue_crosses_real_plan_check_commit_dispatch_and_restart()
    {
        var name = "haby_queue_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin).ExecuteNonQueryAsync();
        }
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
        var boundary = new QueueBoundary();
        DurablePlan plan;
        DeploymentChangeSet change;
        DurableChangeSetCommit committed;
        await using (var services = Services(connection, boundary))
        {
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var environment = await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var providers = services.GetRequiredService<IProviderConfigurationStore>();
            var config = new SampleConfiguration("queue-broker", "sample.impl/v1");
            var instance = await providers.CreateInstanceAsync(environment, config, default);
            await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), config, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>()
                .ChangeAsync(environment, ProviderAlias.Create("queues.primary"), null,
                    new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
            var catalog = new ManifestContractCatalog([new ResourceTypeDefinition(QueueContract, "{\"type\":\"object\"}")], [], [], []);
            var payload = Encoding.UTF8.GetBytes("""
                {"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application",
                "metadata":{"id":"queue-app"},"spec":{"resources":{"jobs":{"type":"sample.queue/v1",
                "providerRef":"queues.primary","properties":{"queueName":"jobs"}}}}}}
                """);
            var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(payload, "tester"))
                .ManifestRevisionRef;
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
            plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
            Assert.NotNull(plan);
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
            Assert.Equal(0, boundary.Calls);
        }
        await using (var missing = Services(connection, boundary, installed: false))
        {
            var planner = missing.GetRequiredService<IDurableDeploymentPlanner>();
            Assert.Equal(plan, await planner.FindPlanAsync(plan.Id, default));
            Assert.Equal(DurablePlanStatus.ProviderCapabilityMismatch, (await planner.CreatePlanAsync(change.Id, "tester", default)).Status);
            Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await planner.CheckPlanAsync(plan.Id, default));
            Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await missing.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(change.Id, plan.Id, "tester", default));
        }
        await using (var restored = Services(connection, boundary))
        {
            committed = Assert.IsType<DurableCommitResult.Committed>(await restored.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
            Assert.Equal(plan.Inputs, (await restored.GetRequiredService<IDurableDesiredStateCommitter>()
                .FindGenerationAsync(committed.DeploymentId, committed.Generation, default))!.Inputs);
        }
        var operation = new DurableCreateOperationKey(committed.DeploymentId, committed.Generation,
            ManifestLocalId.Create("jobs"), DurableOperationAction.Create);
        await using (var missing = Services(connection, boundary, installed: false))
        {
            var executor = missing.GetRequiredService<IDurableCreateOperationExecutor>();
            var before = await executor.ObserveAsync(operation, default);
            Assert.Equal(DurableCreateExecutionOutcome.HandlerUnavailable, (await executor.ExecuteAsync(operation, default)).Outcome);
            var after = await executor.ObserveAsync(operation, default);
            Assert.Equal(before!.Operation, after!.Operation);
            Assert.Empty(after.Attempts);
            Assert.Equal(0, boundary.Calls);
        }
        await using (var restored = Services(connection, boundary))
        {
            var executor = restored.GetRequiredService<IDurableCreateOperationExecutor>();
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => executor.ExecuteAsync(operation, default).AsTask()));
            Assert.All(results, result => Assert.Equal(DurableCreateExecutionOutcome.Succeeded, result.Outcome));
            Assert.Equal(1, boundary.Calls);
            Assert.Equal("jobs", boundary.Input!.QueueName);
            Assert.Equal(plan.Inputs.ProviderInstance, boundary.Input.ProviderInstance);
        }
        // Terminal history needs neither a handler nor a configuration reader.
        await using (var missing = Services(connection, boundary, installed: false, codec: false))
        {
            var executor = missing.GetRequiredService<IDurableCreateOperationExecutor>();
            Assert.Equal(DurableCreateExecutionOutcome.Succeeded, (await executor.ExecuteAsync(operation, default)).Outcome);
            Assert.Single((await executor.ObserveAsync(operation, default))!.Attempts);
        }
        await using var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
        Assert.Equal(1, Assert.Single(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync()).AppliedGeneration);
        Assert.Equal(plan.Fingerprint, Assert.Single(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync()).Fingerprint);
        Assert.Equal(1, boundary.Calls);
    }

    private static ServiceProvider Services(string connection, QueueBoundary boundary, bool installed = true, bool codec = true)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(new QueueClock())
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddSingleton<IProviderRevisionPreflight, AcceptPreflight>()
            .AddPostgreSqlProviderAliasStore().AddPostgreSqlDurableDesiredStateCommitter()
            .AddPostgreSqlDurableCreateOperationJournal().AddSingleton(boundary)
            .AddSingleton(new DurableCreateActionMapping(QueueContract, QueueCreate))
            .AddSingleton<DurableOperationSerialization>()
            .AddSingleton<IDurableCreateOperationExecutor, ExactDurableCreateOperationExecutor>();
        if (codec) services.AddSingleton<IProviderConfigurationCodec, SampleConfigurationCodec>();
        if (installed) services.AddSingleton<QueueAction>()
            .AddSingleton<IDurableActionAdmissionHandler>(p => p.GetRequiredService<QueueAction>())
            .AddSingleton<IDurableActionHandler>(p => p.GetRequiredService<QueueAction>());
        return services.BuildServiceProvider();
    }

    private sealed class QueueClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class AcceptPreflight : IProviderRevisionPreflight
    {
        public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, ProviderAliasBinding target,
            CancellationToken cancellationToken) => ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
    }

    private sealed record QueueCreateInput(string QueueName, ProviderInstanceId ProviderInstance,
        ProviderConfigurationRevisionNumber Revision);
    private sealed class QueueBoundary
    {
        public int Calls;
        public QueueCreateInput? Input;
        public void Create(QueueCreateInput input) { Input = input; Interlocked.Increment(ref Calls); }
    }
    private sealed class QueueAction(IDurableCreateOperationJournal journal, IProviderConfigurationStore configurations,
        QueueBoundary boundary) : DurableActionHandler<QueueCreateInput>
    {
        public override DurableActionExecutionKey Key => ExactActionAdmissionTests.Key;
        public override QueueCreateInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
            resource.Type == QueueContract && resource.Properties.Members.Count == 1 &&
            resource.Properties.Members.TryGetValue("queueName", out var value) && value is ConfigurationString name && name.Value == "jobs"
                ? new(name.Value, inputs.ProviderInstance, inputs.ConfigurationRevision) : null;
        public override async ValueTask<DurableCreateExecutionResult> ExecuteTypedAsync(DurableCreateOperation operation,
            QueueCreateInput? input, CancellationToken cancellationToken)
        {
            Assert.NotNull(input);
            var configuration = await configurations.FindRevisionAsync<SampleConfiguration>(operation.Environment,
                input.ProviderInstance, input.Revision, cancellationToken);
            Assert.Equal("queue-broker", configuration!.Configuration.Target);
            var attempt = Assert.IsType<DurableCreateBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(operation.Key,
                DateTimeOffset.UtcNow, cancellationToken));
            Assert.IsType<DurableCreateMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(operation.Key,
                attempt.AttemptId, DateTimeOffset.UtcNow, cancellationToken));
            boundary.Create(input);
            Assert.True(await journal.TryRecordSuccessAsync(operation.Key, attempt.AttemptId, DateTimeOffset.UtcNow, cancellationToken));
            return new(DurableCreateExecutionOutcome.Succeeded, await journal.FindAsync(operation.Key, cancellationToken));
        }
    }
}
