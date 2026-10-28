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
using DescriptionStatus = Haby.Operator.Contracts.PlanDescriptionStatus;
using ActionKind = Haby.Operator.Contracts.IntendedActionKind;

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
        using var logs = new ExecutionLogs();
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
        var nextConfiguration = new PostgreSqlProviderConfiguration(1, "localhost", providerPort,
            "postgres", "never-connect", secret, PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        var nextInstance = await providers.CreateInstanceAsync(environment, nextConfiguration, default);
        await providers.AddRevisionAsync(environment, nextInstance, ProviderConfigurationRevisionNumber.Create(1), nextConfiguration, default);
        await providers.AddRevisionAsync(environment, nextInstance, ProviderConfigurationRevisionNumber.Create(2), nextConfiguration, default);
        var nextBinding = new ProviderAliasBinding(nextInstance, ProviderConfigurationRevisionNumber.Create(2));
        var role = "planning_host_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(connection))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE ROLE {role}; GRANT ALL ON SCHEMA public TO {role}; GRANT ALL ON ALL TABLES IN SCHEMA public TO {role}; REVOKE SELECT ON secret_payloads FROM {role}", admin).ExecuteNonQueryAsync();
        }
        var restricted = new NpgsqlConnectionStringBuilder(connection) { Options = "-c role=" + role }.ConnectionString;
        await using (var denied = new NpgsqlConnection(restricted))
        {
            await denied.OpenAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
                await new NpgsqlCommand("SELECT * FROM secret_payloads", denied).ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        using var certificate = Certificate();
        var certificatePath = Path.Combine(Path.GetTempPath(), "haby-planning-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-only"));
        DurableChangeSetSummary change;
        DurablePlanSummary original;
        DurablePlanSummary replacement;
        try
        {
            await using (var host = await StartExecutionLoggedHost(restricted, certificatePath, logs))
            {
                Assert.NotNull(host.Services.GetService<ISecretStore>());
                Assert.Null(host.Services.GetService<IProviderRevisionPreflight>());
                Assert.NotNull(host.Services.GetService<IDurableDesiredStateCommitter>());
                using var channel = Channel(host, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var imported = await client.ImportAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFromUtf8(Encoding.UTF8.GetString(Envelope("durable-operator")).Replace("\"id\":\"durable-operator\"", "\"id\":\"durable-operator\",\"displayName\":\"unrelated-secret-sentinel\"", StringComparison.Ordinal)) });
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
                Assert.DoesNotContain("unrelated-secret-sentinel", planned.ToString());
                original = planned.Plan;
                AssertIntendedDatabase(original);
                Assert.Equal("pilot", original.EnvironmentId);
                Assert.Equal(environment.ToString(), original.EnvironmentRecordId);
                Assert.Equal("postgresql.primary", original.ProviderAlias);
                Assert.Equal(PostgreSqlDatabaseCheckpointCapability.ImplementationId, original.ImplementationId);
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
                AssertIntendedDatabase(replacement);
                Assert.Equal(nextInstance.ToString(), replacement.ProviderInstanceId);
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
            await using (var restarted = await StartExecutionLoggedHost(restricted, certificatePath, logs))
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
                var expiredRead = await client.GetPlanAsync(expiredRequest);
                Assert.Equal(PlanningStatus.Expired, expiredRead.Status);
                AssertIntendedDatabase(expiredRead.Plan);
                Assert.Equal(PlanningStatus.Expired, (await client.CheckPlanAsync(expiredRequest)).Status);
                await NativePlanningSmoke(restarted.Urls.Single(), change, material, nextInstance.ToString(), original, expired.Id.ToString());
                // Reads run with catalog writes denied, in addition to secret-material reads denied.
                await using var admin = new NpgsqlConnection(connection);
                await admin.OpenAsync();
                await new NpgsqlCommand($"REVOKE INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public FROM {role}", admin).ExecuteNonQueryAsync();
                AssertIntendedDatabase((await client.GetPlanAsync(new PlanRequest { PlanId = replacement.PlanId })).Plan);
                // Simulate unavailable historical inputs only in this disposable catalog. Keep Plan rows untouched.
                var snapshot = (string)(await new NpgsqlCommand("SELECT snapshot FROM manifest_revisions WHERE application_record_id = '" + change.Reference.ApplicationRecordId + "'", admin).ExecuteScalarAsync())!;
                async Task SetSnapshot(string value)
                {
                    await using var command = new NpgsqlCommand("UPDATE manifest_revisions SET snapshot = @snapshot WHERE application_record_id = @application", admin);
                    command.Parameters.AddWithValue("snapshot", value);
                    command.Parameters.AddWithValue("application", Guid.Parse(change.Reference.ApplicationRecordId));
                    await command.ExecuteNonQueryAsync();
                }
                foreach (var (value, outcome) in new[]
                {
                    ("{corrupt-secret-sentinel", DescriptionStatus.InputCorrupt),
                    (snapshot.Replace("sample_db", "invalid-secret-sentinel", StringComparison.Ordinal), DescriptionStatus.UnsupportedShape)
                })
                {
                    await SetSnapshot(value);
                    var unavailable = await client.GetPlanAsync(new PlanRequest { PlanId = original.PlanId });
                    Assert.Equal(PlanningStatus.Closed, unavailable.Status);
                    Assert.Equal(original.PlanId, unavailable.Plan.PlanId);
                    Assert.Equal(original.Fingerprint, unavailable.Plan.Fingerprint);
                    Assert.Equal(outcome, unavailable.Plan.IntendedWork.Status);
                    Assert.Null(unavailable.Plan.IntendedWork.Action);
                    Assert.DoesNotContain("secret-sentinel", unavailable.ToString());
                    var openRead = await client.GetPlanAsync(new PlanRequest { PlanId = replacement.PlanId });
                    Assert.Equal(outcome == DescriptionStatus.InputCorrupt ? PlanningStatus.PlanInputUnavailable : PlanningStatus.Current, openRead.Status);
                    Assert.Equal(outcome, openRead.Plan.IntendedWork.Status);
                    Assert.Equal(PlanningStatus.Expired, (await client.GetPlanAsync(expiredRequest)).Status);
                }
                await SetSnapshot(snapshot);
                Assert.Equal(original, WithoutClosure((await client.GetPlanAsync(new PlanRequest { PlanId = original.PlanId })).Plan));
            }
            Assert.DoesNotContain("secret-sentinel", logs.Text);
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

    private static void AssertIntendedDatabase(DurablePlanSummary plan)
    {
        Assert.Equal(DescriptionStatus.Available, plan.IntendedWork.Status);
        Assert.Equal(ActionKind.Create, plan.IntendedWork.Action.Kind);
        Assert.Equal("db", plan.IntendedWork.Action.ResourceId);
        Assert.Equal("postgresql.database/v1alpha1", plan.IntendedWork.Action.ResourceType);
        Assert.Equal("sample_db", plan.IntendedWork.Action.PostgresqlDatabase.DatabaseName);
    }

    private static async Task NativePlanningSmoke(string endpoint, DurableChangeSetSummary change, string material, string providerInstance,
        DurablePlanSummary original, string expiredId)
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
                Assert.DoesNotContain("unrelated-secret-sentinel", text);
                return text;
            }
            using var historical = JsonDocument.Parse(await Run(0, "plan", "get", original.PlanId, "-o", "json"));
            Assert.Equal("closed", historical.RootElement.GetProperty("outcome"u8).GetString());
            var historicalPlan = historical.RootElement.GetProperty("data"u8).GetProperty("plan"u8);
            Assert.Equal(original.ProviderInstanceId, historicalPlan.GetProperty("providerInstanceId"u8).GetString());
            Assert.Equal(1, historicalPlan.GetProperty("configurationRevision"u8).GetInt64());
            Assert.Equal("sample_db", historicalPlan.GetProperty("intendedWork"u8).GetProperty("action"u8)
                .GetProperty("postgreSqlDatabase"u8).GetProperty("databaseName"u8).GetString());
            var expiredText = await Run(0, "plan", "get", expiredId);
            Assert.Contains("expired", expiredText);
            Assert.Contains("Intended action: Create; Resource: db", expiredText);
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
            Assert.Equal("pilot", plan.GetProperty("environmentId"u8).GetString());
            Assert.Equal(providerInstance, plan.GetProperty("providerInstanceId"u8).GetString());
            Assert.Equal("haby.postgresql.database/v1alpha1", plan.GetProperty("implementationId"u8).GetString());
            var intended = plan.GetProperty("intendedWork"u8);
            Assert.Equal("available", intended.GetProperty("status"u8).GetString());
            var action = intended.GetProperty("action"u8);
            Assert.Equal("create", action.GetProperty("kind"u8).GetString());
            Assert.Equal("db", action.GetProperty("resourceId"u8).GetString());
            Assert.Equal("postgresql.database/v1alpha1", action.GetProperty("resourceType"u8).GetString());
            Assert.Equal("sample_db", action.GetProperty("postgreSqlDatabase"u8).GetProperty("databaseName"u8).GetString());
            var readText = await Run(0, "plan", "get", planId);
            Assert.Contains(planId, readText);
            Assert.Contains("Intended action: Create; Resource: db; type: postgresql.database/v1alpha1; database: sample_db", readText);
            Assert.Contains("no Provider preflight", readText);
            using var readJson = JsonDocument.Parse(await Run(0, "plan", "get", planId, "-o", "json"));
            Assert.Equal(intended.GetRawText(), readJson.RootElement.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("intendedWork"u8).GetRawText());
            Assert.Contains("current", await Run(0, "plan", "check", planId, "-o", "json"));
            Assert.Contains("closed", await Run(0, "plan", "close", planId, "-o", "json"));
            Assert.Contains("closed", await Run(1, "plan", "check", planId));
            Assert.Contains("closedAtUtc", await Run(0, "plan", "get", planId, "-o", "json"));
            Assert.Contains("invalidRequest", await Run(1, "plan", "get", "bad", "-o", "json"));
        }
        finally { File.Delete(config); }
    }
}
