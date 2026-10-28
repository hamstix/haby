using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    // Setup accepts an offline configuration through the existing store. Provider preflight is
    // deliberately a fixture double; every Host under test uses the production composition.
    private sealed class SeedPreflight : IProviderRevisionPreflight
    {
        public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment,
            ProviderAliasBinding target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
    }

    private sealed class PastClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(-2);
    }

    [Fact]
    public async Task Durable_planning_survives_Host_restart_pins_alias_inputs_and_never_contacts_Provider()
    {
        var connection = await NewDatabaseAsync();
        using var providerTrap = new TcpListener(IPAddress.Loopback, 0);
        providerTrap.Start();
        var providerPort = ((IPEndPoint)providerTrap.LocalEndpoint).Port;
        var providerContact = providerTrap.AcceptTcpClientAsync();
        const string material = "planning-secret-sentinel";
        await using var setup = new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
            .AddPostgreSqlPlaintextSecretStore().AddPostgreSqlProviderConfigurationCodec()
            .AddPostgreSqlProviderConfigurationStore().AddPostgreSqlProviderAliasStore()
            .AddSingleton<IProviderRevisionPreflight, SeedPreflight>()
            .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlDurableDeploymentPlanner().BuildServiceProvider();
        await setup.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = setup.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(environment, secret, Encoding.UTF8.GetBytes(material), default);
        var configuration = new PostgreSqlProviderConfiguration(1, "127.0.0.1", providerPort,
            "postgres", "never-connect", secret, PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        var providers = setup.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(environment, configuration, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(2), configuration, default);
        var aliasStore = setup.GetRequiredService<IProviderAliasStore>();
        var alias = ProviderAlias.Create("postgresql.primary");
        var firstBinding = new ProviderAliasBinding(instance, ProviderConfigurationRevisionNumber.Create(1));
        var nextBinding = new ProviderAliasBinding(instance, ProviderConfigurationRevisionNumber.Create(2));
        using var certificate = Certificate();
        var certificatePath = Path.Combine(Path.GetTempPath(), "haby-planning-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-only"));
        DurableChangeSetSummary change;
        DurablePlanSummary original;
        DurablePlanSummary replacement;
        try
        {
            await using (var host = await StartAsync(connection, certificatePath))
            {
                Assert.Null(host.Services.GetService<ISecretStore>());
                Assert.Null(host.Services.GetService<IProviderRevisionPreflight>());
                Assert.NotNull(host.Services.GetService<IDurableDesiredStateCommitter>());
                using var channel = Channel(host, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var imported = await client.ImportAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(Envelope("durable-operator")) });
                var reference = imported.Created.Revision.Reference;
                var create = new CreateChangeSetRequest
                {
                    EnvironmentId = "pilot",
                    Reference = reference,
                    Precondition = PlanningPrecondition.ExpectedAbsence
                };
                foreach (var precondition in new[] { PlanningPrecondition.Unspecified, PlanningPrecondition.ExpectedGeneration, (PlanningPrecondition)999 })
                    Assert.Equal(PlanningStatus.UnsupportedPrecondition, (await client.CreateChangeSetAsync(WithPrecondition(create, precondition))).Status);
                var missingEnvironment = create.Clone(); missingEnvironment.EnvironmentId = "missing";
                Assert.Equal(PlanningStatus.EnvironmentNotFound, (await client.CreateChangeSetAsync(missingEnvironment)).Status);
                var invalidEnvironment = create.Clone(); invalidEnvironment.EnvironmentId = environment.ToString();
                Assert.Equal(PlanningStatus.InvalidRequest, (await client.CreateChangeSetAsync(invalidEnvironment)).Status);
                var missingRevision = create.Clone(); missingRevision.Reference.RevisionNumber = 999;
                Assert.Equal(PlanningStatus.RevisionNotFound, (await client.CreateChangeSetAsync(missingRevision)).Status);
                var invalidRevision = create.Clone(); invalidRevision.Reference.ApplicationRecordId = "bad";
                Assert.Equal(PlanningStatus.InvalidRequest, (await client.CreateChangeSetAsync(invalidRevision)).Status);
                change = (await client.CreateChangeSetAsync(create)).ChangeSet;
                Assert.NotNull(change);
                Assert.Equal(OperatorHostApplication.TechnicalActorId, change.TechnicalActorId);
                var changeRequest = new ChangeSetRequest { ChangeSetId = change.ChangeSetId };
                Assert.Equal(change, (await client.GetChangeSetAsync(changeRequest)).ChangeSet);
                Assert.Equal(PlanningStatus.AliasNotResolved, (await client.CreatePlanAsync(changeRequest)).Status);
                Assert.Equal(ProviderAliasChangeOutcome.Changed,
                    (await aliasStore.ChangeAsync(environment, alias, null, firstBinding, "fixture", default)).Outcome);
                var planned = await client.CreatePlanAsync(changeRequest);
                Assert.Equal(PlanningStatus.Planned, planned.Status);
                Assert.DoesNotContain(material, planned.ToString());
                original = planned.Plan;
                Assert.Equal(1, original.ConfigurationRevision);
                Assert.Equal(instance.ToString(), original.ProviderInstanceId);
                Assert.Equal(reference, original.Reference);
                Assert.Equal(TimeSpan.FromHours(24), DateTimeOffset.Parse(original.ExpiresAtUtc) - DateTimeOffset.Parse(original.CreatedAtUtc));
                var planRequest = new PlanRequest { PlanId = original.PlanId };
                Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(planRequest)).Status);
                Assert.Equal(ProviderAliasChangeOutcome.Changed,
                    (await aliasStore.ChangeAsync(environment, alias, firstBinding, nextBinding, "fixture", default)).Outcome);
                Assert.Equal(PlanningStatus.PlanStale, (await client.CheckPlanAsync(planRequest)).Status);
                Assert.Equal(original, (await client.GetPlanAsync(planRequest)).Plan);
                replacement = (await client.CreatePlanAsync(changeRequest)).Plan;
                Assert.Equal(2, replacement.ConfigurationRevision);
                Assert.NotEqual(original.PlanId, replacement.PlanId);
                Assert.Equal(PlanningStatus.Closed, (await client.ClosePlanAsync(planRequest)).Status);
                Assert.Equal(PlanningStatus.AlreadyClosed, (await client.ClosePlanAsync(planRequest)).Status);
                Assert.Equal(PlanningStatus.Closed, (await client.CheckPlanAsync(planRequest)).Status);
                foreach (var malformed in new[] { "", "bad", Guid.NewGuid().ToString(), Guid.CreateVersion7().ToString().ToUpperInvariant() })
                {
                    Assert.Equal(PlanningStatus.InvalidRequest, (await client.GetChangeSetAsync(new ChangeSetRequest { ChangeSetId = malformed })).Status);
                    Assert.Equal(PlanningStatus.InvalidRequest, (await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = malformed })).Status);
                    Assert.Equal(PlanningStatus.InvalidRequest, (await client.GetPlanAsync(new PlanRequest { PlanId = malformed })).Status);
                    Assert.Equal(PlanningStatus.InvalidRequest, (await client.CheckPlanAsync(new PlanRequest { PlanId = malformed })).Status);
                    Assert.Equal(PlanningStatus.InvalidRequest, (await client.ClosePlanAsync(new PlanRequest { PlanId = malformed })).Status);
                }
                var unknownChange = new ChangeSetRequest { ChangeSetId = Guid.CreateVersion7().ToString() };
                Assert.Equal(PlanningStatus.ChangeSetNotFound, (await client.GetChangeSetAsync(unknownChange)).Status);
                Assert.Equal(PlanningStatus.ChangeSetNotFound, (await client.CreatePlanAsync(unknownChange)).Status);
                var unknownPlan = new PlanRequest { PlanId = Guid.CreateVersion7().ToString() };
                Assert.Equal(PlanningStatus.PlanNotFound, (await client.GetPlanAsync(unknownPlan)).Status);
                Assert.Equal(PlanningStatus.PlanNotFound, (await client.CheckPlanAsync(unknownPlan)).Status);
                Assert.Equal(PlanningStatus.PlanNotFound, (await client.ClosePlanAsync(unknownPlan)).Status);
                var unsupported = await client.ImportAsync(new EnvelopeRequest
                {
                    OriginalEnvelope = ByteString.CopyFromUtf8(
                    """{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"unsupported"},"spec":{}}}""")
                });
                Assert.NotNull(unsupported.Created);
                var unsupportedChange = await client.CreateChangeSetAsync(new CreateChangeSetRequest
                {
                    EnvironmentId = "pilot",
                    Reference = unsupported.Created.Revision.Reference,
                    Precondition = PlanningPrecondition.ExpectedAbsence
                });
                Assert.Equal(PlanningStatus.UnsupportedShape, (await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = unsupportedChange.ChangeSet.ChangeSetId })).Status);
            }
            // A separate provider with an old clock creates an already expired immutable Plan.
            await using var past = new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
                .AddSingleton<TimeProvider, PastClock>().AddPostgreSqlProviderConfigurationCodec()
                .AddSingleton<IProviderAliasResolver, PostgreSqlProviderAliasResolver>()
                .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
                .AddPostgreSqlDurableDeploymentPlanner().BuildServiceProvider();
            var expired = (await past.GetRequiredService<IDurableDeploymentPlanner>().CreatePlanAsync(
                DeploymentChangeSetId.Parse(change.ChangeSetId), "fixture", default)).Plan!;
            await using (var restarted = await StartAsync(connection, certificatePath))
            {
                using var channel = Channel(restarted, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                Assert.Equal(change, (await client.GetChangeSetAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSetId })).ChangeSet);
                var closed = await client.GetPlanAsync(new PlanRequest { PlanId = original.PlanId });
                Assert.Equal(PlanningStatus.Closed, closed.Status);
                Assert.True(closed.Plan.HasClosedAtUtc);
                Assert.Equal(original, WithoutClosure(closed.Plan));
                Assert.Equal(OperatorHostApplication.TechnicalActorId, closed.Plan.ClosedByTechnicalActorId);
                Assert.Equal(replacement, (await client.GetPlanAsync(new PlanRequest { PlanId = replacement.PlanId })).Plan);
                var expiredRequest = new PlanRequest { PlanId = expired.Id.ToString() };
                Assert.Equal(PlanningStatus.Expired, (await client.GetPlanAsync(expiredRequest)).Status);
                Assert.Equal(PlanningStatus.Expired, (await client.CheckPlanAsync(expiredRequest)).Status);
                await NativePlanningSmoke(restarted.Urls.Single(), change, material);
            }
            Assert.False(providerContact.IsCompleted, "Planning must not contact the Provider endpoint.");
            await using var check = new NpgsqlConnection(connection);
            await check.OpenAsync();
            foreach (var table in new[] { "application_deployments", "application_deployment_generations", "change_set_commits",
                "durable_create_operations", "durable_create_operation_attempts", "postgresql_database_recovery_decisions" })
                Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM " + table, check).ExecuteScalarAsync());
            Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = 'sample_db'", check).ExecuteScalarAsync());
        }
        finally { File.Delete(certificatePath); }
    }

    private static CreateChangeSetRequest WithPrecondition(CreateChangeSetRequest request, PlanningPrecondition precondition)
    {
        var copy = request.Clone(); copy.Precondition = precondition; return copy;
    }

    private static DurablePlanSummary WithoutClosure(DurablePlanSummary plan)
    {
        var copy = plan.Clone(); copy.ClearClosedAtUtc(); copy.ClearClosedByTechnicalActorId(); return copy;
    }

    private static async Task NativePlanningSmoke(string endpoint, DurableChangeSetSummary change, string material)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        Assert.True(File.Exists(binary));
        var config = Path.Combine(Path.GetTempPath(), "haby-native-planning-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(config, $"""
            formatVersion: 1
            currentContext: pilot
            instances:
              local:
                endpoint: {endpoint}
            contexts:
              pilot:
                instance: local
                environmentId: pilot
            """);
        try
        {
            async Task<string> Run(int expected, params string[] args)
            {
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in args.Concat(new[] { "--config", config })) start.ArgumentList.Add(argument);
                start.Environment["HABY_OPERATOR_SKIP_TLS_VALIDATION"] = "true";
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { process.Kill(entireProcessTree: true); throw; }
                var text = await stdout;
                Assert.Equal(expected, process.ExitCode);
                Assert.Equal("", await stderr);
                Assert.DoesNotContain(material, text);
                return text;
            }
            var created = await Run(0, "changeset", "create", change.Reference.ApplicationRecordId,
                change.Reference.RevisionNumber.ToString(), "--expected-absence", "-o", "json");
            using var changesetJson = JsonDocument.Parse(created);
            var id = changesetJson.RootElement.GetProperty("data"u8).GetProperty("changeSet"u8).GetProperty("changeSetId"u8).GetString()!;
            Assert.Contains(id, await Run(0, "changeset", "get", id));
            var planned = await Run(0, "plan", "create", id, "-o", "json");
            using var planJson = JsonDocument.Parse(planned);
            var plan = planJson.RootElement.GetProperty("data"u8).GetProperty("plan"u8);
            var planId = plan.GetProperty("planId"u8).GetString()!;
            Assert.Equal(2, plan.GetProperty("configurationRevision"u8).GetInt64());
            Assert.Contains(planId, await Run(0, "plan", "get", planId));
            Assert.Contains("current", await Run(0, "plan", "check", planId, "-o", "json"));
            Assert.Contains("closed", await Run(0, "plan", "close", planId, "-o", "json"));
            Assert.Contains("closed", await Run(1, "plan", "check", planId));
            Assert.Contains("closedAtUtc", await Run(0, "plan", "get", planId, "-o", "json"));
            Assert.Contains("invalidRequest", await Run(1, "plan", "get", "bad", "-o", "json"));
        }
        finally { File.Delete(config); }
    }
}
