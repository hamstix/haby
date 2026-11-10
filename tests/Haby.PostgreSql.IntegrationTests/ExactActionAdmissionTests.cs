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

public sealed partial class ExactActionAdmissionTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly ResourceTypeId QueueContract = ResourceTypeId.Create("sample.queue/v1");
    private static readonly OperationContractId QueueCreate = OperationContractId.Create("sample.queue.create/v1");
    private static readonly DurableActionExecutionKey Key = new(new(QueueContract,
        new("sample/v1", "sample.configuration/v1", "sample.impl/v1")), QueueCreate);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_typed_queue_crosses_real_plan_check_commit_dispatch_and_restart(bool naming)
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
            if (naming)
            {
                var policies = services.GetRequiredService<IResourceNamingStore>();
                Assert.Equal(ResourceNamingOutcome.Created, (await policies.CreateAsync(environment, new("contour", 1), new("prefixed", "contour/"), "tester", default)).Outcome);
                Assert.Equal(ResourceNamingOutcome.Changed, (await policies.SelectAsync(environment, ProviderAlias.Create("queues.primary"),
                    new(instance, ProviderConfigurationRevisionNumber.Create(1)), null, new("contour", 1), "tester", default)).Outcome);
            }
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
        var operation = new DurableOperationKey(committed.DeploymentId, committed.Generation,
            OperationId.Create("jobs" + "-create"));
        await using (var missing = Services(connection, boundary, installed: false))
        {
            var executor = missing.GetRequiredService<IDurableOperationExecutor>();
            var before = await executor.ObserveAsync(operation, default);
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await executor.ExecuteAsync(operation, default)).Outcome);
            var after = await executor.ObserveAsync(operation, default);
            Assert.Equal(before!.Operation, after!.Operation);
            Assert.Empty(after.Attempts);
            Assert.Equal(0, boundary.Calls);
        }
        await using (var restored = Services(connection, boundary))
        {
            var executor = restored.GetRequiredService<IDurableOperationExecutor>();
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => executor.ExecuteAsync(operation, default).AsTask()));
            Assert.All(results, result => Assert.Equal(DurableExecutionOutcome.Succeeded, result.Outcome));
            Assert.Equal(1, boundary.Calls);
            Assert.Equal(naming ? "contour/jobs" : "jobs", boundary.Input!.QueueName);
            Assert.Equal(naming ? new ResourceNamingPolicy("prefixed", "contour/") : ResourceNamingPolicy.Exact, plan.Inputs.Naming.Policy);
        }
        // Terminal history needs neither a handler nor a configuration reader.
        await using (var missing = Services(connection, boundary, installed: false, codec: false))
        {
            var executor = missing.GetRequiredService<IDurableOperationExecutor>();
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(operation, default)).Outcome);
            Assert.Single((await executor.ObserveAsync(operation, default))!.Attempts);
        }
        await using var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
        Assert.Equal(1, Assert.Single(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync()).AppliedGeneration);
        Assert.Equal(plan.Fingerprint, Assert.Single(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync()).Fingerprint);
        Assert.Equal(1, boundary.Calls);
    }

    private static ServiceProvider Services(string connection, QueueBoundary boundary, bool installed = true, bool codec = true, ConfigurationRenderingCatalog? rendering = null)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(new QueueClock()).AddSingleton(rendering ?? ConfigurationRenderingCatalog.Empty)
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddSingleton<IProviderRevisionPreflight, AcceptPreflight>()
            .AddPostgreSqlProviderAliasStore().AddPostgreSqlDurableDesiredStateCommitter()
            .AddPostgreSqlDurableOperationJournal().AddSingleton(boundary)
            .AddSingleton<DurableOperationSerialization>()
            .AddSingleton<ExactDurableOperationExecutor>()
            .AddSingleton<IDurableOperationExecutor>(p => p.GetRequiredService<ExactDurableOperationExecutor>())
            .AddSingleton<DurableGenerationRunner>();
        if (boundary.Generated)
        {
            services.AddSingleton<IGeneratedCredentialOperations, PostgreSqlGeneratedCredentialOperations>();
            services.AddSingleton<IDurableActionHandler>(p => new GeneratedCredentialAction(Key.Admission,
                p.GetRequiredService<IDurableOperationJournal>(), p.GetRequiredService<IGeneratedCredentialSlots>(),
                p.GetRequiredService<IGeneratedCredentialOperations>(), p.GetRequiredService<TimeProvider>()));
        }
        if (codec) services.AddSingleton<IProviderConfigurationCodec, SampleConfigurationCodec>();
        if (installed && boundary.ExternalEvidence)
        {
            services.AddSingleton<QueueEvidenceProducer>()
                .AddSingleton<IDurableActionAdmissionHandler>(p => p.GetRequiredService<QueueEvidenceProducer>())
                .AddSingleton<IDurableActionHandler>(p => p.GetRequiredService<QueueEvidenceProducer>())
                .AddSingleton<IDurableActionHandler, QueueEvidenceConsumer>();
            for (var i = 0; i < boundary.OutputContractRegistrations; i++)
                services.AddSingleton<IExternalOperationOutputContract, QueueEvidenceContract>();
        }
        else if (installed) services.AddSingleton<QueueAction>()
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

    public sealed record QueueCreateInput(string QueueName, int Units = 1);
    private sealed class QueueBoundary
    {
        public SecretRef? DeliveryCredential;
        public bool Graph;
        public bool Generated;
        public bool ExternalEvidence;
        public bool DependentResources;
        public int OutputContractRegistrations = 1;
        public bool RejectAcceptedOutput;
        public string PlannedName = "jobs";
        public string Format = "sample.queue-create/v1";
        public string? Mode;
        public string StopId = "20-dependent";
        public CancellationTokenSource? Cancellation;
        public Func<CancellationToken, Task>? Pause;
        public List<string> Effects = [];
        public int Calls;
        public QueueCreateInput? Input;
        public void Create(QueueCreateInput input) { Input = input; Interlocked.Increment(ref Calls); }
    }
    private sealed class QueueAction(IDurableOperationJournal journal, IProviderConfigurationStore configurations,
        QueueBoundary boundary, IServiceProvider services) : DurableActionHandler<QueueCreateInput>, IResourceNamingPolicyHandler
    {
        public override IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
        {
            var single = PrepareNode(OperationId.Create(resourceId.Value + "-create"), resourceId, resource, inputs);
            if (boundary.Generated)
            {
                var producer = single with
                {
                    Id = OperationId.Create("10-material"),
                    Action = new(Key.Admission, OperationContractId.Create(GeneratedCredentialContracts.Action)),
                    Input = GeneratedCredentialInputCodec.Encode(new(new(inputs.Environment, inputs.Revision.ApplicationRecordId, resourceId),
                        GeneratedCredentialSlot.ApplicationPassword, GeneratedCredentialSlot.RandomPasswordV1))
                };
                var selector = GeneratedCredentialContracts.PasswordFrom(producer.Id);
                var consumer = single with { Id = OperationId.Create("20-dependent"), Dependencies = [producer.Id], OutputReferences = [selector] };
                return [consumer, producer];
            }
            if (!boundary.Graph) return [single];
            DurableOperationNode Step(string id, int units, params string[] dependencies) => single with
            {
                Id = OperationId.Create(id),
                Input = new(InputFormat, System.Text.Json.JsonSerializer.Serialize(new QueueCreateInput(boundary.PlannedName, units), InputTypeInfo)),
                Dependencies = dependencies.Select(OperationId.Create).ToArray()
            };
            // Deliberately non-canonical declaration order; all three steps own the same Resource.
            return [Step("30-independent", 3), Step("20-dependent", 2, "10-first"), Step("10-first", 1)];
        }
        public bool SupportsNaming(ResourceNamingPolicy policy) => policy.Mode == "exact" || policy.Mode == "prefixed" && policy.Prefix is { Length: > 0 and <= 128 };
        public override string InputFormat => boundary.Format;
        public override System.Text.Json.Serialization.Metadata.JsonTypeInfo<QueueCreateInput> InputTypeInfo => QueueJsonContext.Default.QueueCreateInput;
        public override DurableActionExecutionKey Key => ExactActionAdmissionTests.Key;
        public override QueueCreateInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
            resource.Type == QueueContract && resource.Properties.Members.Count == 1 &&
            resource.Properties.Members.TryGetValue("queueName", out var value) && value is ConfigurationString name && name.Value == "jobs"
                ? new((inputs.Naming.Policy.Prefix ?? "") + name.Value) : null;
        public override async ValueTask<DurableExecutionResult> ExecuteTypedAsync(DurableOperation operation,
            QueueCreateInput? input, CancellationToken cancellationToken)
        {
            Assert.NotNull(input);
            if (boundary.Generated)
                Assert.NotNull(await services.GetRequiredService<IGeneratedCredentialOperations>().ConsumeAsync(operation.Key,
                    Assert.Single(operation.Node.OutputReferences), cancellationToken));
            var configuration = await configurations.FindRevisionAsync<SampleConfiguration>(operation.Environment,
                operation.ProviderInstance, operation.ConfigurationRevision, cancellationToken);
            Assert.Equal("queue-broker", configuration!.Configuration.Target);
            var attempt = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(operation.Key,
                DateTimeOffset.UtcNow, cancellationToken));
            if (operation.Key.OperationId.Value == boundary.StopId)
            {
                if (boundary.Pause is { } pause) await pause(cancellationToken);
                if (boundary.Mode is "retry" or "cancel")
                {
                    if (boundary.Mode == "cancel") boundary.Cancellation!.Cancel();
                    await journal.TryCloseAttemptBeforeDispatchAsync(operation.Key, attempt.AttemptId,
                        boundary.Mode == "retry" ? DurableAttemptOutcome.FailedBeforeDispatch : DurableAttemptOutcome.CanceledBeforeDispatch,
                        boundary.Mode == "retry" ? DurableOperationReason.Connectivity : null, DateTimeOffset.UtcNow, default);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new(DurableExecutionOutcome.RetryableFailure, await journal.FindAsync(operation.Key, default));
                }
                if (boundary.Mode == "failure")
                {
                    await journal.TryFinalizeFailureAsync(operation.Key, attempt.AttemptId, DurableOperationState.Failed,
                        DurableOperationReason.InputInvalid, DateTimeOffset.UtcNow, default);
                    return new(DurableExecutionOutcome.Failed, await journal.FindAsync(operation.Key, default));
                }
            }
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(operation.Key,
                attempt.AttemptId, DateTimeOffset.UtcNow, cancellationToken));
            if (operation.Key.OperationId.Value == boundary.StopId && boundary.Mode == "crash") throw new IOException("Test-only process death.");
            boundary.Create(input);
            boundary.Effects.Add(operation.Key.OperationId.Value + ":" + input.QueueName + ":" + input.Units);
            if (operation.Key.OperationId.Value == boundary.StopId && boundary.Mode == "uncertain")
            {
                await journal.TryFinalizeFailureAsync(operation.Key, attempt.AttemptId, DurableOperationState.NeedsAttention,
                    DurableOperationReason.UncertainAfterDispatch, DateTimeOffset.UtcNow, default);
                return new(DurableExecutionOutcome.NeedsAttention, await journal.FindAsync(operation.Key, default));
            }
            Assert.True(await journal.TryRecordSuccessAsync(operation.Key, attempt.AttemptId, DateTimeOffset.UtcNow, cancellationToken));
            return new(DurableExecutionOutcome.Succeeded, await journal.FindAsync(operation.Key, cancellationToken));
        }
    }
    [System.Text.Json.Serialization.JsonSerializable(typeof(QueueCreateInput))]
    internal partial class QueueJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
