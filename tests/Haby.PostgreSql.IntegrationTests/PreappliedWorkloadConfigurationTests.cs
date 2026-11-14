using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using ProfileAssignmentTarget = Haby.Domain.ProfileAssignmentTarget;
using StoreStatus = Haby.Application.ConfigurationDocumentStatus;
using RpcStatus = Haby.Operator.Contracts.ConfigurationDocumentStatus;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private static void ConfiguredDocuments(JsonNode envelope, bool secondDocument = false, bool shared = false)
    {
        var component = envelope["manifest"]!["spec"]!["components"]!["api"]!;
        component["documents"] = JsonNode.Parse("""{"settings":{"fileName":"settings.json","format":"json","template":{"Mode":"frozen"}}}"""u8);
        component["workloads"]!["main"]!["properties"]!["configuration"] = JsonNode.Parse("""{"documents":["settings"]}"""u8);
        if (secondDocument)
        {
            component["documents"]!["z-last"] = JsonNode.Parse("""{"fileName":"last.json","format":"json","template":{}}"""u8);
            component["workloads"]!["main"]!["properties"]!["configuration"]!["documents"]!.AsArray().Add("z-last");
        }
        if (shared) component["workloads"]!["second"] = component["workloads"]!["main"]!.DeepClone();
    }

    internal static (InternalWorkloadConfiguration Preparation, PostgreSqlConfigurationDocumentStore Documents) ConfigurationPreparation(
        ServiceProvider services, IConfigurationOutputEvidence? evidence = null, ConfigurationRenderingCatalog? rendering = null,
        ConfigurationFormatterCatalog? formatters = null)
    {
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var store = new PostgreSqlConfigurationDocumentStore(contexts,
            new(rendering ?? ConfigurationRenderingCatalog.Empty, formatters ?? new([], []), evidence ?? services.GetRequiredService<IConfigurationOutputEvidence>()),
            services.GetRequiredService<TimeProvider>());
        return (new(contexts, services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(), store,
            services.GetServices<IInternalActionHandler>().ToArray(), services.GetServices<IExternalOperationOutputContract>(),
            services.GetServices<IProviderConfigurationCodec>(), services.GetRequiredService<DurableActionAdmission>()), store);
    }

    [Fact]
    public async Task Initial_configuration_preparation_concurrent_replay_restart_restore_and_public_refusals_preserve_exact_artifacts()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, shared: true));
        WorkloadConfigurationPreparation first;
        ConfigurationDocumentRow artifact;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            var preparation = ConfigurationPreparation(services);
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => preparation.Preparation.PrepareAsync(scenario.Commit, "tester", default).AsTask()));
            first = results[0];
            Assert.Null(first.Failure); Assert.Equal(2, first.Prepared.Count);
            Assert.Single(first.Prepared.Select(x => x.Revision).Distinct());
            Assert.All(results, r => Assert.Equal(first.Prepared, r.Prepared));
            var target = new ConfigurationDocumentTarget(scenario.Commit.DeploymentId, scenario.Commit.Generation, ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
            Assert.Equal(StoreStatus.NotApplied, (await preparation.Documents.MaterializeAsync(target, "tester", default)).Status);
            Assert.Equal(StoreStatus.NotApplied, (await preparation.Documents.ReadAsync(target, default)).Status);
            // Executable acceptance refuses before creating an intent, attempt, dispatch marker or Kubernetes client.
            await Assert.ThrowsAsync<Haby.Kubernetes.Contracts.WorkloadContractException>(async () => await Actions(services,
                services.GetRequiredService<IInternalActionHandler>()).AcceptAsync(scenario.Commit, first.Prepared[0].Workload,
                InternalDeploymentAction.Create, "configured-refusal", default));
        }
        await using (var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).Options))
        {
            artifact = await verify.ConfigurationDocuments.AsNoTracking().SingleAsync();
            Assert.Equal(2, await verify.WorkloadConfigurationRecipients.CountAsync());
            Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            var proof = await verify.GenerationStageCheckpoints.AsNoTracking().SingleAsync();
            Assert.Empty(ResourcesReadyCheckpointCodec.Decode(proof.EvidenceJson).Resources);
        }
        await PublicConfigurationRefusesAsync(scenario);
        await VerifyRestart(scenario.Connection);
        var restored = await NewDatabaseAsync();
        var dump = "/tmp/haby208-" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await VerifyRestart(restored);
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); }
        async Task VerifyRestart(string connection)
        {
            await using var restarted = InternalServices(connection, scenario.Time);
            var replay = await ConfigurationPreparation(restarted).Preparation.PrepareAsync(scenario.Commit, "later-actor", default);
            Assert.Equal(first.Prepared, replay.Prepared);
            await using var check = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            var saved = await check.ConfigurationDocuments.AsNoTracking().SingleAsync();
            Assert.Equal(artifact.Payload, saved.Payload); Assert.Equal(artifact.Id, saved.Id);
            Assert.All(await check.WorkloadConfigurationRecipients.AsNoTracking().ToArrayAsync(), r => Assert.Equal("tester", r.Actor));
        }
    }

    [Fact]
    public async Task Preparation_cancellation_after_one_document_commits_an_honest_prefix_and_retry_finishes_the_remainder()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, secondDocument: true));
        using var cancellation = new CancellationTokenSource();
        await using (var services = Services(scenario.Connection, scenario.Time, internalActions: true,
            commandInterceptor: new CancelAfterDocumentCommit(cancellation)))
        {
            var partial = await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", cancellation.Token);
            Assert.True(partial.Cancelled); Assert.Single(partial.Prepared);
            Assert.Equal("z-last", partial.Failed!.Document.Value);
        }
        await using (var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).Options))
        {
            Assert.Single(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
            Assert.Single(await verify.WorkloadConfigurationRecipients.AsNoTracking().ToArrayAsync());
        }
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var complete = await ConfigurationPreparation(restarted).Preparation.PrepareAsync(scenario.Commit, "resume", default);
        Assert.Null(complete.Failure); Assert.Equal(2, complete.Prepared.Count);
    }

    [Fact]
    public async Task Later_document_composition_failure_preserves_the_complete_first_document_without_partial_second_writes()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e =>
        {
            ConfiguredDocuments(e, secondDocument: true);
            e["manifest"]!["spec"]!["components"]!["api"]!["documents"]!["z-last"]!["template"] = new JsonObject { ["unsupported"] = "{{expression}}" };
        });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var partial = await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        Assert.Single(partial.Prepared); Assert.Equal(StoreStatus.InputUnavailable, partial.Failure);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.WorkloadConfigurationRecipients.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Wrong_accepted_authority_and_corrupt_proof_refuse_preparation_and_replay_without_new_writes()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var preparation = ConfigurationPreparation(services).Preparation;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await preparation.PrepareAsync(scenario.Commit with { ActorId = "foreign" }, "tester", default));
        var prepared = await preparation.PrepareAsync(scenario.Commit, "tester", default);
        Assert.Single(prepared.Prepared);
        await using (var arrange = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            // Deliberate privileged corruption, never a manufactured accepted generation/proof.
            await arrange.Database.ExecuteSqlRawAsync("UPDATE generation_stage_checkpoints SET evidence_fingerprint = repeat('0', 64)");
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await preparation.PrepareAsync(scenario.Commit, "tester", default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.WorkloadConfigurationRecipients.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task A_second_Workload_selecting_an_undeclared_document_refuses_before_generation_acceptance()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () => await InternalScenarioAsync(initialOnly: true, configureManifest: e =>
        {
            ConfiguredDocuments(e, shared: true);
            e["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["second"]!["properties"]!["configuration"] = JsonNode.Parse("""{"documents":["foreign"]}"""u8);
        }));
    }

    [Fact]
    public async Task Initial_Profile_document_is_frozen_and_later_assignment_changes_cannot_rebind_its_prepared_artifact()
    {
        AssignmentToken? assignment = null;
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e =>
        {
            ConfiguredDocuments(e);
            e["manifest"]!["spec"]!["components"]!["api"]!.AsObject().Remove("documents");
        }, acceptGeneration: async (services, environment, revision) =>
        {
            assignment = await AssignPreparationProfile(services, environment, false);
            return await new InternalAcceptedGenerationBuilder(services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(),
                (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>(),
                services.GetServices<IInternalActionHandler>().ToArray()).AcceptForEffectAsync(environment, revision.ApplicationRecordId,
                    revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default);
        });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var first = await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        Assert.Single(first.Prepared);
        var environment = (await services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("initial"), default))!;
        Assert.Equal(AssignmentOutcome.Updated, (await services.GetRequiredService<IProfileAssignmentStore>().ApplyAsync(environment,
            new(new ProfileAssignmentTarget.Environment(), []), new AssignmentPrecondition.ExpectedConcurrencyToken(assignment!),
            new("later", scenario.Time.GetUtcNow(), null), default)).Outcome);
        var repeat = await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "later", default);
        Assert.Equal(first.Prepared, repeat.Prepared);
    }

    [Fact]
    public async Task Effective_Profile_static_secret_projection_refuses_before_preparation_or_named_secret_resolution()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () => await InternalScenarioAsync(initialOnly: true,
            configureManifest: e => { ConfiguredDocuments(e); e["manifest"]!["spec"]!["components"]!["api"]!.AsObject().Remove("documents"); }, acceptGeneration: async (services, environment, revision) =>
            {
                await AssignPreparationProfile(services, environment, true);
                var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
                var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision,
                    new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
                var planned = await planner.CreateInternalPlanAsync(change.Id, "tester", default, services.GetServices<IInternalActionHandler>().ToArray());
                Assert.Equal(DurablePlanStatus.ConfigurationRefused, planned.Status);
                Assert.Contains(planned.ConfigurationReport!.Diagnostics, d => d.Code == "profile.secret-projections.unsupported");
                await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
                Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.WorkloadConfigurationRecipients.AsNoTracking().ToArrayAsync());
                throw new InvalidDataException("Expected unsupported Profile projection.");
            }));
    }

    private static async Task<AssignmentToken> AssignPreparationProfile(ServiceProvider services, EnvironmentRecordId environment, bool secret)
    {
        var profile = Haby.Tests.ProfilePersistenceSamples.Node(secrets: secret);
        profile["spec"]!.AsObject().Remove("bindingRules");
        var imported = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
            services.GetRequiredService<IConfigurationProfileImportStore>(), TimeProvider.System).ExecuteAsync(
                Haby.Tests.ProfilePersistenceSamples.Bytes(profile), "tester"));
        var metadata = (await services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("initial"), default))!;
        Assert.Equal(environment, metadata.RecordId);
        var result = await services.GetRequiredService<IProfileAssignmentStore>().ApplyAsync(metadata,
            new(new ProfileAssignmentTarget.Environment(), [new ProfileDecision.Use(imported.Revision.Reference)]),
            new AssignmentPrecondition.ExpectedAbsence(), new("tester", DateTimeOffset.UtcNow, null), default);
        Assert.Equal(AssignmentOutcome.Created, result.Outcome);
        return result.Assignment!.Token;
    }

    private sealed class CancelAfterDocumentCommit(CancellationTokenSource cancellation) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ConfigurationDocumentRow>().Any()) cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    private static async Task PublicConfigurationRefusesAsync(InternalScenario scenario)
    {
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection,
            "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = OperatorTestCredentials.Client(channel);
        await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).Options);
        var app = await db.Applications.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(scenario.Revision.ApplicationRecordId.ToString()));
        var env = await db.Environments.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(scenario.Environment.ToString()));
        foreach (var exact in new[] { false, true })
        {
            var request = new ConfigurationDocumentRequest { Environment = env.EnvironmentId, ApplicationId = app.ApplicationId, ComponentId = "api", DocumentId = "settings" };
            if (exact) request.Generation = scenario.Commit.Generation.Value;
            Assert.Equal(RpcStatus.NotApplied, (await client.MaterializeConfigurationAsync(request)).Status);
            Assert.Equal(RpcStatus.NotApplied, (await client.GetConfigurationAsync(request)).Status);
            Assert.Equal(RpcStatus.NotApplied, (await client.PreviewConfigurationAsync(request)).Status);
        }
        await host.StopAsync();
    }
}
