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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_import_baseline_adds_other_Provider_Reference_and_delivers_combined_configuration_after_restart(bool drift)
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
                    var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(envelope.ToJsonString()) });
                    Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase);
                    var request = new CreateResourceImportChangeSetRequest { RequestId = Guid.NewGuid().ToString(), EnvironmentRecordId = environment.Environment.RecordId,
                        Reference = imported.Created.Revision.Reference, InputFormat = PostgreSqlImportPreparer.NamesFormat,
                        InputJson = ByteString.CopyFromUtf8(PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema)).Json), CredentialMaterial = ByteString.CopyFromUtf8(pair.Password) };
                    var accepted = await client.CreateResourceImportChangeSetAsync(request); request.CredentialMaterial = ByteString.Empty;
                    Assert.Equal(ImportStatus.Created, accepted.Status);
                    var baseline = await Confirm(accepted.Request.ChangeSetId);
                    Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = baseline.DeploymentId, Generation = baseline.Generation })).Status);
                    await using var baselineRead = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                    var authority = await baselineRead.ResourceManagement.AsNoTracking().OrderBy(x => x.Resource).Select(x => new { x.Id, x.Origin, x.Authority, x.SourceGeneration }).ToArrayAsync();
                    var originalCredentials = await baselineRead.ProtectedApplicationCredentials.AsNoTracking().Select(x => new { x.Id, x.Secret, x.Version, x.ConfirmedReceipt }).ToArrayAsync();
                    Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8("""
                        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"sample","environment":"pilot"},"spec":{
                         "revision":1,"providerKind":"sample.service/v1","configurationContract":"sample.service.configuration/v1","implementationId":"sample.service.impl/v1",
                         "properties":{"Endpoint":"https://service.example.invalid/"},"secretRefs":{"system":{"secretName":"shared","version":1},"application":{"secretName":"shared","version":1}}}}
                        """) })).Status);
                    await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "sample.primary", ExpectedAbsent = true, Target = new() { Name = "sample", Revision = 1 } });
                    envelope["applicationVersion"] = "v2";
                    envelope["manifest"]!["spec"]!["resources"]!["service"] = JsonNode.Parse("""{"type":"sample.reference/v1","providerRef":"sample.primary","properties":{}}"""u8);
                    envelope["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]!["service"] = JsonNode.Parse("""
                        {"target":{"kind":"local-resource","resourceRef":"service"},"options":{"label":"combined"},"documentProjections":[{"documentRef":"settings","path":"/Service","rendererRef":"sample.access-json/v1"}]}
                        """u8);
                    var next = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(envelope.ToJsonString()) });
                    Assert.Equal(ImportReply.OutcomeOneofCase.Created, next.OutcomeCase);
                    var changeRequest = new CreateChangeSetRequest { EnvironmentId = "pilot", Reference = next.Created.Revision.Reference,
                        Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = baseline.Generation, Acquisitions = new() };
                    changeRequest.Acquisitions.Resources.Add(new ResourceAcquisitionMember { Resource = "service", Mode = AcquisitionMode.Reference,
                        Input = new() { Format = "sample.reference-input/v1", Json = "{\"Identity\":\"sample-service\"}" } });
                    await OperatorHostIntegrationTests.AuthDenied(async () => await new ManifestOperator.ManifestOperatorClient(channel).CreateChangeSetAsync(changeRequest));
                    var change = await client.CreateChangeSetAsync(changeRequest); Assert.Equal(PlanningStatus.Created, change.Status);
                    committed = await Confirm(change.ChangeSet.ChangeSetId);
                    Assert.NotEqual(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                    if (drift)
                    {
                        await Sql(new NpgsqlConnectionStringBuilder(Control()) { Database = pair.Database }.ConnectionString,
                            $"ALTER SCHEMA public OWNER TO \"{pair.Login}\"");
                        Assert.Equal(GenerationExecutionStatus.Failed, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed.DeploymentId, Generation = committed.Generation })).Status);
                        Assert.NotEqual(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                        await using var refused = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                        Assert.Equal(baseline.Generation, (await refused.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                        Assert.Single(await refused.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
                        Assert.Empty(await refused.OperationOutputs.AsNoTracking().Where(x => x.Generation == committed.Generation).ToArrayAsync());
                        Assert.Equal(originalCredentials, await refused.ProtectedApplicationCredentials.AsNoTracking().Select(x => new { x.Id, x.Secret, x.Version, x.ConfirmedReceipt }).ToArrayAsync());
                        return;
                    }
                    Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed.DeploymentId, Generation = committed.Generation })).Status);
                    Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                    grant = await client.CreateConfigurationAccessAsync(new() { Selector = new() { Environment = "pilot", ApplicationId = "transition-consumer", ComponentId = "worker", DocumentId = "settings" } });
                    Assert.Equal(ConfigurationAccessStatus.Created, grant.Status);
                    await using var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                    Assert.Equal(authority, await verify.ResourceManagement.AsNoTracking().Where(x => x.Resource != "service").OrderBy(x => x.Resource).Select(x => new { x.Id, x.Origin, x.Authority, x.SourceGeneration }).ToArrayAsync());
                    Assert.Equal(originalCredentials, await verify.ProtectedApplicationCredentials.AsNoTracking().Select(x => new { x.Id, x.Secret, x.Version, x.ConfirmedReceipt }).ToArrayAsync());
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_import_replay_refuses_modified_credential_owner_scope(bool application)
    {
        var setup = await AcceptedPair(await ExistingPair());
        await using var services = Services(setup.Connection);
        var acceptance = services.GetRequiredService<IResourceObservationAcceptance>();
        var receipt = (await acceptance.FindAsync(setup.Key, default))!;
        ProviderResourceReservation[] reservations;
        await using (var read = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            reservations = (await read.ProviderResourceReservations.AsNoTracking().ToArrayAsync()).Select(x => new ProviderResourceReservation(
                Haby.Domain.ProviderInstanceId.Parse(x.ProviderInstanceId.ToString()), setup.Owner.EnvironmentRecordId, setup.Owner.ApplicationRecordId,
                Haby.Domain.ManifestLocalId.Create(x.ResourceLocalId), x.Namespace, x.Value)).ToArray();
        var owner = application ? setup.Owner with { ApplicationRecordId = Haby.Domain.ApplicationRecordId.New() }
            : setup.Owner with { EnvironmentRecordId = Haby.Domain.EnvironmentRecordId.New() };
        var request = new ResourceObservationAcceptance(receipt.Operations, [new(setup.Owner, setup.Staged)], reservations, receipt.Evidence);
        var modified = request with { Credentials = [new(owner, setup.Staged)] };
        Assert.Equal(ResourceObservationAcceptanceStatus.InputUnavailable, (await acceptance.CompleteAsync(modified, DateTimeOffset.UtcNow, default)).Status);
        Assert.Equal(ResourceObservationAcceptanceStatus.Existing, (await acceptance.CompleteAsync(request, DateTimeOffset.UtcNow, default)).Status);
        await AssertOriginalReceipt(setup);
    }

}
