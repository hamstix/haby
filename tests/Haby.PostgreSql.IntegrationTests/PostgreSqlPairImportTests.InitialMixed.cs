using Haby.TestDiagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using ImportStatus = Haby.Operator.Contracts.ResourceImportRequestStatus;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Fact]
    public Task Public_initial_mixed_generation_reuses_its_original_authority_in_a_successive_revision() =>
        Public_initial_mixed_Import_and_Reference_delivers_only_complete_generation_after_restart("successive");

    [Theory]
    [InlineData("success")]
    [InlineData("import-failure")]
    [InlineData("reference-failure")]
    [InlineData("missing-capability")]
    [InlineData("incomplete-pair")]
    public async Task Public_initial_mixed_Import_and_Reference_delivers_only_complete_generation_after_restart(string scenario)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_COMPOSITION_SAMPLE_PATH");
        Assert.True(Path.IsPathFullyQualified(binary!) && File.Exists(binary), "Published nonfriend composition sample required.");
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var before = await Snapshot(pair);
        var catalogName = "additive_public_" + Guid.NewGuid().ToString("N"); databases.Add(catalogName);
        await Sql(Control(), $"CREATE DATABASE \"{catalogName}\"");
        var catalog = new NpgsqlConnectionStringBuilder(Control()) { Database = catalogName }.ConnectionString;
        var root = Path.Combine(Path.GetTempPath(), "haby303-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), """{"Logging":{"LogLevel":{"Default":"None"}},"Sample":{"Value":"transition"}}""");
        try
        {
            string credential;
            await using (var issue = new OperatorHostIntegrationTests.CompositionProcess(binary!, catalog, root,
                ["operator-token-issue", "--token-output-file", Path.Combine(root, "operator.token")]))
                await issue.RequireExitAsync(0);
            credential = (await File.ReadAllTextAsync(Path.Combine(root, "operator.token"))).TrimEnd();
            DurableCommitSummary? committed = null;
            ConfigurationAccessReply? grant = null;
            Guid[] completedReceipts = [];
            int completedAttempts = 0;
            for (var startup = 0; startup < 2; startup++)
            {
                var port = OperatorHostCompositionTests.AvailablePort();
                await using var child = new OperatorHostIntegrationTests.CompositionProcess(binary!, catalog, root,
                    ["--contentRoot=" + root, "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"], retainDiagnostics: true);
                await child.ExpectAsync("composition-built"); await child.SendAsync("start"); await child.ExpectAsync("composition-started");
                using var channel = GrpcChannel.ForAddress("http://127.0.0.1:" + port);
                var client = OperatorTestCredentials.Client(channel, credential);
                if (startup == 0)
                {
                    var checkedPlans = 0;
                    var environment = await client.CreateEnvironmentAsync(new() { Name = "pilot" });
                    Assert.Equal(EnvironmentStatus.Created, environment.Status);
                    var control = new NpgsqlConnectionStringBuilder(Control());
                    await Secret("administrator", "ProviderAdministrator", control.Password!); control.Password = null;
                    await Secret("shared", "SharedApplicationCredential", "synthetic-shared-material");
                    var provider = JsonNode.Parse("""
                        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"postgresql","environment":"pilot"},"spec":{
                         "revision":1,"providerKind":"postgresql/v1","configurationContract":"haby.postgresql.connection/v1","implementationId":"haby.postgresql.database-checkpoint/v1",
                         "properties":{"host":"127.0.0.1","port":5432,"maintenanceDatabase":"postgres","administratorUserName":"postgres","transportMode":"disable"},
                         "secretRefs":{"administrator-password":{"secretName":"administrator","version":1}}}}
                        """u8)!;
                    provider["spec"]!["properties"]!["port"] = control.Port;
                    provider["spec"]!["implementationId"] = PostgreSqlDatabaseCheckpointCapability.ImplementationId;
                    Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(provider.ToJsonString()) })).Status);
                    Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "postgresql.primary", ExpectedAbsent = true, Target = new() { Name = "postgresql", Revision = 1 } })).Status);
                    var envelope = Declaration("transition-consumer");
                    Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8("""
                        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"sample","environment":"pilot"},"spec":{
                         "revision":1,"providerKind":"sample.service/v1","configurationContract":"sample.service.configuration/v1","implementationId":"sample.service.impl/v1",
                         "properties":{"Endpoint":"https://service.example.invalid/"},"secretRefs":{"system":{"secretName":"shared","version":1},"application":{"secretName":"shared","version":1}}}}
                        """) })).Status);
                    await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "sample.primary", ExpectedAbsent = true, Target = new() { Name = "sample", Revision = 1 } });
                    envelope["manifest"]!["spec"]!["resources"]!["service"] = JsonNode.Parse("""{"type":"sample.reference/v1","providerRef":"sample.primary","properties":{}}"""u8);
                    envelope["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]!["service"] = JsonNode.Parse("""
                        {"target":{"kind":"local-resource","resourceRef":"service"},"options":{"label":"combined"},"documentProjections":[{"documentRef":"settings","path":"/Service","rendererRef":"sample.access-json/v1"}]}
                        """u8);
                    if (scenario == "missing-capability") envelope["manifest"]!["spec"]!["resources"]!["service"]!["type"] = "sample.import/v1";
                    var next = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(envelope.ToJsonString()) });
                    Assert.Equal(ImportReply.OutcomeOneofCase.Created, next.OutcomeCase);
                    var request = new CreateResourceImportChangeSetRequest
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        EnvironmentRecordId = environment.Environment.RecordId,
                        Reference = next.Created.Revision.Reference,
                        InputFormat = PostgreSqlImportPreparer.NamesFormat,
                        InputJson = ByteString.CopyFromUtf8(PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema)).Json),
                        CredentialMaterial = ByteString.CopyFromUtf8(pair.Password),
                        CredentialSlot = "application-password"
                    };
                    Assert.Equal(ImportStatus.ImportNotSupported, (await client.CreateResourceImportChangeSetAsync(request.Clone())).Status);
                    await OperatorHostIntegrationTests.AuthDenied(async () => await new ManifestOperator.ManifestOperatorClient(channel).StageResourceImportAsync(request.Clone()));
                    var staged = await client.StageResourceImportAsync(request); request.CredentialMaterial = ByteString.Empty;
                    Assert.Equal(ImportStatus.Pending, staged.Status);
                    var changeRequest = new CreateChangeSetRequest
                    {
                        EnvironmentId = "pilot",
                        Reference = next.Created.Revision.Reference,
                        Precondition = PlanningPrecondition.ExpectedAbsence,
                        Acquisitions = new()
                    };
                    foreach (var resource in new[] { "db", "app-login" }) changeRequest.Acquisitions.Resources.Add(new ResourceAcquisitionMember
                    {
                        Resource = resource,
                        Mode = AcquisitionMode.ImportUseAndRetain,
                        ImportRequestId = request.RequestId
                    });
                    changeRequest.Acquisitions.Resources.Add(new ResourceAcquisitionMember
                    {
                        Resource = "service",
                        Mode = AcquisitionMode.Reference,
                        Input = new() { Format = "sample.reference-input/v1", Json = "{\"Identity\":\"sample-service\"}" }
                    });
                    await OperatorHostIntegrationTests.AuthDenied(async () => await new ManifestOperator.ManifestOperatorClient(channel).CreateChangeSetAsync(changeRequest));
                    if (scenario is "incomplete-pair" or "missing-capability")
                    {
                        if (scenario == "incomplete-pair") changeRequest.Acquisitions.Resources.RemoveAt(0);
                        Assert.Equal(scenario == "incomplete-pair" ? PlanningStatus.ImportNotSupported : PlanningStatus.ReferenceNotSupported,
                            (await client.CreateChangeSetAsync(changeRequest)).Status);
                        await using (var refused = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                        {
                            Assert.Empty(await refused.DurableChangeSets.AsNoTracking().ToArrayAsync());
                            Assert.Empty(await refused.ApplicationDeployments.AsNoTracking().ToArrayAsync());
                            Assert.Empty(await refused.ResourceManagement.AsNoTracking().ToArrayAsync());
                        }
                        await child.SendAsync("stop"); await child.ExpectAsync("composition-stopped"); await child.RequireExitAsync(0);
                        break;
                    }
                    var change = await client.CreateChangeSetAsync(changeRequest); Assert.Equal(PlanningStatus.Created, change.Status);
                    Assert.Equal(change.ChangeSet.ChangeSetId, (await client.CreateChangeSetAsync(changeRequest)).ChangeSet.ChangeSetId);
                    committed = await Confirm(change.ChangeSet.ChangeSetId);
                    Assert.Equal(change.ChangeSet.ChangeSetId, (await client.CreateChangeSetAsync(changeRequest)).ChangeSet.ChangeSetId);
                    var incompleteReplay = changeRequest.Clone();
                    incompleteReplay.Acquisitions.Resources.RemoveAt(incompleteReplay.Acquisitions.Resources.Count - 1);
                    Assert.Equal(PlanningStatus.InvalidRequest, (await client.CreateChangeSetAsync(incompleteReplay)).Status);
                    Assert.NotEqual(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await child.AtDiagnosticStageAsync(CompositionDiagnosticStage.InitialMixedMaterialization,
                            async () => await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" }))).Status);
                    if (scenario == "import-failure") await Sql(Control(), $"ALTER ROLE \"{pair.Login}\" NOLOGIN");
                    if (scenario == "reference-failure")
                    {
                        var applied = await client.GetProviderAsync(new() { Environment = "pilot", Name = "sample", Revision = 1 });
                        var bindings = await client.GetSharedCredentialBindingsAsync(new() { Environment = "pilot", ProviderInstanceId = applied.Provider.InstanceId, Revision = 1 });
                        var systemUse = bindings.Bindings.Single(x => x.Use == "SystemAccess");
                        Assert.Equal("Revoked", (await client.RevokeSharedCredentialUseAsync(new()
                        {
                            Environment = "pilot",
                            ProviderInstanceId = applied.Provider.InstanceId,
                            Revision = 1,
                            Slot = systemUse.Slot,
                            Use = systemUse.Use,
                            SecretId = systemUse.SecretId,
                            SecretVersionId = systemUse.SecretVersionId
                        })).Status);
                    }
                    if (scenario is "import-failure" or "reference-failure")
                    {
                        Assert.NotEqual(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed.DeploymentId, Generation = committed.Generation })).Status);
                        Assert.NotEqual(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await child.AtDiagnosticStageAsync(CompositionDiagnosticStage.InitialMixedMaterialization,
                            async () => await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" }))).Status);
                        Assert.NotEqual(ConfigurationAccessStatus.Created, (await client.CreateConfigurationAccessAsync(new() { Selector = new() { Environment = "pilot", ApplicationId = "transition-consumer", ComponentId = "worker", DocumentId = "settings" } })).Status);
                        await using (var refused = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                        {
                            Assert.Null((await refused.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                            completedReceipts = await refused.ResourceObservationReceipts.AsNoTracking().Select(x => x.Id).ToArrayAsync();
                            Assert.Equal(scenario == "reference-failure" ? 1 : 0, completedReceipts.Length);
                            Assert.Equal(scenario == "reference-failure" ? 2 : 0, await refused.OperationOutputs.CountAsync());
                            completedAttempts = await refused.DurableOperationAttempts.CountAsync(x => x.OperationId != "service-reference");
                        }
                        if (scenario == "import-failure") await Sql(Control(), $"ALTER ROLE \"{pair.Login}\" LOGIN");
                        await child.SendAsync("stop"); await child.ExpectAsync("composition-stopped"); await child.RequireExitAsync(0);
                        continue;
                    }
                    Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed.DeploymentId, Generation = committed.Generation })).Status);
                    Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await child.AtDiagnosticStageAsync(CompositionDiagnosticStage.InitialMixedMaterialization,
                            async () => await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" }))).Status);
                    await using (var confirmed = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                    {
                        var owned = await confirmed.ResourceManagement.AsNoTracking().OrderBy(x => x.Resource).ToArrayAsync();
                        Assert.Equal(3, owned.Length);
                        Assert.All(owned, x => Assert.Equal(committed.Generation, x.SourceGeneration));
                        Assert.All(owned.Where(x => x.Resource != "service"), x => { Assert.Equal(ResourceOrigin.Imported, x.Origin); Assert.Equal(ResourceLifecycleAuthority.UseAndRetain, x.Authority); });
                        Assert.Equal(ResourceOrigin.Referenced, owned.Single(x => x.Resource == "service").Origin);
                        Assert.Equal(ResourceLifecycleAuthority.ConsumptionOnly, owned.Single(x => x.Resource == "service").Authority);
                        Assert.Single(await confirmed.ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Resource == "app-login" && x.ConfirmedReceipt != null).ToArrayAsync());
                        Assert.Equal(2, await confirmed.ResourceObservationReceipts.CountAsync());
                    }
                    if (scenario == "successive")
                    {
                        Guid[] receipts;
                        AdditiveImportAuthority[] authority;
                        AdditiveImportCredential[] credentials;
                        await using (var initial = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                        {
                            receipts = await initial.ResourceObservationReceipts.AsNoTracking().Select(x => x.Id).ToArrayAsync();
                            authority = await initial.ResourceManagement.AsNoTracking().OrderBy(x => x.Resource).Select(x => new AdditiveImportAuthority(x.Id, x.Origin, x.Authority, x.SourceGeneration)).ToArrayAsync();
                            credentials = await initial.ProtectedApplicationCredentials.AsNoTracking().Select(x => new AdditiveImportCredential(x.Id, x.Secret, x.Version, x.ConfirmedReceipt)).ToArrayAsync();
                        }
                        envelope["applicationVersion"] = "v2";
                        envelope["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]!["service"]!["options"]!["label"] = "successive";
                        var successor = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(envelope.ToJsonString()) });
                        Assert.Equal(ImportReply.OutcomeOneofCase.Created, successor.OutcomeCase);
                        var reuse = await client.CreateChangeSetAsync(new()
                        {
                            EnvironmentId = "pilot",
                            Reference = successor.Created.Revision.Reference,
                            Precondition = PlanningPrecondition.ExpectedGeneration,
                            ExpectedGeneration = committed.Generation
                        });
                        Assert.Equal(PlanningStatus.Created, reuse.Status);
                        committed = await Confirm(reuse.ChangeSet.ChangeSetId);
                        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed.DeploymentId, Generation = committed.Generation })).Status);
                        Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await child.AtDiagnosticStageAsync(CompositionDiagnosticStage.InitialMixedMaterialization,
                            async () => await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" }))).Status);
                        await using var reused = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                        Assert.Equal(receipts, await reused.ResourceObservationReceipts.AsNoTracking().Select(x => x.Id).ToArrayAsync());
                        Assert.Equal(authority, await reused.ResourceManagement.AsNoTracking().OrderBy(x => x.Resource).Select(x => new AdditiveImportAuthority(x.Id, x.Origin, x.Authority, x.SourceGeneration)).ToArrayAsync());
                        Assert.Equal(credentials, await reused.ProtectedApplicationCredentials.AsNoTracking().Select(x => new AdditiveImportCredential(x.Id, x.Secret, x.Version, x.ConfirmedReceipt)).ToArrayAsync());
                    }
                    grant = await client.CreateConfigurationAccessAsync(new() { Selector = new() { Environment = "pilot", ApplicationId = "transition-consumer", ComponentId = "worker", DocumentId = "settings" } });
                    Assert.Equal(ConfigurationAccessStatus.Created, grant.Status);
                    async Task Secret(string name, string purpose, string material) => Assert.Equal(SecretStatus.Created, (await client.CreateSecretAsync(new() { Environment = "pilot", Name = name, Purpose = purpose, RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8(material) })).Status);
                    async Task<DurableCommitSummary> Confirm(string change)
                    {
                        var plan = await client.CreatePlanAsync(new() { ChangeSetId = change }); Assert.Equal(PlanningStatus.Planned, plan.Status);
                        var stage = ++checkedPlans == 1 ? CompositionDiagnosticStage.InitialProtectedImportBaselineCheckPlan : CompositionDiagnosticStage.AdditiveCheckPlan;
                        Assert.Equal(PlanningStatus.Current, (await child.AtDiagnosticStageAsync(stage,
                            async () => await client.CheckPlanAsync(new() { PlanId = plan.Plan.PlanId }))).Status);
                        var commit = await client.CommitDesiredStateAsync(new() { ChangeSetId = change, PlanId = plan.Plan.PlanId }); Assert.Equal(CommitStatus.Committed, commit.Status); return commit.Commit;
                    }
                }
                if (scenario is "import-failure" or "reference-failure")
                {
                    Assert.NotEqual(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed!.DeploymentId, Generation = committed.Generation })).Status);
                    await using (var retry = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                    {
                        Assert.Equal(completedReceipts, await retry.ResourceObservationReceipts.AsNoTracking().Select(x => x.Id).ToArrayAsync());
                        Assert.Equal(completedAttempts, await retry.DurableOperationAttempts.CountAsync(x => x.OperationId != "service-reference"));
                        Assert.Null((await retry.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                    }
                    await child.SendAsync("stop"); await child.ExpectAsync("composition-stopped"); await child.RequireExitAsync(0);
                    continue;
                }
                Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed!.DeploymentId, Generation = committed.Generation })).Status);
                var delivery = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
                var delivered = await delivery.DeliverAsync(new() { RevisionId = grant!.Grant.Document.RevisionId }, new Metadata { { "authorization", "Bearer " + grant.Credential } });
                using var json = JsonDocument.Parse(delivered.Json.Memory);
                var connection = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("Connection"u8).GetString());
                Assert.True(connection.Password == pair.Password, "Retained credential changed."); connection.Password = null;
                Assert.Equal("synthetic-shared-material", json.RootElement.GetProperty("Service"u8).GetProperty("credential"u8).GetString());
                await child.SendAsync("stop"); await child.ExpectAsync("composition-stopped"); await child.RequireExitAsync(0);
            }
            Assert.True(before == await Snapshot(pair), "Transition mutated existing PostgreSQL state.");
            Assert.True(await PasswordUnchanged(passwordGuard, pair), "Transition changed existing password verifier.");
        }
        finally { Directory.Delete(root, true); }
    }
}
