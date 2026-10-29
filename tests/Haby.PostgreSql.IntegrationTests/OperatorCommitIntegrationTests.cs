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
    [Fact]
    public async Task Desired_commit_retries_and_competition_survive_Host_restart_without_execution_or_secret_reads()
    {
        var connection = await NewDatabaseAsync();
        using var providerTrap = new TcpListener(IPAddress.Loopback, 0);
        providerTrap.Start();
        var providerPort = ((IPEndPoint)providerTrap.LocalEndpoint).Port;
        var providerContact = providerTrap.AcceptTcpClientAsync();
        const string material = "commit-secret-sentinel";
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
        var certificatePath = Path.Combine(Path.GetTempPath(), "haby-commit-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-only"));

        Assert.Equal(ProviderAliasChangeOutcome.Changed,
            (await aliasStore.ChangeAsync(environment, alias, null, firstBinding, "fixture", default)).Outcome);
        // A catalog role can read metadata and write desired state, but cannot read secret material.
        // Startup migrations are already applied by fixture setup; the production Host still checks them.
        var role = "commit_host_" + Guid.NewGuid().ToString("N");
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
        DurableCommitSummary original;
        DesiredGenerationSummary generation;
        string retryPlan;
        try
        {
            await using (var host = await StartAsync(restricted, certificatePath))
            {
                Assert.NotNull(host.Services.GetService<ISecretStore>());
                Assert.IsType<ExactProviderRevisionPreflight>(host.Services.GetRequiredService<IProviderRevisionPreflight>());
                Assert.Same(host.Services.GetRequiredService<IDurableCreateOperationExecutor>(),
                    host.Services.GetRequiredService<IDurableCreateOperationExecutor>());
                using var channel = Channel(host, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var imported = await client.ImportAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(Envelope("durable-commit")) });
                var reference = imported.Created.Revision.Reference;
                async Task<string> Change() => (await client.CreateChangeSetAsync(new CreateChangeSetRequest
                {
                    EnvironmentId = "pilot",
                    Reference = reference,
                    Precondition = PlanningPrecondition.ExpectedAbsence
                })).ChangeSet.ChangeSetId;
                async Task<string> Plan(string change) => (await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change })).Plan.PlanId;
                async Task<CommitReply> Commit(string change, string plan) => await client.CommitDesiredStateAsync(
                    new CommitDesiredStateRequest { ChangeSetId = change, PlanId = plan },
                    headers: new Grpc.Core.Metadata { { "x-actor-id", "untrusted-client" } });
                var first = await Change();
                var competing = await Change();
                var firstPlan = await Plan(first);
                var competingPlan = await Plan(competing);
                Assert.Equal(CommitStatus.PlanDoesNotBelongToChangeSet, (await Commit(first, competingPlan)).Status);
                Assert.Equal(CommitStatus.PlanNotFound, (await Commit(first, Guid.CreateVersion7().ToString())).Status);
                Assert.Equal(CommitStatus.ChangeSetNotFound, (await Commit(Guid.CreateVersion7().ToString(), firstPlan)).Status);
                foreach (var malformed in new[] { "", "bad", Guid.NewGuid().ToString(), Guid.CreateVersion7().ToString().ToUpperInvariant() })
                {
                    Assert.Equal(CommitStatus.InvalidRequest, (await Commit(malformed, firstPlan)).Status);
                    Assert.Equal(CommitStatus.InvalidRequest, (await Commit(first, malformed)).Status);
                    Assert.Equal(CommitStatus.InvalidRequest, (await client.GetCommitAsync(new ChangeSetRequest { ChangeSetId = malformed })).Status);
                    Assert.Equal(CommitStatus.InvalidRequest, (await client.GetGenerationAsync(new GenerationRequest { DeploymentId = malformed, Generation = 1 })).Status);
                }
                foreach (var number in new long[] { 0, -1 })
                    Assert.Equal(CommitStatus.InvalidRequest, (await client.GetGenerationAsync(new GenerationRequest { DeploymentId = first, Generation = number })).Status);
                Assert.Equal(CommitStatus.CommitNotFound, (await client.GetCommitAsync(new ChangeSetRequest { ChangeSetId = first })).Status);
                Assert.Equal(CommitStatus.GenerationNotFound, (await client.GetGenerationAsync(new GenerationRequest { DeploymentId = first, Generation = 1 })).Status);
                await client.ClosePlanAsync(new PlanRequest { PlanId = firstPlan });
                Assert.Equal(CommitStatus.Closed, (await Commit(first, firstPlan)).Status);
                await using (var past = new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
                    .AddSingleton<TimeProvider, PastClock>().AddPostgreSqlProviderConfigurationCodec()
                    .AddSingleton<IProviderAliasResolver, PostgreSqlProviderAliasResolver>()
                    .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
                    .AddPostgreSqlDurableDeploymentPlanner().BuildServiceProvider())
                {
                    var expired = (await past.GetRequiredService<IDurableDeploymentPlanner>().CreatePlanAsync(
                        DeploymentChangeSetId.Parse(first), "fixture", default)).Plan!;
                    Assert.Equal(CommitStatus.Expired, (await Commit(first, expired.Id.ToString())).Status);
                }
                firstPlan = await Plan(first);
                // Remove only the mutable alias through a fresh setup connection to model unavailable input.
                await using (var mutableSetup = new NpgsqlConnection(connection))
                {
                    await mutableSetup.OpenAsync();
                    await new NpgsqlCommand("DELETE FROM provider_aliases", mutableSetup).ExecuteNonQueryAsync();
                }
                Assert.Equal(CommitStatus.PlanInputUnavailable, (await Commit(first, firstPlan)).Status);
                var unavailableCheck = await client.GetPlanAsync(new PlanRequest { PlanId = firstPlan });
                Assert.Equal(PlanningStatus.PlanInputUnavailable, unavailableCheck.Status);
                AssertIntendedDatabase(unavailableCheck.Plan);
                Assert.Equal(ProviderAliasChangeOutcome.Changed,
                    (await aliasStore.ChangeAsync(environment, alias, null, nextBinding, "fixture", default)).Outcome);
                Assert.Equal(CommitStatus.PlanStale, (await Commit(first, firstPlan)).Status);
                await AssertCommitCatalog(connection, 0);
                firstPlan = await Plan(first);
                competingPlan = await Plan(competing);
                var firstAlternative = await Plan(first);
                var competingAlternative = await Plan(competing);
                var results = await Task.WhenAll(Commit(first, firstPlan), Commit(competing, competingPlan));
                var accepted = Assert.Single(results, r => r.Status == CommitStatus.Committed);
                var stale = Assert.Single(results, r => r.Status == CommitStatus.ChangeSetStale);
                Assert.True(stale.HasActualGeneration);
                Assert.Equal(1, stale.ActualGeneration);
                original = accepted.Commit;
                retryPlan = original.ChangeSetId == first ? firstAlternative : competingAlternative;
                Assert.Equal(1, original.Generation);
                Assert.Equal(OperatorHostApplication.TechnicalActorId, original.TechnicalActorId);
                Assert.DoesNotContain(material, accepted.ToString());
                // Ignore the initial response as a client would after losing it; retry different Plans concurrently.
                var retries = await Task.WhenAll(Commit(original.ChangeSetId, original.PlanId), Commit(original.ChangeSetId, retryPlan));
                Assert.All(retries, r => { Assert.Equal(CommitStatus.Committed, r.Status); Assert.Equal(original, r.Commit); });
                // Even a no-longer-current alias cannot invalidate an existing commit.
                Assert.Equal(ProviderAliasChangeOutcome.Changed,
                    (await aliasStore.ChangeAsync(environment, alias, nextBinding, firstBinding, "fixture", default)).Outcome);
                Assert.Equal(original, (await Commit(original.ChangeSetId, retryPlan)).Commit);
                // ChangeSet staleness takes precedence when its Plan inputs have also changed.
                var loser = original.ChangeSetId == first ? competing : first;
                var loserPlan = original.ChangeSetId == first ? competingPlan : firstPlan;
                Assert.Equal(CommitStatus.ChangeSetStale, (await Commit(loser, loserPlan)).Status);
                var staleDescription = await client.GetPlanAsync(new PlanRequest { PlanId = loserPlan });
                Assert.Equal(PlanningStatus.ChangeSetStale, staleDescription.Status);
                AssertIntendedDatabase(staleDescription.Plan);
                generation = (await client.GetGenerationAsync(new GenerationRequest { DeploymentId = original.DeploymentId, Generation = 1 })).DesiredGeneration;
                Assert.Equal(original.PlanId, generation.PlanId);
                Assert.Equal(2, generation.ConfigurationRevision);
                Assert.Equal(reference, generation.Reference);
                Assert.Equal("Present", generation.DesiredPresence);
            }
            await using (var restarted = await StartAsync(restricted, certificatePath))
            {
                using var channel = Channel(restarted, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                Assert.Equal(original, (await client.GetCommitAsync(new ChangeSetRequest { ChangeSetId = original.ChangeSetId })).Commit);
                Assert.Equal(generation, (await client.GetGenerationAsync(new GenerationRequest { DeploymentId = original.DeploymentId, Generation = 1 })).DesiredGeneration);
                Assert.Equal(CommitStatus.GenerationNotFound, (await client.GetGenerationAsync(new GenerationRequest { DeploymentId = original.DeploymentId, Generation = 2 })).Status);
                Assert.Equal(original, (await client.CommitDesiredStateAsync(new CommitDesiredStateRequest { ChangeSetId = original.ChangeSetId, PlanId = retryPlan })).Commit);
                await NativeCommitSmoke(restarted.Urls.Single(), original, retryPlan, material);
            }
            Assert.False(providerContact.IsCompleted, "Desired-state acceptance must never contact a Provider.");
            await AssertCommitCatalog(connection, 1);
        }
        finally { File.Delete(certificatePath); }
    }

    [Fact]
    public async Task Published_native_CLI_imports_plans_commits_and_reads_after_Host_restart_when_supplied()
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        var connection = await NewDatabaseAsync();
        using var providerTrap = new TcpListener(IPAddress.Loopback, 0);
        providerTrap.Start();
        var providerPort = ((IPEndPoint)providerTrap.LocalEndpoint).Port;
        var providerContact = providerTrap.AcceptTcpClientAsync();
        const string material = "commit-secret-sentinel";
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
        var certificatePath = Path.Combine(Path.GetTempPath(), "haby-commit-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-only"));


        Assert.Equal(ProviderAliasChangeOutcome.Changed,
            (await aliasStore.ChangeAsync(environment, alias, null, firstBinding, "fixture", default)).Outcome);
        var envelope = Path.Combine(Path.GetTempPath(), "haby-native-envelope-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllBytesAsync(envelope, Envelope("native-commit"));
        DurableCommitSummary commit;
        string plan;
        try
        {
            await using (var host = await StartAsync(connection, certificatePath))
            {
                async Task<JsonDocument> Run(params string[] args) => JsonDocument.Parse(await RunNativeCommitCli(binary, host.Urls.Single(), args));
                using var imported = await Run("manifest", "import", envelope);
                var reference = imported.RootElement.GetProperty("data"u8).GetProperty("revision"u8).GetProperty("reference"u8);
                using var change = await Run("changeset", "create", reference.GetProperty("applicationRecordId"u8).GetString()!,
                    reference.GetProperty("revisionNumber"u8).GetInt64().ToString(), "--expected-absence");
                var id = change.RootElement.GetProperty("data"u8).GetProperty("changeSet"u8).GetProperty("changeSetId"u8).GetString()!;
                using var planned = await Run("plan", "create", id);
                plan = planned.RootElement.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("planId"u8).GetString()!;
                using var accepted = await Run("changeset", "commit", id, "--plan", plan);
                Assert.Equal("committed", accepted.RootElement.GetProperty("outcome"u8).GetString());
                using var channel = Channel(host, certificate);
                commit = (await new ManifestOperator.ManifestOperatorClient(channel).GetCommitAsync(new ChangeSetRequest { ChangeSetId = id })).Commit;
                Assert.Equal(plan, commit.PlanId);
                Assert.Equal(OperatorHostApplication.TechnicalActorId, commit.TechnicalActorId);
            }
            await using (var restarted = await StartAsync(connection, certificatePath))
                await NativeCommitSmoke(restarted.Urls.Single(), commit, plan, material);
            Assert.False(providerContact.IsCompleted);
            await AssertCommitCatalog(connection, 1);
        }
        finally { File.Delete(envelope); File.Delete(certificatePath); }
    }

    private static async Task AssertCommitCatalog(string connection, long count)
    {
        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        foreach (var table in new[] { "application_deployments", "application_deployment_generations", "change_set_commits", "durable_create_operations" })
            Assert.Equal(count, await new NpgsqlCommand("SELECT count(*) FROM " + table, check).ExecuteScalarAsync());
        foreach (var table in new[] { "durable_create_operation_attempts", "postgresql_database_recovery_decisions" })
            Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM " + table, check).ExecuteScalarAsync());
        Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = 'sample_db'", check).ExecuteScalarAsync());
        if (count == 0) return;
        Assert.Equal(1L, await new NpgsqlCommand("SELECT desired_generation FROM application_deployments", check).ExecuteScalarAsync());
        Assert.Equal(DBNull.Value, await new NpgsqlCommand("SELECT applied_generation FROM application_deployments", check).ExecuteScalarAsync());
        Assert.Equal(1L, await new NpgsqlCommand("SELECT generation FROM application_deployment_generations", check).ExecuteScalarAsync());
        Assert.Equal(1L, await new NpgsqlCommand("SELECT generation FROM change_set_commits", check).ExecuteScalarAsync());
        Assert.Equal("Pending", await new NpgsqlCommand("SELECT state FROM durable_create_operations", check).ExecuteScalarAsync());
        Assert.Equal(0L, await new NpgsqlCommand("SELECT attempt_number FROM durable_create_operations", check).ExecuteScalarAsync());
        Assert.Equal(DBNull.Value, await new NpgsqlCommand("SELECT dispatched_at FROM durable_create_operations", check).ExecuteScalarAsync());
    }

    private static async Task<string> RunNativeCommitCli(string binary, string endpoint, string[] args)
    {
        var config = Path.Combine(Path.GetTempPath(), "haby-native-commit-" + Guid.NewGuid().ToString("N") + ".yaml");
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
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in args.Concat(new[] { "--config", config, "-o", "json" })) start.ArgumentList.Add(argument);
            start.Environment["HABY_OPERATOR_SKIP_TLS_VALIDATION"] = "true";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr);
            var result = await stdout;
            Assert.DoesNotContain("commit-secret-sentinel", result);
            Assert.DoesNotContain("never-connect", result);
            return result;
        }
        finally { File.Delete(config); }
    }

    private static async Task NativeCommitSmoke(string endpoint, DurableCommitSummary commit, string alternative, string material)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        Assert.True(File.Exists(binary));
        var config = Path.Combine(Path.GetTempPath(), "haby-native-commit-" + Guid.NewGuid().ToString("N") + ".yaml");
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
            foreach (var format in new[] { "text", "json" })
                foreach (var args in new string[][]
                {
                    ["changeset", "commit", commit.ChangeSetId, "--plan", alternative],
                    ["changeset", "commit", "get", commit.ChangeSetId],
                    ["deployment", "generation", "get", commit.DeploymentId, "1"]
                })
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in args.Concat(new[] { "--config", config, "-o", format })) start.ArgumentList.Add(argument);
                    start.Environment["HABY_OPERATOR_SKIP_TLS_VALIDATION"] = "true";
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch { process.Kill(entireProcessTree: true); throw; }
                    Assert.Equal(0, process.ExitCode);
                    Assert.Equal("", await stderr);
                    var text = await stdout;
                    Assert.DoesNotContain(material, text);
                    Assert.DoesNotContain("never-connect", text);
                    Assert.Contains(commit.PlanId, text);
                    Assert.Contains(commit.DeploymentId, text);
                    if (format == "json")
                    {
                        using var json = JsonDocument.Parse(text);
                        var data = json.RootElement.GetProperty("data"u8);
                        Assert.Equal("desiredStateOnly", data.GetProperty("stateMeaning"u8).GetString());
                        var snapshot = data.GetProperty(args[0] == "deployment" ? "desiredGeneration" : "commit");
                        Assert.Equal(commit.PlanId, snapshot.GetProperty("planId"u8).GetString());
                        Assert.Equal(1, snapshot.GetProperty("generation"u8).GetInt64());
                    }
                }
        }
        finally { File.Delete(config); }
    }
}
