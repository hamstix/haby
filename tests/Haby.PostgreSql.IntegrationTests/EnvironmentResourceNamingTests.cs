using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public abstract partial class ApplicationLoginScenario
{
    private protected async Task<(string Connection, EnvironmentRecordId Environment, ProviderInstanceId Provider)> NamingCatalog()
    {
        var connection = _catalogPools.Register(new NpgsqlConnectionStringBuilder(await Database("naming_catalog_")));
        await using var services = Services(connection, full: true);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        var bytes = Encoding.UTF8.GetBytes(target.Password!);
        try { await secrets.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), secret, bytes, default); }
        finally { Array.Clear(bytes); }
        var configuration = new PostgreSqlProviderConfiguration(1, target.Host!, target.Port, target.Database!, target.Username!, secret, PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var provider = await providers.CreateInstanceAsync(environment, configuration, default);
        await providers.AddRevisionAsync(environment, provider, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
            ProviderAlias.Create("postgresql.primary"), null, new(provider, ProviderConfigurationRevisionNumber.Create(1)), "fixture", default)).Outcome);
        return (connection, environment, provider);
    }

}

[Collection("Login logging isolated")]
public sealed partial class ApplicationGenerationTests(PostgreSqlFixture fixture) : ApplicationLoginScenario(fixture), IClassFixture<PostgreSqlFixture>
{

    [Fact]
    public async Task Naming_same_manifest_two_authenticated_native_hosts_one_server_freezes_names_and_delivers_real_isolated_access()
    {
        var declaration = PostgreSqlPairPlanningTests.Example();
        var logical = "orders_" + Guid.NewGuid().ToString("N")[..16];
        declaration["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = logical;
        foreach (var component in new[] { "api", "worker" })
            declaration["manifest"]!["spec"]!["components"]![component]!["bindings"]!["storage"]!["configurationSlot"] = "main-database";
        declaration["manifest"]!["spec"]!["components"]!["api"]!["bindings"]!["storage"]!["options"] = System.Text.Json.Nodes.JsonNode.Parse("{\"timeout\":200}"u8);
        var bytes = PostgreSqlPairPlanningTests.Bytes(declaration);
        var manifestFile = Path.Combine(Path.GetTempPath(), "haby-naming-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        var policyFile = Path.Combine(Path.GetTempPath(), "haby-naming-policy-" + Guid.NewGuid().ToString("N") + ".json");
        var catalogs = new[] { await NamingCatalog(), await NamingCatalog() };
        var accepted = new List<Scenario>();
        var grants = new List<(ConfigurationDocumentReply Document, ConfigurationAccessReply Grant)>();
        var settings = new List<NpgsqlConnectionStringBuilder>();
        try
        {
            await File.WriteAllBytesAsync(manifestFile, bytes);
            for (var index = 0; index < 2; index++)
            {
                var catalog = catalogs[index]; var prefix = index == 0 ? "staging_" : "staging_shard_";
                await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + catalog.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
                await File.WriteAllTextAsync(policyFile, "{\"mode\":\"prefixed\",\"prefix\":\"" + prefix + "\"}");
                _ = await NativeReuseCommand(host.Urls.Single(), "resource-naming", "create", "contour", "--revision", "1", "--file", policyFile);
                var created = await client.CreateResourceNamingAsync(new() { Reference = new() { Environment = "pilot", Name = "contour", Revision = 1 }, DeclarationJson = await File.ReadAllTextAsync(policyFile) });
                Assert.Contains(created.Status, new[] { ResourceNamingStatus.Created, ResourceNamingStatus.Unchanged });
                _ = await NativeReuseCommand(host.Urls.Single(), "resource-naming", "select", "postgresql.primary", "--policy", "contour", "--revision", "1");
                var selection = await client.GetResourceNamingSelectionAsync(new() { Environment = "pilot", Alias = "postgresql.primary" });
                if (!selection.HasToken)
                    selection = await client.SelectResourceNamingAsync(new()
                    {
                        Binding = new() { Environment = "pilot", Alias = "postgresql.primary" },
                        Name = "contour",
                        Revision = 1,
                        ExpectedProviderInstanceId = catalog.Provider.ToString(),
                        ExpectedConfigurationRevision = 1,
                        ExpectedAbsence = true
                    });
                Assert.Equal(prefix, selection.Prefix);
                _ = await NativeReuseCommand(host.Urls.Single(), "resource-naming", "get", "contour", "--revision", "1");
                _ = await NativeReuseCommand(host.Urls.Single(), "resource-naming", "selection", "postgresql.primary");
                Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(new() { Payload = ByteString.CopyFromUtf8(RenderingProfile) })).Status);
                Assert.Equal(ProfileAssignmentStatus.Created, (await client.ApplyProfileAssignmentAsync(new()
                {
                    Environment = "pilot",
                    ExpectedAbsence = true,
                    Payload = ByteString.CopyFromUtf8("""
                    {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfileAssignment","spec":{"target":{"kind":"environment"},"decisions":[{"kind":"use","profileRef":{"profileId":"connection-layout","revision":1}}]}}
                    """)
                })).Status);
                _ = await NativeReuseCommand(host.Urls.Single(), "manifest", "import", manifestFile);
                var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(bytes) });
                var revision = imported.Created?.Revision.Reference ?? imported.ExistingEquivalent.Revision.Reference;
                var nativeChange = await NativeReuseCommand(host.Urls.Single(), "changeset", "create", revision.ApplicationRecordId, revision.RevisionNumber.ToString(), "--expected-absence");
                var change = nativeChange is { } c ? await client.GetChangeSetAsync(new() { ChangeSetId = c.GetProperty("data"u8).GetProperty("changeSet"u8).GetProperty("changeSetId"u8).GetString()! }) :
                    await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedAbsence });
                var nativePlan = await NativeReuseCommand(host.Urls.Single(), "plan", "create", change.ChangeSet.ChangeSetId);
                var plan = nativePlan is { } p ? await client.GetPlanAsync(new() { PlanId = p.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("planId"u8).GetString()! }) :
                    await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
                Assert.NotNull(plan.Plan);
                using var plannedNaming = JsonDocument.Parse(JsonDocument.Parse(plan.Plan.ProviderInputs.Single(x => x.ResourceId == "db").NamingJson).RootElement.GetProperty("snapshot"u8).GetString()!);
                Assert.Equal(prefix, plannedNaming.RootElement.GetProperty("policy"u8).GetProperty("prefix"u8).GetString());
                Assert.Equal(1, plannedNaming.RootElement.GetProperty("reference"u8).GetProperty("revision"u8).GetInt64());
                Assert.Equal(2, plan.Plan.ProviderInputs.Count);
                _ = await NativeReuseCommand(host.Urls.Single(), "plan", "check", plan.Plan.PlanId);
                _ = await NativeReuseCommand(host.Urls.Single(), "changeset", "commit", change.ChangeSet.ChangeSetId, "--plan", plan.Plan.PlanId);
                var commit = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId, PlanId = plan.Plan.PlanId });
                Assert.Equal(CommitStatus.Committed, commit.Status);
                var saved = await host.Services.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(DeploymentPlanId.Parse(plan.Plan.PlanId), default);
                var receipt = (await host.Services.GetRequiredService<IDurableDesiredStateCommitter>().FindCommitAsync(DeploymentChangeSetId.Parse(change.ChangeSet.ChangeSetId), default))!;
                var scenario = new Scenario(catalog.Connection, prefix + logical, receipt, saved!) { DeclaredDatabase = logical };
                Assert.Equal(scenario.Database, plan.Plan.Operations.Single(o => o.OperationId == "db-create").TargetName);
                Assert.Equal(scenario.Role, plan.Plan.Operations.Single(o => o.OperationId == "app-login-create").TargetName);
                accepted.Add(scenario);
                Assert.Null(await DatabaseOid(scenario.Database)); Assert.False(await Exists(scenario.Role));
                await using (var verify = Context(catalog.Connection)) { Assert.Empty(await verify.GeneratedCredentialSlots.ToArrayAsync()); Assert.Empty(await verify.DurableOperationAttempts.ToArrayAsync()); }
                // Mutable selection changes AFTER Commit cannot change accepted operation names.
                Assert.Equal(ResourceNamingStatus.Created, (await client.CreateResourceNamingAsync(new() { Reference = new() { Environment = "pilot", Name = "contour", Revision = 2 }, DeclarationJson = "{\"mode\":\"prefixed\",\"prefix\":\"other_\"}" })).Status);
                Assert.Equal(ResourceNamingStatus.Changed, (await client.SelectResourceNamingAsync(new()
                {
                    Binding = new() { Environment = "pilot", Alias = "postgresql.primary" },
                    Name = "contour",
                    Revision = 2,
                    ExpectedProviderInstanceId = catalog.Provider.ToString(),
                    ExpectedConfigurationRevision = 1,
                    ExpectedToken = selection.Token
                })).Status);
                await host.StopAsync();
            }
            Assert.NotEqual(accepted[0].Owner, accepted[1].Owner); Assert.NotEqual(accepted[0].Role, accepted[1].Role);
            for (var index = 0; index < 2; index++)
            {
                var scenario = accepted[index];
                await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
                var pending = await client.GetGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 });
                using var acceptedNaming = JsonDocument.Parse(JsonDocument.Parse(pending.DesiredGeneration.ProviderInputs.Single(x => x.ResourceId == "db").NamingJson).RootElement.GetProperty("snapshot"u8).GetString()!);
                Assert.Equal(1, acceptedNaming.RootElement.GetProperty("reference"u8).GetProperty("revision"u8).GetInt64());
                Assert.Equal(scenario.Plan.Inputs.Providers.Resource(ManifestLocalId.Create("db")).ResourceNaming().Policy.Prefix, acceptedNaming.RootElement.GetProperty("policy"u8).GetProperty("prefix"u8).GetString());
                _ = await NativeReuseCommand(host.Urls.Single(), "deployment", "generation", "execute", scenario.Commit.DeploymentId.ToString(), "1");
                Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 })).Status);
                _ = await NativeReuseCommand(host.Urls.Single(), "configuration", "materialize", DocumentRequest(scenario).ApplicationId, "--all", "--generation", "1");
                var document = await client.MaterializeConfigurationAsync(DocumentRequest(scenario, exact: true));
                ConfigurationAccessReply grant;
                var credentialFile = Path.Combine(Path.GetTempPath(), "haby-naming-delivery-" + Guid.NewGuid().ToString("N") + ".token");
                try
                {
                    var nativeGrant = await NativeReuseCommand(host.Urls.Single(), "configuration", "access", "create", DocumentRequest(scenario).ApplicationId,
                        "--component", "api", "--document", "settings", "--generation", "1", "--token-output-file", credentialFile);
                    if (nativeGrant is { } granted)
                    {
                        Assert.Equal(document.Artifact.RevisionId, granted.GetProperty("data"u8).GetProperty("revisionId"u8).GetString());
                        grant = new() { Status = ConfigurationAccessStatus.Created, Credential = (await File.ReadAllTextAsync(credentialFile)).TrimEnd('\r', '\n') };
                    }
                    else grant = await client.CreateConfigurationAccessAsync(new() { Selector = DocumentRequest(scenario, exact: true) });
                }
                finally { File.Delete(credentialFile); }
                grants.Add((document, grant));
                var builder = await Receive(channel, document, grant, scenario);
                settings.Add(builder);
                await using (var runtime = new NpgsqlConnection(builder.ConnectionString))
                {
                    await runtime.OpenAsync();
                    await new NpgsqlCommand("CREATE TABLE app.naming_sentinel(id integer PRIMARY KEY, value text NOT NULL); CREATE TABLE app.naming_writes(id integer)", runtime).ExecuteNonQueryAsync();
                    await using var insert = new NpgsqlCommand("INSERT INTO app.naming_sentinel VALUES (175, $1)", runtime); insert.Parameters.AddWithValue("original-" + index); await insert.ExecuteNonQueryAsync();
                }
                var history = await ReuseHistory(scenario.Connection); var identity = await ExternalIdentity(scenario);
                var changed = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = SameRevision(scenario), Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 1 });
                Assert.Equal(PlanningStatus.UnsupportedShape, (await client.CreatePlanAsync(new() { ChangeSetId = changed.ChangeSet.ChangeSetId })).Status);
                Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(identity, await ExternalIdentity(scenario));
                // Return to the original policy. Its new selection token is a significant input,
                // while compatibility still requires unchanged original physical names.
                var current = await client.GetResourceNamingSelectionAsync(new() { Environment = "pilot", Alias = "postgresql.primary" });
                Assert.Equal(ResourceNamingStatus.Changed, (await client.SelectResourceNamingAsync(new()
                {
                    Binding = new() { Environment = "pilot", Alias = "postgresql.primary" },
                    Name = "contour",
                    Revision = 1,
                    ExpectedProviderInstanceId = catalogs[index].Provider.ToString(),
                    ExpectedConfigurationRevision = 1,
                    ExpectedToken = current.Token
                })).Status);
                var update = await UpdatePlan(client, SameRevision(scenario), 1, host.Urls.Single());
                Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = update.ChangeSetId, PlanId = update.PlanId })).Status);
                Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
                var selector = DocumentRequest(scenario, exact: true); selector.Generation = 2;
                var nextDocument = await client.MaterializeConfigurationAsync(selector); var nextGrant = await client.CreateConfigurationAccessAsync(new() { Selector = selector });
                var wrongDocument = await Assert.ThrowsAsync<RpcException>(async () => await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                    .DeliverAsync(new() { RevisionId = nextDocument.Artifact.RevisionId }, Bearer(grants[index].Grant.Credential)));
                Assert.Equal(StatusCode.PermissionDenied, wrongDocument.StatusCode);
                foreach (var pair in new[] { grants[index], (nextDocument, nextGrant) })
                {
                    var received = await Receive(channel, pair.Item1, pair.Item2, scenario);
                    Assert.True(builder.Password == received.Password, "Credential changed.");
                    await using var runtime = new NpgsqlConnection(received.ConnectionString); await runtime.OpenAsync();
                    Assert.Equal("original-" + index, await new NpgsqlCommand("SELECT value FROM app.naming_sentinel WHERE id = 175", runtime).ExecuteScalarAsync());
                    await new NpgsqlCommand("INSERT INTO app.naming_writes VALUES (175)", runtime).ExecuteNonQueryAsync();
                }
                Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(identity, await ExternalIdentity(scenario));
                await host.StopAsync();
            }
            Assert.True(settings[0].Password != settings[1].Password, "Independent catalogs shared credential material.");
            for (var index = 0; index < 2; index++)
            {
                settings[index].Database = accepted[1 - index].Database; settings[index].Pooling = false;
                var denied = false;
                try { await using var cross = new NpgsqlConnection(settings[index].ConnectionString); await cross.OpenAsync(); }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { denied = true; }
                Assert.True(denied, "Cross-catalog database access was admitted.");
            }
        }
        finally { foreach (var builder in settings) builder.Password = null; File.Delete(manifestFile); File.Delete(policyFile); }

        static async Task<NpgsqlConnectionStringBuilder> Receive(GrpcChannel channel, ConfigurationDocumentReply document, ConfigurationAccessReply grant, Scenario scenario)
        {
            var delivered = await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                .DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
            using var json = JsonDocument.Parse(delivered.Json.Memory);
            var result = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString());
            Assert.Equal(scenario.Database, result.Database); Assert.Equal(scenario.Role, result.Username); Assert.Equal(200, result.Timeout);
            return result;
        }
    }

}
