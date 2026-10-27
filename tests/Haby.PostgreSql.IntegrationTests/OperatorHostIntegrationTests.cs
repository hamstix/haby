using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class OperatorHostIntegrationTests(PostgreSqlFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Pg_dump_restore_preserves_revision_warnings_and_preview_in_fresh_host()
    {
        var source = await NewDatabaseAsync();
        var restored = await NewDatabaseAsync();
        var sourceName = new NpgsqlConnectionStringBuilder(source).Database!;
        var restoredName = new NpgsqlConnectionStringBuilder(restored).Database!;
        var dump = "/tmp/haby-catalog-" + Guid.NewGuid().ToString("N") + ".dump";
        var targetName = "restore_preview_" + Guid.NewGuid().ToString("N");
        using var certificate = Certificate();
        var path = Path.Combine(Path.GetTempPath(), "haby-operator-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "test-only"));
        Exception? testFailure = null;
        try
        {
            RevisionReference reference;
            RevisionSummary originalRevision;
            PreviewDatabaseReply originalPreview;
            await using (var host = await StartAsync(source, path))
            {
                using var channel = Channel(host, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var payload = Encoding.UTF8.GetString(Envelope("restore-example"))
                    .Replace("sample_db", targetName, StringComparison.Ordinal);
                var imported = await client.ImportAsync(new EnvelopeRequest
                    { OriginalEnvelope = ByteString.CopyFromUtf8(payload) });
                Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase);
                Assert.Equal("manifest.resource.unused", Assert.Single(imported.Created.Revision.HistoricalWarnings.Diagnostics).Code);
                reference = imported.Created.Revision.Reference;
                originalRevision = imported.Created.Revision;
                var preview = PreviewRequest(reference);
                originalPreview = await client.PreviewDatabaseAsync(preview);
                Assert.Equal(PreviewDatabaseReply.OutcomeOneofCase.WouldCreate, originalPreview.OutcomeCase);
            }

            var sourceState = await CatalogStateAsync(source);
            Assert.Equal((1L, 1L), (sourceState.Applications, sourceState.Revisions));
            Assert.NotEmpty(sourceState.Migrations);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + sourceName);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner",
                "--no-privileges", "--dbname=" + restoredName, dump);

            var restoredState = await CatalogStateAsync(restored);
            Assert.Equal(sourceState, restoredState);
            await using (var fresh = await StartAsync(restored, path))
            {
                using var channel = Channel(fresh, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var found = await client.GetRevisionAsync(new GetRevisionRequest { Reference = reference });
                Assert.Equal(GetRevisionReply.OutcomeOneofCase.Found, found.OutcomeCase);
                Assert.Equal(originalRevision, found.Found);
                var result = await client.PreviewDatabaseAsync(PreviewRequest(reference));
                Assert.Equal(PreviewDatabaseReply.OutcomeOneofCase.WouldCreate, result.OutcomeCase);
                Assert.Equal(targetName, result.WouldCreate.DatabaseName);
                Assert.Equal(originalPreview, result);
            }
            Assert.Equal(restoredState, await CatalogStateAsync(restored));
            await using var check = new NpgsqlConnection(fixture.ConnectionString);
            await check.OpenAsync();
            await using var target = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", check);
            target.Parameters.AddWithValue("name", targetName);
            Assert.Equal(0L, await target.ExecuteScalarAsync());
        }
        catch (Exception error)
        {
            testFailure = error;
            throw;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            try { await fixture.RunClientUtilityAsync("rm", "-f", dump); }
            catch (Exception error) { cleanupErrors.Add(error); }
            try { File.Delete(path); }
            catch (Exception error) { cleanupErrors.Add(error); }
            if (cleanupErrors.Count > 0)
            {
                if (testFailure is null) throw new AggregateException("Backup drill cleanup failed.", cleanupErrors);
                foreach (var error in cleanupErrors)
                    output.WriteLine("Cleanup also failed: " + error.Message);
            }
        }
    }

    private static PreviewDatabaseRequest PreviewRequest(RevisionReference reference)
    {
        var request = new PreviewDatabaseRequest { Reference = reference, EnvironmentId = "test" };
        request.Providers.Add(new PreviewProviderSelection
        {
            Alias = "postgresql.primary", InstanceId = "local-postgresql", ConfigurationRevision = 7,
            ImplementationId = "haby.postgresql.database/v1alpha1"
        });
        return request;
    }

    private static async Task<(long Applications, long Revisions, string Migrations)> CatalogStateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        async Task<long> CountAsync(string table)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM " + table, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }
        await using var migrations = new NpgsqlCommand("SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\"", connection);
        return (await CountAsync("applications"), await CountAsync("manifest_revisions"),
            (string)(await migrations.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Preview_after_restart_is_hypothetical_and_does_not_create_database_or_deployment_state()
    {
        var catalogConnection = await NewDatabaseAsync();
        var targetName = "preview_" + Guid.NewGuid().ToString("N");
        using var certificate = Certificate();
        var path = Path.Combine(Path.GetTempPath(), "haby-operator-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "test-only"));
        try
        {
            RevisionReference reference;
            await using (var host = await StartAsync(catalogConnection, path))
            {
                using var channel = Channel(host, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var payload = Encoding.UTF8.GetString(Envelope("preview-example"))
                    .Replace("sample_db", targetName, StringComparison.Ordinal);
                var imported = await client.ImportAsync(new EnvelopeRequest
                    { OriginalEnvelope = ByteString.CopyFromUtf8(payload) });
                reference = imported.Created.Revision.Reference;
            }
            await using (var restarted = await StartAsync(catalogConnection, path))
            {
                using var channel = Channel(restarted, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var request = new PreviewDatabaseRequest { Reference = reference, EnvironmentId = "test" };
                request.Providers.Add(new PreviewProviderSelection
                {
                    Alias = "postgresql.primary", InstanceId = "local-postgresql", ConfigurationRevision = 7,
                    ImplementationId = "haby.postgresql.database/v1alpha1"
                });
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var result = await client.PreviewDatabaseAsync(request);
                    Assert.Equal(PreviewDatabaseReply.OutcomeOneofCase.WouldCreate, result.OutcomeCase);
                    Assert.Equal(targetName, result.WouldCreate.DatabaseName);
                    Assert.Equal("db", result.WouldCreate.ResourceId);
                    Assert.Equal("postgresql.primary", result.WouldCreate.ProviderAlias);
                    Assert.DoesNotContain("connection", result.ToString(), StringComparison.OrdinalIgnoreCase);
                }
                request.Providers[0].Alias = "missing";
                Assert.Equal(PreviewDatabaseReply.OutcomeOneofCase.AliasNotResolved,
                    (await client.PreviewDatabaseAsync(request)).OutcomeCase);
                request.Providers[0].Alias = "postgresql.primary";
                request.Providers[0].ImplementationId = "wrong-secret-value";
                var mismatch = await client.PreviewDatabaseAsync(request);
                Assert.Equal(PreviewDatabaseReply.OutcomeOneofCase.CapabilityMismatch, mismatch.OutcomeCase);
                Assert.DoesNotContain("secret-value", mismatch.ToString());
                request.Providers[0].ImplementationId = "haby.postgresql.database/v1alpha1";
                request.EnvironmentId = "invalid_secret=value";
                var malformed = await client.PreviewDatabaseAsync(request);
                Assert.Equal(PreviewDatabaseReply.OutcomeOneofCase.InvalidContext, malformed.OutcomeCase);
                Assert.DoesNotContain("secret-value", malformed.ToString());
                request.EnvironmentId = "test";
                request.Providers.Add(request.Providers[0].Clone());
                Assert.Equal(PreviewDatabaseReply.OutcomeOneofCase.InvalidContext,
                    (await client.PreviewDatabaseAsync(request)).OutcomeCase);
                request.Providers.RemoveAt(1);
                var unsupportedDocument = JsonNode.Parse(Envelope("preview-unsupported"))!;
                unsupportedDocument["manifest"]!["spec"]!["resources"]!["other"] = JsonNode.Parse(
                    """{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"other_db"}}"""u8);
                var unsupportedPayload = unsupportedDocument.ToJsonString();
                var unsupported = await client.ImportAsync(new EnvelopeRequest
                    { OriginalEnvelope = ByteString.CopyFromUtf8(unsupportedPayload) });
                Assert.Equal(ImportReply.OutcomeOneofCase.Created, unsupported.OutcomeCase);
                request.Reference = unsupported.Created.Revision.Reference;
                Assert.Equal(PreviewDatabaseReply.OutcomeOneofCase.UnsupportedShape,
                    (await client.PreviewDatabaseAsync(request)).OutcomeCase);
                var original = await client.GetRevisionAsync(new GetRevisionRequest { Reference = reference });
                Assert.Equal(GetRevisionReply.OutcomeOneofCase.Found, original.OutcomeCase);
                Assert.Equal("manifest.resource.unused", Assert.Single(original.Found.HistoricalWarnings.Diagnostics).Code);
            }
            await using (var check = new NpgsqlConnection(fixture.ConnectionString))
            {
                await check.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", check);
                command.Parameters.AddWithValue("name", targetName);
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }
            await using (var check = new NpgsqlConnection(catalogConnection))
            {
                await check.OpenAsync();
                await using var count = new NpgsqlCommand("SELECT count(*) FROM manifest_revisions", check);
                Assert.Equal(2L, await count.ExecuteScalarAsync());
                await using var deployment = new NpgsqlCommand(
                    "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('application_deployments', 'operations')", check);
                Assert.Equal(0L, await deployment.ExecuteScalarAsync());
                await using var plans = new NpgsqlCommand("SELECT (SELECT count(*) FROM deployment_change_sets) + (SELECT count(*) FROM deployment_plans)", check);
                Assert.Equal(0L, await plans.ExecuteScalarAsync());
            }
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task Reflection_is_available_in_development_and_production()
    {
        var connection = await NewDatabaseAsync();
        using var certificate = Certificate();
        var path = Path.Combine(Path.GetTempPath(), "haby-operator-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "test-only"));
        try
        {
            await using (var development = await StartAsync(connection, path, "Development"))
            {
                using var channel = Channel(development, certificate);
                var client = new ServerReflection.ServerReflectionClient(channel);
                using var call = client.ServerReflectionInfo();
                await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" });
                Assert.True(await call.ResponseStream.MoveNext());
                Assert.Contains(call.ResponseStream.Current.ListServicesResponse.Service,
                    service => service.Name == "haby.operator.v1alpha1.ManifestOperator");
            }
            await using (var production = await StartAsync(connection, path, "Production"))
            {
                using var channel = Channel(production, certificate);
                var client = new ServerReflection.ServerReflectionClient(channel);
                using var call = client.ServerReflectionInfo();
                await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" });
                Assert.True(await call.ResponseStream.MoveNext());
                Assert.Contains(call.ResponseStream.Current.ListServicesResponse.Service,
                    service => service.Name == "haby.operator.v1alpha1.ManifestOperator");
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Generated_client_uses_tls_and_durable_catalog_across_host_restart()
    {
        var connection = await NewDatabaseAsync();
        using var certificate = Certificate();
        var path = Path.Combine(Path.GetTempPath(), "haby-operator-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "test-only"));
        try
        {
            RevisionReference reference;
            await using (var host = await StartAsync(connection, path))
            {
                using (var untrustedChannel = GrpcChannel.ForAddress(host.Urls.Single()))
                {
                    var untrusted = new ManifestOperator.ManifestOperatorClient(untrustedChannel);
                    await Assert.ThrowsAsync<RpcException>(async () => await untrusted.ValidateAsync(
                        new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(Envelope("operator-example")) }));
                }
                using var channel = Channel(host, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var payload = Envelope("operator-example");
                var valid = await client.ValidateAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(payload) });
                Assert.Equal(ValidateReply.OutcomeOneofCase.Valid, valid.OutcomeCase);
                Assert.Equal("manifest.resource.unused", Assert.Single(valid.Valid.Warnings.Diagnostics).Code);

                var invalidValidationRequest = await client.ValidateAsync(new EnvelopeRequest
                { OriginalEnvelope = ByteString.CopyFromUtf8("{bad secret-value") });
                Assert.Equal(ValidateReply.OutcomeOneofCase.InvalidRequest, invalidValidationRequest.OutcomeCase);
                Assert.DoesNotContain("secret-value", invalidValidationRequest.ToString());
                var invalidValidationManifest = await client.ValidateAsync(new EnvelopeRequest
                {
                    OriginalEnvelope = ByteString.CopyFromUtf8(
                        """{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"Bad_Id"},"spec":{}}}""")
                });
                Assert.Equal(ValidateReply.OutcomeOneofCase.InvalidManifest, invalidValidationManifest.OutcomeCase);

                var invalidRequest = await client.ImportAsync(new EnvelopeRequest
                { OriginalEnvelope = ByteString.CopyFromUtf8("{bad secret-value") });
                Assert.Equal(ImportReply.OutcomeOneofCase.InvalidRequest, invalidRequest.OutcomeCase);
                Assert.DoesNotContain("secret-value", invalidRequest.ToString());
                var invalid = await client.ImportAsync(new EnvelopeRequest
                {
                    OriginalEnvelope = ByteString.CopyFromUtf8(
                    """{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"Bad_Id"},"spec":{}}}""")
                });
                Assert.Equal(ImportReply.OutcomeOneofCase.InvalidManifest, invalid.OutcomeCase);
                await using (var verify = new NpgsqlConnection(connection))
                {
                    await verify.OpenAsync();
                    await using var count = new NpgsqlCommand("SELECT count(*) FROM manifest_revisions", verify);
                    Assert.Equal(0L, await count.ExecuteScalarAsync());
                }

                var created = await client.ImportAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(payload) });
                Assert.Equal(ImportReply.OutcomeOneofCase.Created, created.OutcomeCase);
                reference = created.Created.Revision.Reference;
                Assert.Equal(OperatorHostApplication.TechnicalActorId, created.Created.Revision.TechnicalActorId);
                Assert.Equal("manifest.resource.unused", Assert.Single(created.Created.Revision.HistoricalWarnings.Diagnostics).Code);
                var repeat = await client.ImportAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(payload) });
                Assert.Equal(ImportReply.OutcomeOneofCase.ExistingEquivalent, repeat.OutcomeCase);
                Assert.Equal(reference, repeat.ExistingEquivalent.Revision.Reference);
                var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload)
                    .Replace("\"name\":\"sample_db\"", "\"name\":\"other_db\"", StringComparison.Ordinal));
                var conflict = await client.ImportAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(changed) });
                Assert.Equal(ImportReply.OutcomeOneofCase.VersionContentConflict, conflict.OutcomeCase);
                Assert.Contains("/spec/resources/db/properties/name", conflict.VersionContentConflict.ChangedPaths);
                Assert.DoesNotContain("other_db", conflict.ToString());
                var oversized = new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(new byte[OperatorHostApplication.MaxReceiveBytes + 1]) };
                var error = await Assert.ThrowsAsync<RpcException>(async () => await client.ValidateAsync(oversized));
                Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
            }
            await using (var restarted = await StartAsync(connection, path))
            {
                using var channel = Channel(restarted, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var found = await client.GetRevisionAsync(new GetRevisionRequest { Reference = reference });
                Assert.Equal(GetRevisionReply.OutcomeOneofCase.Found, found.OutcomeCase);
                Assert.Equal("manifest.resource.unused", Assert.Single(found.Found.HistoricalWarnings.Diagnostics).Code);
                Assert.Equal(OperatorHostApplication.TechnicalActorId, found.Found.TechnicalActorId);
                var absent = await client.GetRevisionAsync(new GetRevisionRequest
                {
                    Reference = new RevisionReference
                    { ApplicationRecordId = reference.ApplicationRecordId, RevisionNumber = 100 }
                });
                Assert.Equal(GetRevisionReply.OutcomeOneofCase.NotFound, absent.OutcomeCase);
                var invalidReference = await Assert.ThrowsAsync<RpcException>(async () => await client.GetRevisionAsync(
                    new GetRevisionRequest
                    {
                        Reference = new RevisionReference
                        { ApplicationRecordId = "Bad_Id", RevisionNumber = 1 }
                    }));
                Assert.Equal(StatusCode.InvalidArgument, invalidReference.StatusCode);
                var zeroNumber = await Assert.ThrowsAsync<RpcException>(async () => await client.GetRevisionAsync(
                    new GetRevisionRequest
                    {
                        Reference = new RevisionReference
                        { ApplicationRecordId = reference.ApplicationRecordId, RevisionNumber = 0 }
                    }));
                Assert.Equal(StatusCode.InvalidArgument, zeroNumber.StatusCode);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Failed_migration_prevents_host_from_serving()
    {
        var connection = await NewDatabaseAsync();
        await using (var db = new NpgsqlConnection(connection))
        {
            await db.OpenAsync();
            await using var obstruction = new NpgsqlCommand("CREATE TABLE applications (unexpected integer)", db);
            await obstruction.ExecuteNonQueryAsync();
        }
        using var certificate = Certificate();
        var path = Path.Combine(Path.GetTempPath(), "haby-operator-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "test-only"));
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => OperatorHostApplication.BuildMigratedAsync(Arguments(connection, path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_tls_material_prevents_host_configuration()
    {
        var missing = Path.Combine(Path.GetTempPath(), "missing-haby-cert-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(Arguments("Host=localhost", missing)));
        Assert.Contains("Haby:Tls:CertificatePath", error.Message);
        Assert.DoesNotContain("Host=localhost", error.Message);
        var invalidPort = Arguments("Host=localhost", missing);
        invalidPort[^2] = "--Haby:Port=invalid";
        var portError = Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(invalidPort));
        Assert.Contains("Haby:Port", portError.Message);
    }

    private async Task<string> NewDatabaseAsync()
    {
        var name = "haby_operator_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static byte[] Envelope(string applicationId) => Encoding.UTF8.GetBytes(
        """{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"operator-example"},"spec":{"resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"sample_db"}}}}}}"""
            .Replace("operator-example", applicationId, StringComparison.Ordinal));

    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    private static string[] Arguments(string connection, string path, string environment = "Production") =>
        ["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=true", "--Haby:Tls:CertificatePath=" + path,
            "--Haby:Tls:CertificatePassword=test-only", "--Haby:Port=0", "--environment=" + environment];

    private static async Task<WebApplication> StartAsync(string connection, string path, string environment = "Production")
    {
        var app = await OperatorHostApplication.BuildMigratedAsync(Arguments(connection, path, environment));
        await app.StartAsync();
        return app;
    }

    private static GrpcChannel Channel(WebApplication app, X509Certificate2 certificate)
    {
        var address = new Uri(app.Urls.Single());
        Assert.Equal("https", address.Scheme);
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(address.Host)));
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, presented, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors &&
            presented?.Thumbprint == certificate.Thumbprint;
        return GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
    }
}
