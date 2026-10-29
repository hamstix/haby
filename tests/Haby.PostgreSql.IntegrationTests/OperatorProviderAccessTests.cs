using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

[Collection("Provider alias isolated")]
public sealed class OperatorProviderAccessTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Exact_preflight_and_aliases_cross_real_Host_declaration_reload_restart_and_native_transport()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        string sampleId;
        var fake = new FakeEvidence();
        await using (var host = OperatorHostApplication.Build(args, s => RegisterFake(s, fake)))
        {
            await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            await host.StartAsync();
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            await client.CreateEnvironmentAsync(new() { Name = "pilot" });
            await client.CreateEnvironmentAsync(new() { Name = "other" });
            var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
            await client.CreateSecretAsync(new() { Environment = "pilot", Name = "admin", RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8(admin.Password!) });
            await client.CreateSecretAsync(new() { Environment = "pilot", Name = "sample-secret", RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFrom(new byte[] { 1, 2, 3 }) });
            var pg = await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(PgDeclaration(admin)) });
            Assert.Equal(ProviderStatus.Created, pg.Status);
            foreach (var revision in new[] { 1, 2, 3 })
                Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(SampleDeclaration(revision)) })).Status);
            sampleId = (await client.GetProviderAsync(new() { Environment = "pilot", Name = "sample" })).Provider.InstanceId;
            var before = await Snapshot(connection);
            Assert.Equal(ProviderPreflightStatus.Ready, (await client.CheckProviderAsync(new() { Environment = "pilot", Name = "pg-main", Revision = 1 })).Preflight);
            Assert.Equal(ProviderPreflightStatus.Ready, (await client.CheckProviderAsync(new() { Environment = "pilot", Name = "sample", Revision = 1 })).Preflight);
            Assert.Equal(before, await Snapshot(connection));
            foreach (var request in new[] {
                new CheckProviderRequest { Environment = "other", Name = "sample", Revision = 1 },
                new CheckProviderRequest { Environment = "pilot", Name = "unknown", Revision = 1 },
                new CheckProviderRequest { Environment = "pilot", Name = "sample", Revision = 99 } })
                Assert.Equal(ProviderAccessStatus.TargetUnavailable, (await client.CheckProviderAsync(request)).Status);
            Assert.Equal(ProviderAccessStatus.EnvironmentNotFound, (await client.CheckProviderAsync(new() { Environment = "missing", Name = "sample", Revision = 1 })).Status);
            Assert.Equal(ProviderAccessStatus.InvalidRequest, (await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "sample.primary", Target = Target("sample", 1) })).Status);
            var first = Set(null, 1);
            var calls = fake.Calls;
            Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(first)).Status);
            Assert.Equal(calls + 1, fake.Calls); // standalone success cannot authorize alias set
            var afterFirst = await Snapshot(connection);
            fake.Outcome = ProviderPreflightOutcome.SecretUnavailable;
            Assert.Equal(ProviderPreflightStatus.SecretUnavailable, (await client.SetProviderAliasAsync(Set(1, 2))).Preflight);
            Assert.Equal(afterFirst, await Snapshot(connection));
            fake.Outcome = ProviderPreflightOutcome.Ready;
            Assert.Equal(ProviderAccessStatus.Unchanged, (await client.SetProviderAliasAsync(Set(1, 1))).Status);
            Assert.Equal(afterFirst, await Snapshot(connection));
            Assert.Equal(ProviderAccessStatus.StaleExpectation, (await client.SetProviderAliasAsync(first)).Status);
            Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(Set(1, 2))).Status);
            var beforeStale = await Snapshot(connection);
            Assert.Equal(ProviderAccessStatus.StaleExpectation, (await client.SetProviderAliasAsync(Set(1, 3))).Status);
            Assert.Equal(beforeStale, await Snapshot(connection));
            Assert.Equal(ProviderAccessStatus.InstanceAlreadyBound, (await client.SetProviderAliasAsync(new()
            { Environment = "pilot", Alias = "sample.other", Target = Target("sample", 1), ExpectedAbsent = true })).Status);
            var race = await Task.WhenAll(client.SetProviderAliasAsync(Set(2, 1)).ResponseAsync, client.SetProviderAliasAsync(Set(2, 3)).ResponseAsync);
            Assert.Single(race, x => x.Status == ProviderAccessStatus.Changed);
            Assert.Single(race, x => x.Status == ProviderAccessStatus.StaleExpectation);
            var current = (await client.GetProviderAliasAsync(new() { Environment = "pilot", Alias = "sample.primary" })).Binding;
            // Switch instances and preserve exact unnamed-ID expectation compatibility.
            var switchInstance = new SetProviderAliasRequest
            {
                Environment = "pilot",
                Alias = "sample.primary",
                Target = Target("pg-main", 1),
                Expected = new() { InstanceId = sampleId, Revision = current.Revision }
            };
            Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(switchInstance)).Status);
            await ExerciseNative(host.Urls.Single(), pg.Provider.InstanceId);
            Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(new()
            { Environment = "pilot", Alias = "sample.saved", Target = Target("sample", 2), ExpectedAbsent = true })).Status);
            await host.StopAsync();
        }
        await using (var restarted = OperatorHostApplication.Build(args, s => RegisterFake(s, new FakeEvidence())))
        {
            await restarted.StartAsync();
            using var channel = GrpcChannel.ForAddress(restarted.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(ProviderPreflightStatus.Ready, (await client.CheckProviderAsync(new() { Environment = "pilot", Name = "sample", Revision = 2 })).Preflight);
            Assert.Equal(1, (await client.GetProviderAliasAsync(new() { Environment = "pilot", Alias = "sample.primary" })).Binding.Revision);
            await restarted.StopAsync();
        }
        // No fake registrations in this build. Historical configuration must not block startup or PG.
        await using (var missing = OperatorHostApplication.Build(args, s => s.AddSingleton<CredentialSampleCodec>()
            .AddSingleton<IProviderConfigurationCodec>(p => p.GetRequiredService<CredentialSampleCodec>())))
        {
            await missing.StartAsync();
            using var channel = GrpcChannel.ForAddress(missing.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(sampleId, (await client.GetProviderAsync(new() { Environment = "pilot", Name = "sample", Revision = 1 })).Provider.InstanceId);
            Assert.Equal(2, (await client.ListProvidersAsync(new() { Environment = "pilot" })).Items.Count);
            Assert.Equal(sampleId, (await client.GetProviderAliasAsync(new() { Environment = "pilot", Alias = "sample.saved" })).Binding.InstanceId);
            Assert.Equal(ProviderPreflightStatus.HandlerUnavailable, (await client.CheckProviderAsync(new() { Environment = "pilot", Name = "sample", Revision = 1 })).Preflight);
            Assert.Equal(ProviderPreflightStatus.Ready, (await client.CheckProviderAsync(new() { Environment = "pilot", Name = "pg-main", Revision = 1 })).Preflight);
            await missing.StopAsync();
        }
    }

    [Fact]
    public async Task Real_Npgsql_failures_are_safe_and_checks_preserve_external_catalogs()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        await using var host = await OperatorHostApplication.BuildMigratedAsync(args);
        await host.StartAsync();
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        await client.CreateEnvironmentAsync(new() { Name = "pilot" });
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        await client.CreateSecretAsync(new() { Environment = "pilot", Name = "admin", RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8(admin.Password!) });
        await client.CreateSecretAsync(new() { Environment = "pilot", Name = "bad", RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8("intentionally-wrong") });
        Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(PgDeclaration(admin)) })).Status);
        var bad = PgDeclaration(admin).Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal).Replace("\"secretName\":\"admin\"", "\"secretName\":\"bad\"", StringComparison.Ordinal);
        Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(bad) })).Status);
        var offline = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Port = 1 };
        var offlineDeclaration = PgDeclaration(offline).Replace("\"revision\":1", "\"revision\":3", StringComparison.Ordinal);
        Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(offlineDeclaration), ConfirmLogicalIdentity = true, ExpectedPreviousRevision = 2 })).Status);
        var role = "haby_check_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        await using (var setup = new NpgsqlConnection(fixture.ConnectionString))
        {
            await setup.OpenAsync();
            // Disposable fixture setup only. Preflight never executes DDL or creates probe objects.
            await new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}'", setup).ExecuteNonQueryAsync();
        }
        await client.CreateSecretAsync(new() { Environment = "pilot", Name = "limited", RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8(password) });
        var limited = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Username = role };
        var limitedDeclaration = PgDeclaration(limited).Replace("pg-main", "pg-limited", StringComparison.Ordinal).Replace("\"secretName\":\"admin\"", "\"secretName\":\"limited\"", StringComparison.Ordinal);
        Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(limitedDeclaration) })).Status);
        var before = await Snapshot(connection);
        var externalBefore = await ExternalSnapshot();
        foreach (var test in new[] { ("pg-main", 1L, ProviderPreflightStatus.Ready), ("pg-main", 2L, ProviderPreflightStatus.AuthenticationFailed),
                     ("pg-main", 3L, ProviderPreflightStatus.ServerUnavailable), ("pg-limited", 1L, ProviderPreflightStatus.InsufficientPrivilege) })
        {
            var reply = await client.CheckProviderAsync(new() { Environment = "pilot", Name = test.Item1, Revision = test.Item2 });
            Assert.Equal(test.Item3, reply.Preflight);
            Assert.Equal(test.Item3 == ProviderPreflightStatus.Ready ? ProviderAccessStatus.Ready : ProviderAccessStatus.PreflightFailed, reply.Status);
            Assert.DoesNotContain(admin.Password!, reply.ToString());
            Assert.DoesNotContain(password, reply.ToString());
            Assert.DoesNotContain(admin.Host!, reply.ToString());
        }
        Assert.Equal(before, await Snapshot(connection));
        Assert.Equal(externalBefore, await ExternalSnapshot());
        Assert.Equal(ProviderAccessStatus.PreflightFailed, (await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "postgresql.primary", Target = Target("pg-main", 2), ExpectedAbsent = true })).Status);
        Assert.Equal(before, await Snapshot(connection));
        await host.StopAsync();

        async Task<string> ExternalSnapshot()
        {
            await using var database = new NpgsqlConnection(fixture.ConnectionString);
            await database.OpenAsync();
            return (string)(await new NpgsqlCommand("SELECT (SELECT count(*) FROM pg_database)::text || ':' || (SELECT count(*) FROM pg_roles)::text", database).ExecuteScalarAsync())!;
        }
    }

    [Fact]
    public async Task Cancellation_after_alias_update_rolls_back_audit_and_final_target_is_rechecked()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        using var cancellation = new CancellationTokenSource();
        var fault = new CancelAuditInsert(cancellation);
        await using var host = OperatorHostApplication.Build(args, services =>
        {
            RegisterFake(services, new());
            services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.UseNpgsql(connection).AddInterceptors(fault));
        });
        await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await host.Services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("pilot"), default);
        using var material = new SecretMaterial([1, 2, 3]);
        await host.Services.GetRequiredService<SecretAdministration>().CreateAsync("pilot", "sample-secret", Guid.NewGuid(), material, "actor", default);
        var administration = host.Services.GetRequiredService<ProviderAdministration>();
        var first = await administration.ApplyAsync(Encoding.UTF8.GetBytes(SampleDeclaration(1)), null, false, null, "actor", default);
        Assert.Equal(ProviderOutcome.Created, first.Outcome);
        Assert.Equal(ProviderOutcome.Created, (await administration.ApplyAsync(Encoding.UTF8.GetBytes(SampleDeclaration(2)), null, false, null, "actor", default)).Outcome);
        var access = host.Services.GetRequiredService<ProviderAccessAdministration>();
        Assert.Equal(ProviderAccessOutcome.Changed, (await access.SetAliasAsync("pilot", "sample.primary", new("sample", null, 1), true, null, "actor", default)).Outcome);
        var before = await Snapshot(connection);
        fault.Enabled = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await access.SetAliasAsync("pilot", "sample.primary", new("sample", null, 2), false, new("sample", null, 1), "actor", cancellation.Token));
        Assert.True(fault.Observed);
        Assert.Equal(before, await Snapshot(connection));
        // A successful network check cannot replace the final exact catalog availability guard.
        // No stored data is removed: this deterministic preflight seam models disappearing input.
        var store = new PostgreSqlProviderAliasStore(host.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), new ReadyPreflight(), TimeProvider.System);
        var unavailable = new ProviderAliasBinding(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1));
        Assert.Equal(ProviderAliasChangeOutcome.TargetUnavailable, (await store.ChangeAsync(first.Provider!.Environment, ProviderAlias.Create("sample.new"), null, unavailable, "actor", default)).Outcome);
        Assert.Equal(before, await Snapshot(connection));
    }

    private sealed class ReadyPreflight : IProviderRevisionPreflight
    {
        public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, ProviderAliasBinding binding, CancellationToken token) =>
            ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
    }
    private sealed class CancelAuditInsert(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool Enabled;
        public bool Observed;
        private void Check(System.Data.Common.DbCommand command, CancellationToken token)
        {
            if (!Enabled || !command.CommandText.Contains("INSERT INTO provider_alias_audit", StringComparison.Ordinal)) return;
            Observed = true;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        }
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, CommandEventData data,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken token = default)
        { Check(command, token); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        { Check(command, token); return ValueTask.FromResult(result); }
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("private-codec")]
    [InlineData("missing-current")]
    public void Invalid_current_preflight_registration_fails_Host_construction(string scenario)
    {
        var args = new[] { "--Haby:PostgreSql:ConnectionString=Host=127.0.0.1;Database=unused;Username=unused", "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(args, s =>
        {
            RegisterFake(s, new());
            if (scenario == "overlap") s.AddSingleton<IProviderPreflightHandler, CredentialFakePreflight>();
            if (scenario == "private-codec") s.AddSingleton<IProviderPreflightHandler>(p => new CredentialFakePreflight(new(), p.GetRequiredService<ISecretStore>(), new()));
            if (scenario == "missing-current") s.Remove(s.Single(x => x.ServiceType == typeof(IProviderPreflightHandler) && x.ImplementationType == typeof(CredentialFakePreflight)));
        }));
    }

    private static void RegisterFake(IServiceCollection services, FakeEvidence evidence) => services
        .AddSingleton(evidence).AddSingleton<CredentialSampleCodec>()
        .AddSingleton<IProviderConfigurationCodec>(p => p.GetRequiredService<CredentialSampleCodec>())
        .AddSingleton<IProviderDeclarationAdapter, CredentialSampleAdapter>()
        .AddSingleton<IProviderPreflightHandler, CredentialFakePreflight>();
    private static Haby.Operator.Contracts.ProviderSelector Target(string name, long revision) => new() { Name = name, Revision = revision };
    private static SetProviderAliasRequest Set(long? previous, long revision)
    {
        var result = new SetProviderAliasRequest { Environment = "pilot", Alias = "sample.primary", Target = Target("sample", revision) };
        if (previous is { } value) result.Expected = Target("sample", value); else result.ExpectedAbsent = true;
        return result;
    }
    private async Task<string> NewDatabaseAsync()
    {
        var name = "haby_access_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = true, MaxPoolSize = 16 }.ConnectionString;
    }
    private static async Task<string> Snapshot(string connection)
    {
        await using var context = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
        return string.Join("|", await context.ProviderInstances.AsNoTracking().CountAsync(),
            await context.ProviderConfigurationRevisions.AsNoTracking().CountAsync(), await context.ProviderAliasAudits.AsNoTracking().CountAsync(),
            string.Join(";", await context.ProviderAliases.AsNoTracking().OrderBy(x => x.Alias).Select(x => x.ProviderInstanceId + ":" + x.RevisionNumber).ToArrayAsync()));
    }
    private static string SampleDeclaration(int revision) => $$$$$"""
        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"sample","environment":"pilot"},
        "spec":{"revision":{{{{{revision}}}}},"providerKind":"credential-sample/v1","configurationContract":"credential-sample.configuration/v1",
        "implementationId":"credential-sample.impl/v1","properties":{"Target":"queue-cluster","ImplementationId":"unused"},
        "secretRefs":{"credential":{"secretName":"sample-secret","version":1}}}}
        """;
    private static string PgDeclaration(NpgsqlConnectionStringBuilder admin) => $$$$$"""
        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"pg-main","environment":"pilot"},
        "spec":{"revision":1,"providerKind":"postgresql/v1","configurationContract":"haby.postgresql.connection/v1",
        "implementationId":"{{{{{PostgreSqlDatabaseProvisioner.ImplementationId}}}}}","properties":{"host":"{{{{{admin.Host}}}}}","port":{{{{{admin.Port}}}}},
        "maintenanceDatabase":"postgres","administratorUserName":"{{{{{admin.Username}}}}}"},"secretRefs":{"administrator-password":{"secretName":"admin","version":1}}}}
        """;
    private static async Task ExerciseNative(string endpoint, string instance)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (string.IsNullOrEmpty(binary)) { Assert.NotEqual("1", Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED")); return; }
        var config = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(config, $"formatVersion: 1\ninstances: {{}}\ncontexts: {{}}\n");
            foreach (var format in new[] { "text", "json" })
                foreach (var command in new[] {
                    new[] { "provider", "check", "pg-main", "--revision", "1", "--environment", "pilot" },
                    new[] { "provider", "alias", "get", "sample.primary", "--environment", "pilot" },
                    new[] { "provider", "alias", "set", "sample.primary", "--provider", "pg-main", "--revision", "1", "--expected-provider", "pg-main", "--expected-revision", "1", "--environment", "pilot" } })
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.Environment["HABY_OPERATOR_URL"] = endpoint;
                    foreach (var arg in command.Concat(["--config", config, "-o", format])) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                    Assert.Equal(0, process.ExitCode);
                    Assert.Empty(await stderr);
                    var text = await stdout;
                    if (command[1] == "alias") Assert.Contains(instance, text);
                    Assert.DoesNotContain("Password", text);
                    if (format == "json") { using var json = JsonDocument.Parse(text); Assert.Equal(1, json.RootElement.GetProperty("formatVersion"u8).GetInt32()); }
                }
        }
        finally { File.Delete(config); }
    }
}

internal sealed class FakeEvidence
{
    public int Calls;
    public ProviderPreflightOutcome Outcome = ProviderPreflightOutcome.Ready;
}
internal sealed class CredentialFakePreflight(CredentialSampleCodec codec, ISecretStore secrets, FakeEvidence evidence)
    : ProviderPreflightHandler<CredentialSampleConfiguration>(codec)
{
    public override IReadOnlyList<ProviderAdapterKey> Keys { get; } =
        [new("credential-sample/v1", "credential-sample.configuration/v1", "credential-sample.impl/v1")];
    public override async ValueTask<ProviderPreflightResult> CheckTypedAsync(EnvironmentRecordId environment, CredentialSampleConfiguration configuration, CancellationToken token)
    {
        Interlocked.Increment(ref evidence.Calls);
        Assert.Equal("queue-cluster", configuration.Target);
        using var material = await secrets.ResolveAsync(environment, new(SecretId.Parse(configuration.SecretId), SecretVersionId.Parse(configuration.SecretVersionId)), token);
        Assert.NotNull(material);
        Assert.Equal(new byte[] { 1, 2, 3 }, material.CopyBytes());
        return new(evidence.Outcome);
    }
}
