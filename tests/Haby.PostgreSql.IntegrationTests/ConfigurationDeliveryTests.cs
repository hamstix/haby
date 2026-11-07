using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Client;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Haby.PostgreSql;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Delivery_protobuf_response_budget_exact_and_one_over_has_independent_wire_oracle(int over, bool allowed)
    {
        // One field tag plus a four-byte varint length. This independently tests the
        // transport backstop; valid 4 MiB JSON responses ordinarily stay well below it.
        var reply = new DeliverConfigurationReply { Json = ByteString.CopyFrom(new byte[8 * 1024 * 1024 - 5 + over]) };
        Assert.Equal(8 * 1024 * 1024 + over, reply.ToByteArray().Length);
        if (allowed) ConfigurationDeliveryService.ValidateResponseSize(reply);
        else Assert.Equal(StatusCode.ResourceExhausted, Assert.Throws<RpcException>(() => ConfigurationDeliveryService.ValidateResponseSize(reply)).StatusCode);
    }

    [Fact]
    public async Task Delivery_shared_TLS_endpoint_explicit_closed_address_preserves_operator_and_requires_runtime_credential()
    {
        var scenario = await Prepare(full: true, render: true);
        var path = Path.Combine(Path.GetTempPath(), "haby-delivery-" + Guid.NewGuid().ToString("N") + ".pfx");
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=haby-delivery-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, password));
        try
        {
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Port=0", "--Haby:ListenAddress=127.0.0.2",
                "--Haby:Tls:CertificatePath=" + path, "--Haby:Tls:CertificatePassword=" + password]);
            await host.StartAsync();
            Assert.Single(host.Urls); Assert.StartsWith("https://127.0.0.2:", host.Urls.Single());
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual is not null && actual.RawData.AsSpan().SequenceEqual(certificate.RawData) };
            using var channel = GrpcChannel.ForAddress(host.Urls.Single(), new GrpcChannelOptions { HttpHandler = handler });
            var admin = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(EnvironmentStatus.Found, (await admin.GetEnvironmentAsync(new() { Name = "pilot" })).Status);
            await Denied(new ConfigurationDelivery.ConfigurationDeliveryClient(channel), ConfigurationDocumentRevisionId.New().ToString(), null);
            await host.StopAsync();
        }
        finally { File.Delete(path); }
        var invalid = Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Port=0", "--Haby:Tls:Enabled=false", "--Haby:ListenAddress=invalid-address"]));
        Assert.Equal("Missing or invalid configuration keys: Haby:ListenAddress", invalid.Message);
    }

    [Fact]
    public async Task Delivery_additive_catalog_migration_retains_existing_unresolved_artifact_without_reset_or_authority_backfill()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        ConfigurationDocumentRow saved;
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]))
        {
            var target = new ConfigurationDocumentTarget(scenario.Commit.DeploymentId, scenario.Commit.Generation, ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
            Assert.Equal(Haby.Application.ConfigurationDocumentStatus.Created, (await host.Services.GetRequiredService<IConfigurationDocumentStore>().MaterializeAsync(target, "tester", default)).Status);
        }
        await using (var arrange = Context(scenario.Connection))
        {
            saved = await arrange.ConfigurationDocuments.AsNoTracking().SingleAsync();
            await arrange.Database.ExecuteSqlRawAsync("""
                DROP TABLE configuration_access_grants;
                DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%AddConfigurationAccess';
                """);
        }
        await using (var services = OwnerServices(scenario)) await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await using var verify = Context(scenario.Connection);
        Assert.False(verify.Database.HasPendingModelChanges());
        Assert.Equal(verify.Database.GetMigrations(), await verify.Database.GetAppliedMigrationsAsync());
        var after = await verify.ConfigurationDocuments.AsNoTracking().SingleAsync(); Assert.Equal(saved.Payload, after.Payload); Assert.Equal(saved.Fingerprint, after.Fingerprint);
        Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
    }
    private sealed class DeliveryClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class DeliveryReadTrap : DbCommandInterceptor
    {
        public bool Enabled;
        public bool ForbidCurrentInputs;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (ForbidCurrentInputs && new[] { "configuration_profile_revisions", "configuration_profile_assignments", "provider_aliases", "provider_configuration_revisions", "manifest_revisions" }
                .Any(table => command.CommandText.Contains(table, StringComparison.Ordinal))) throw new InvalidOperationException("Current input discovery during delivery.");
            if (Enabled && (command.CommandText.Contains("configuration_document_revisions", StringComparison.Ordinal) ||
                command.CommandText.Contains("secret_payloads", StringComparison.Ordinal))) throw new InvalidOperationException("Protected read before authorization.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class DeliveryLogs : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Lines);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
        }
    }
    private static Metadata Bearer(string credential) => new() { { "authorization", "Bearer " + credential } };
    private static async Task Denied(ConfigurationDelivery.ConfigurationDeliveryClient client, string revision, string? credential)
    {
        var failure = await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverAsync(new() { RevisionId = revision }, credential is null ? null : Bearer(credential)));
        Assert.Equal(StatusCode.PermissionDenied, failure.StatusCode);
        Assert.Equal("Configuration access denied.", failure.Status.Detail);
        Assert.All(failure.Trailers, x => Assert.Contains(x.Key, new[] { "date", "server" }));
    }

    [Fact]
    public async Task Authorized_delivery_real_host_generated_client_application_restart_rotation_native_and_restore_preserve_exact_grants()
    {
        var scenario = await Prepare(full: true, render: true);
        var clock = new DeliveryClock();
        var trap = new DeliveryReadTrap();
        var logs = new DeliveryLogs();
        string? generatedPassword = null;
        ConfigurationAccessReply active;
        ConfigurationAccessReply revoked;
        ConfigurationAccessReply expires;
        ConfigurationDocumentReply api;
        ConfigurationDocumentReply worker;
        ConfigurationDocumentRow[] saved;
        var folder = Path.Combine(Path.GetTempPath(), "haby-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var credentialFile = Path.Combine(folder, "retained.token");
        try
        {
            await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
                services => services.AddLogging(l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)).AddSingleton<TimeProvider>(clock).AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(trap))))
            {
                await host.StartAsync();
                using var channel = GrpcChannel.ForAddress(host.Urls.Single());
                var admin = new ManifestOperator.ManifestOperatorClient(channel);
                var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
                var selection = DocumentRequest(scenario);
                Assert.Equal(ConfigurationAccessStatus.NotFound, (await admin.CreateConfigurationAccessAsync(new() { Selector = selection })).Status);
                Assert.Equal(GenerationExecutionStatus.Succeeded, (await admin.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 })).Status);
                Assert.Equal(ConfigurationAccessStatus.NotFound, (await admin.CreateConfigurationAccessAsync(new() { Selector = selection })).Status);
                api = await admin.MaterializeConfigurationAsync(selection);
                worker = await admin.MaterializeConfigurationAsync(DocumentRequest(scenario, "worker"));
                Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.Created, api.Status);
                Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.Created, worker.Status);
                revoked = await admin.CreateConfigurationAccessAsync(new() { Selector = selection });
                Assert.Equal(ConfigurationAccessStatus.Created, revoked.Status); Assert.False(revoked.Grant.HasExpiresAtUtc);
                Assert.True(ConfigurationDeliveryCredential.TryIdentify(revoked.Credential, out _));
                await using (var verify = Context(scenario.Connection)) saved = await verify.ConfigurationDocuments.AsNoTracking().OrderBy(x => x.Component).ToArrayAsync();
                // Change mutable selection inputs and arrange a different current secret version.
                // The old exact version remains immutable and is the only usable database password.
                var source = ConfigurationDocumentCodec.Decode(saved[0].Payload, saved[0].Fingerprint);
                var deferred = Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(source.Tree).Members["PostgreSQL"]).Members["DefaultConnection"]).Members["ConnectionString"]);
                var pinned = Assert.IsType<ConfigurationSecretReference>(deferred.Arguments.Members["password"]).Reference;
                var alternate = SecretVersionId.New();
                var random = RandomNumberGenerator.GetBytes(32);
                var alternateMaterial = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
                try
                {
                    await using var arrange = Context(scenario.Connection);
                    arrange.SecretVersions.Add(new() { Id = Guid.Parse(alternate.ToString()), SecretId = Guid.Parse(pinned.SecretId.ToString()), ProtectionFormat = "Plaintext", FormatVersion = 1 });
                    arrange.SecretPayloads.Add(new() { SecretVersionId = Guid.Parse(alternate.ToString()), Bytes = alternateMaterial });
                    await arrange.SaveChangesAsync();
                    var protectedSlot = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await arrange.Secrets.Where(x => x.Id == Guid.Parse(pinned.SecretId.ToString()))
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentVersionId, Guid.Parse(alternate.ToString()))));
                    Assert.Equal("23503", protectedSlot.SqlState); // Generated-slot pin cannot be advanced by a current-version edit.
                    await arrange.ProviderAliases.ExecuteDeleteAsync(); await arrange.ProfileAssignments.ExecuteDeleteAsync();
                }
                finally { Array.Clear(random); Array.Clear(alternateMaterial); }
                var before = await ConfigurationState(scenario);
                trap.ForbidCurrentInputs = true;

                trap.Enabled = true;
                await Denied(runtime, api.Artifact.RevisionId, null);
                await Denied(runtime, api.Artifact.RevisionId, "invalid");
                await Denied(runtime, worker.Artifact.RevisionId, revoked.Credential);
                await Denied(runtime, ConfigurationDocumentRevisionId.New().ToString(), revoked.Credential);
                await Denied(runtime, api.Artifact.RevisionId, revoked.Credential[..^1] + (revoked.Credential[^1] == 'a' ? 'b' : 'a'));
                trap.Enabled = false;
                var delivered = await runtime.DeliverAsync(new() { RevisionId = api.Artifact.RevisionId }, Bearer(revoked.Credential));
                Assert.Equal(api.Artifact.Fingerprint, delivered.SourceFingerprint);
                using (var json = JsonDocument.Parse(delivered.Json.Memory))
                {
                    var connection = json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString()!;
                    var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);
                    Assert.Equal(200, builder.Timeout); Assert.Equal(scenario.Role, builder.Username); Assert.Equal(scenario.Database, builder.Database);
                    Assert.False(string.IsNullOrEmpty(builder.Password));
                    generatedPassword = builder.Password;
                }
                await File.WriteAllTextAsync(credentialFile, revoked.Credential + "\n");
                await RunRuntime(host.Urls.Single(), api.Artifact.RevisionId, credentialFile, scenario);
                await RunRuntime(host.Urls.Single(), api.Artifact.RevisionId, credentialFile, scenario);
                var workerGrant = await admin.CreateConfigurationAccessAsync(new() { Selector = DocumentRequest(scenario, "worker") });
                var workerDelivery = await runtime.DeliverAsync(new() { RevisionId = worker.Artifact.RevisionId }, Bearer(workerGrant.Credential));
                using (var json = JsonDocument.Parse(workerDelivery.Json.Memory))
                    Assert.Equal(15, new Npgsql.NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString()).Timeout);
                await Denied(runtime, api.Artifact.RevisionId, workerGrant.Credential);
                active = await admin.CreateConfigurationAccessAsync(new() { Selector = selection });
                Assert.False(active.Credential == revoked.Credential);
                await File.WriteAllTextAsync(credentialFile, active.Credential + "\n");
                await RunRuntime(host.Urls.Single(), api.Artifact.RevisionId, credentialFile, scenario);
                var firstRevoke = await admin.RevokeConfigurationAccessAsync(new() { GrantId = revoked.Grant.GrantId });
                var secondRevoke = await admin.RevokeConfigurationAccessAsync(new() { GrantId = revoked.Grant.GrantId });
                Assert.Equal(firstRevoke, secondRevoke); Assert.False(firstRevoke.HasCredential);
                trap.Enabled = true; await Denied(runtime, api.Artifact.RevisionId, revoked.Credential); trap.Enabled = false;
                expires = await admin.CreateConfigurationAccessAsync(new() { Selector = selection, ExpiresAtUtc = clock.Now.AddHours(2).ToString("O") });
                Assert.Equal(ConfigurationAccessStatus.Created, expires.Status);
                await runtime.DeliverAsync(new() { RevisionId = api.Artifact.RevisionId }, Bearer(expires.Credential));
                foreach (var bad in new[] { "malformed", clock.Now.AddMinutes(-1).ToString("O"), clock.Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(1)).ToString("O") })
                    Assert.Equal(ConfigurationAccessStatus.InvalidRequest, (await admin.CreateConfigurationAccessAsync(new() { Selector = selection, ExpiresAtUtc = bad })).Status);
                clock.Now += TimeSpan.FromDays(365);
                trap.Enabled = true; await Denied(runtime, api.Artifact.RevisionId, expires.Credential); trap.Enabled = false;
                await runtime.DeliverAsync(new() { RevisionId = api.Artifact.RevisionId }, Bearer(active.Credential));
                await NativeAccess(host.Urls.Single(), selection, folder);
                Assert.True(before == await ConfigurationState(scenario), "Delivery changed protected provisioning state.");
                await using (var verify = Context(scenario.Connection))
                {
                    var grants = await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync();
                    Assert.All(grants, g => { Assert.Equal(32, g.CredentialHash.Length); Assert.False(g.Scope.Contains(active.Credential, StringComparison.Ordinal)); });
                    var stored = grants.Single(x => x.Id.ToString() == active.Grant.GrantId);
                    Assert.True(ConfigurationDeliveryCredential.Verify(active.Credential, stored.CredentialHash));
                    Assert.False(ConfigurationDeliveryCredential.Verify(revoked.Credential, stored.CredentialHash));
                }
                await host.StopAsync();
                var captured = string.Join('\n', logs.Lines);
                foreach (var confidential in new[] { active.Credential, revoked.Credential, expires.Credential, generatedPassword!, "Authorization", "Password=", "ConnectionString" })
                    Assert.False(captured.Contains(confidential, StringComparison.OrdinalIgnoreCase), "Host logs contained protected data.");
            }
            await VerifyRestored(scenario.Connection);
            var restored = _catalogPools.Register(new Npgsql.NpgsqlConnectionStringBuilder(await Database("delivery_restore_")));
            var directory = "/tmp/haby-delivery-" + Guid.NewGuid().ToString("N");
            var dump = directory + "/catalog.dump";
            Exception? failure = null;
            try
            {
                await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
                await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new Npgsql.NpgsqlConnectionStringBuilder(scenario.Connection).Database);
                await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new Npgsql.NpgsqlConnectionStringBuilder(restored).Database, dump);
                await VerifyRestored(restored);
            }
            catch (Exception e) { failure = e; throw; }
            finally
            {
                try { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
                catch when (failure is not null) { }
            }

            async Task VerifyRestored(string connection)
            {
                await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0", "--Haby:ListenAddress=127.0.0.1"],
                    s => s.AddSingleton<TimeProvider>(clock).AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(trap)));
                await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
                await host.StartAsync();
                using var channel = GrpcChannel.ForAddress(host.Urls.Single());
                var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
                await RunRuntime(host.Urls.Single(), api.Artifact.RevisionId, credentialFile, scenario);
                await Denied(runtime, api.Artifact.RevisionId, revoked.Credential);
                await Denied(runtime, api.Artifact.RevisionId, expires.Credential);
                var admin = new ManifestOperator.ManifestOperatorClient(channel);
                var safe = await admin.GetConfigurationAccessAsync(new() { GrantId = active.Grant.GrantId });
                Assert.False(safe.HasCredential); Assert.Equal(active.Grant, safe.Grant);
                await using var verify = Context(connection);
                var rows = await verify.ConfigurationDocuments.AsNoTracking().OrderBy(x => x.Component).ToArrayAsync();
                Assert.True(saved.Select(x => x.Payload).SequenceEqual(rows.Select(x => x.Payload)), "Restored artifacts changed.");
                Assert.Equal(saved.Select(x => x.Fingerprint), rows.Select(x => x.Fingerprint));
                await host.StopAsync();
            }
        }
        finally { foreach (var path in Directory.EnumerateFiles(folder)) File.Delete(path); Directory.Delete(folder); }
    }

    private sealed class PayloadReads : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("secret_payloads", StringComparison.Ordinal) && !command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase)) Count++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class DeliveryFormatterFault : IConfigurationFormatter
    {
        public bool Enabled;
        public int Count;
        public ConfigurationFormatterId Contract => PostgreSqlConnectionRendering.Formatter;
        public ManifestLocalId Identity => PostgreSqlConnectionRendering.Implementation;
        public ConfidentialConfigurationString? Format(ConfigurationObject arguments, ReadOnlySpan<char> material)
        {
            Count++;
            if (Enabled) throw new InvalidOperationException("private-formatter-material-sentinel");
            return new PostgreSqlConnectionFormatter().Format(arguments, material);
        }
    }

    [Fact]
    public async Task Authorized_delivery_production_owner_purpose_material_and_formatter_failures_publish_no_document_or_new_state()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var reads = new PayloadReads(); var formatter = new DeliveryFormatterFault(); var logs = new DeliveryLogs();
        var formatters = PostgreSqlConnectionRendering.FormatterCatalog();
        var catalog = new ConfigurationRenderingCatalog([new PostgreSqlConnectionRendererContract()], [new PostgreSqlConnectionRenderer()], formatters, [formatter]);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            s => s.AddSingleton(catalog).AddLogging(l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)).AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(reads)));
        await host.StartAsync(); using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var admin = new ManifestOperator.ManifestOperatorClient(channel); var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var document = await admin.MaterializeConfigurationAsync(DocumentRequest(scenario));
        var grant = await admin.CreateConfigurationAccessAsync(new() { Selector = DocumentRequest(scenario) });
        ConfigurationDocumentRow saved; SecretPayloadRow payload; SecretRef reference;
        await using (var arrange = Context(scenario.Connection))
        {
            saved = await arrange.ConfigurationDocuments.AsNoTracking().SingleAsync();
            var decoded = ConfigurationDocumentCodec.Decode(saved.Payload, saved.Fingerprint);
            var connection = Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(decoded.Tree).Members["PostgreSQL"]).Members["DefaultConnection"]).Members["ConnectionString"]);
            reference = Assert.IsType<ConfigurationSecretReference>(connection.Arguments.Members["password"]).Reference;
            payload = await arrange.SecretPayloads.AsNoTracking().SingleAsync(x => x.SecretVersionId == Guid.Parse(reference.SecretVersionId.ToString()));
        }
        var before = await ConfigurationState(scenario);
        var scopedSecrets = host.Services.GetRequiredService<ISecretStore>();
        foreach (var wrong in new[] { new SecretDefinition(new SecretOwner.Resource(scenario.Owner with { ResourceLocalId = ManifestLocalId.Create("foreign-resource") }), SecretPurpose.ApplicationLogin),
            new SecretDefinition(new SecretOwner.Environment(scenario.Owner.EnvironmentRecordId), SecretPurpose.ProviderAdministrator) })
        {
            var priorReads = reads.Count;
            using var refused = await scopedSecrets.ResolveBoundedAsync(wrong, reference, DeliverConfiguration.MaxSecretBytes, default);
            Assert.Null(refused); Assert.Equal(priorReads, reads.Count);
        }
        foreach (var mode in new[] { "missing", "utf8", "nul", "format", "oversize", "formatter" })
        {
            await using (var arrange = Context(scenario.Connection))
            {
                // Deliberate corruption of this disposable catalog only. LOCAL restores
                // the guards before the act/verify contexts and before pool return.
                await using var corrupt = await arrange.Database.BeginTransactionAsync();
                await arrange.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
                if (mode == "missing") await arrange.SecretPayloads.Where(x => x.SecretVersionId == payload.SecretVersionId).ExecuteDeleteAsync();
                else
                {
                    if (!await arrange.SecretPayloads.AnyAsync(x => x.SecretVersionId == payload.SecretVersionId)) arrange.SecretPayloads.Add(new() { SecretVersionId = payload.SecretVersionId, Bytes = payload.Bytes });
                    await arrange.SaveChangesAsync();
                    var bytes = mode switch { "utf8" => new byte[] { 0xff }, "nul" => new byte[] { 65, 0, 66 }, "format" => new byte[] { 65, 66 }, "oversize" => new byte[65537], _ => payload.Bytes };
                    await arrange.SecretPayloads.Where(x => x.SecretVersionId == payload.SecretVersionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Bytes, bytes));
                }
                await corrupt.CommitAsync();
            }
            formatter.Enabled = mode == "formatter";
            var priorReads = reads.Count; var priorFormatting = formatter.Count;
            var failure = await Assert.ThrowsAsync<RpcException>(async () => await runtime.DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential)));
            Assert.Equal(mode == "oversize" ? StatusCode.ResourceExhausted : StatusCode.FailedPrecondition, failure.StatusCode);
            Assert.False(failure.ToString().Contains("private-formatter-material-sentinel", StringComparison.Ordinal));
            if (mode is "oversize" or "missing") Assert.Equal(priorReads, reads.Count);
            if (mode != "formatter") Assert.Equal(priorFormatting, formatter.Count);
        }
        await using (var arrange = Context(scenario.Connection))
        {
            await using var restore = await arrange.Database.BeginTransactionAsync();
            await arrange.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
            await arrange.SecretPayloads.Where(x => x.SecretVersionId == payload.SecretVersionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Bytes, payload.Bytes));
            await restore.CommitAsync();
        }
        formatter.Enabled = false;
        await runtime.DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
        Assert.True(before == await ConfigurationState(scenario), "Delivery changed protected provisioning state.");
        await using (var verify = Context(scenario.Connection))
        {
            var after = await verify.ConfigurationDocuments.AsNoTracking().SingleAsync(); Assert.Equal(saved.Payload, after.Payload); Assert.Equal(saved.Fingerprint, after.Fingerprint);
            Assert.Single(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        }
        var captured = string.Join('\n', logs.Lines);
        Assert.False(captured.Contains(grant.Credential, StringComparison.Ordinal)); Assert.False(captured.Contains("private-formatter-material-sentinel", StringComparison.Ordinal));
        Array.Clear(payload.Bytes);
        await host.StopAsync();
    }

    private static async Task RunRuntime(string endpoint, string revision, string path, Scenario scenario)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(RuntimeConfigurationClient).Assembly.Location);
        start.Environment["HABY_RUNTIME_ENDPOINT"] = endpoint; start.Environment["HABY_RUNTIME_REVISION"] = revision; start.Environment["HABY_RUNTIME_TOKEN_FILE"] = path;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stderr);
        var text = await stdout;
        Assert.Contains("user=" + scenario.Role, text); Assert.Contains("database=" + scenario.Database, text);
        Assert.False(text.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task NativeAccess(string endpoint, ConfigurationDocumentRequest selector, string folder)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        var config = Path.Combine(folder, "native.yaml"); var credentialFile = Path.Combine(folder, "native.token");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: pilot\ninstances:\n  selected:\n    endpoint: {endpoint}\ncontexts:\n  pilot:\n    instance: selected\n    environmentId: pilot\n");
        var text = await Run(["create", selector.ApplicationId, "--component", "api", "--document", "settings", "--token-output-file", credentialFile]);
        using var parsed = JsonDocument.Parse(text); var id = parsed.RootElement.GetProperty("data"u8).GetProperty("grantId"u8).GetString()!;
        var retained = await RuntimeConfigurationClient.ReadCredentialAsync(credentialFile, default);
        using var channel = GrpcChannel.ForAddress(endpoint);
        var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var revision = parsed.RootElement.GetProperty("data"u8).GetProperty("revisionId"u8).GetString()!;
        await runtime.DeliverAsync(new() { RevisionId = revision }, Bearer(retained));
        foreach (var verb in new[] { "get", "revoke", "revoke" })
        {
            var ordinary = await Run([verb, id]); Assert.False(ordinary.Contains(retained, StringComparison.Ordinal));
        }
        await Denied(runtime, revision, retained);
        async Task<string> Run(string[] args)
        {
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "configuration", "access" }.Concat(args).Concat(["--config", config, "--output", "json"])) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            Assert.Equal(0, process.ExitCode); Assert.Equal("", await stderr); return await stdout;
        }
    }
}
