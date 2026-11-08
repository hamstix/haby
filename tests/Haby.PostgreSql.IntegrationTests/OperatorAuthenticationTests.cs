using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    private static string[] AuthArguments(string connection, bool reflection = true) =>
        ["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0", "--Haby:Reflection:Enabled=" + reflection];
    private static Metadata OperatorHeaders(string credential) => new() { { "authorization", "Bearer " + credential } };
    private static async Task<Grpc.Core.RpcException> AuthDenied(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<RpcException>(action);
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.DoesNotContain("haby_operator_v1.", error.Message);
        Assert.DoesNotContain("haby_delivery_v1.", error.Message);
        return error;
    }

    [Fact]
    public async Task Operator_auth_covers_every_registered_administrative_method_before_work_and_fails_closed_on_storage()
    {
        var connection = await NewDatabaseAsync();
        await using var host = await OperatorHostApplication.BuildMigratedAsync(AuthArguments(connection));
        await host.StartAsync(); // Deliberately no fixture issuance: startup must not create authority.
        var factory = host.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        await using (var verify = await factory.CreateDbContextAsync()) Assert.Empty(await verify.OperatorAccess.AsNoTracking().ToArrayAsync());
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        await AuthDenied(async () => await client.ListEnvironmentsAsync(new()));
        var descriptors = ManifestOperator.Descriptor.Methods.Select(m => "/" + m.Service.FullName + "/" + m.Name).ToArray();
        var mapped = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText).Where(x => x?.Contains("/haby.operator.", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(descriptors.Order(), mapped.Order());
        using var http = new HttpClient();
        var operatorShape = OperatorCredential.Create(Guid.CreateVersion7());
        var delivery = ConfigurationDeliveryCredential.Create(Guid.CreateVersion7());
        foreach (var path in descriptors)
        {
            foreach (var supplied in new string?[] { null, "", "Bearer malformed", "Bearer " + delivery, "Bearer " + operatorShape, "Basic " + operatorShape })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, host.Urls.Single() + path)
                { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(new byte[5]) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
                if (supplied is not null) request.Headers.TryAddWithoutValidation("Authorization", supplied);
                using var response = await http.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }
        }
        var store = host.Services.GetRequiredService<IOperatorAccessStore>();
        var issued = await store.CreateAsync("coverage", LocalOperatorIssuance.LocalActor, null, default);
        await AuthDenied(async () => await client.ListEnvironmentsAsync(new(), new Metadata { { "authorization", "Bearer " + issued.Credential }, { "authorization", "Bearer " + issued.Credential } }));
        Assert.Empty((await client.ListEnvironmentsAsync(new(), OperatorHeaders(issued.Credential))).Items);
        var wrongSecret = issued.Credential[..50] + new string('0', 64);
        await AuthDenied(async () => await client.ListEnvironmentsAsync(new(), OperatorHeaders(wrongSecret)));
        // Every representative denial above must leave the complete business state empty.
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await verify.Applications.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.Environments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DurablePlans.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.Secrets.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.PostgreSqlDatabaseRecoveryDecisions.AsNoTracking().ToArrayAsync());
            // Authentication storage failure, without changing any business table.
            await verify.Database.ExecuteSqlRawAsync("ALTER TABLE operator_access_grants RENAME TO unavailable_operator_access_grants");
        }
        await AuthDenied(async () => await client.ListEnvironmentsAsync(new(), OperatorHeaders(issued.Credential)));
        await using (var repair = await factory.CreateDbContextAsync()) await repair.Database.ExecuteSqlRawAsync("ALTER TABLE unavailable_operator_access_grants RENAME TO operator_access_grants");
    }

    [Fact]
    public async Task Operator_local_initial_concurrent_recovery_temporary_issuance_audit_rotation_and_restore()
    {
        var connection = await NewDatabaseAsync();
        var restored = await NewDatabaseAsync();
        var folder = Path.Combine(Path.GetTempPath(), "operator-auth-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var dump = "/tmp/operator-auth-" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            // Explicit initial issuance migrates with no web Host, listener or TLS configuration.
            var initialFile = Path.Combine(folder, "first.token");
            using var ordinary = new StringWriter(); using var errors = new StringWriter();
            var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--token-output-file", initialFile };
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { typeof(OperatorHostApplication).Assembly.Location, "operator-token-issue", "--token-output-file", initialFile }) start.ArgumentList.Add(arg);
            start.Environment["Haby__PostgreSql__ConnectionString"] = connection;
            using (var process = Process.Start(start)!)
            {
                var o = process.StandardOutput.ReadToEndAsync(); var e = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                Assert.Equal(0, process.ExitCode); ordinary.Write(await o); Assert.Empty(await e);
            }
            var initial = (await File.ReadAllTextAsync(initialFile)).TrimEnd(); Assert.True(OperatorCredential.TryIdentify(initial, out var initialId));
            Assert.DoesNotContain(initial, ordinary.ToString());
            Assert.Equal(2, await LocalOperatorIssuance.RunAsync(args, ordinary, errors)); // No overwrite and no issuance.
            var clock = new OperatorClock();
            OperatorAccessGrant original;
            string replacement;
            Guid replacementId;
            int savedCount;
            await using (var host = OperatorHostApplication.Build(AuthArguments(connection), services => services.AddSingleton<TimeProvider>(clock)))
            {
                await host.StartAsync(); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = new ManifestOperator.ManifestOperatorClient(channel);
                var headers = OperatorHeaders(initial);
                var store = host.Services.GetRequiredService<IOperatorAccessStore>(); original = (await store.GetAsync(initialId, default))!;
                Assert.Equal(LocalOperatorIssuance.LocalActor, original.Actor);
                var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(Envelope("authenticated-audit")) }, headers);
                Assert.Equal(OperatorCredential.Actor(initialId), imported.Created.Revision.TechnicalActorId);
                // Display names are metadata, not actor selectors.
                var next = await client.CreateOperatorAccessAsync(new() { DisplayName = "impersonated-name" }, headers);
                Assert.Equal(OperatorCredential.Actor(initialId), next.Grant.Actor); replacement = next.Credential; replacementId = Guid.Parse(next.Grant.GrantId);
                Assert.DoesNotContain(replacement, next.ToString());
                var page = await client.ListOperatorAccessAsync(new() { PageSize = 1 }, headers);
                Assert.Single(page.Grants); Assert.True(page.HasNextAfterGrantId);
                Assert.Single((await client.ListOperatorAccessAsync(new() { PageSize = 1, AfterGrantId = page.NextAfterGrantId }, headers)).Grants);
                var expired = await client.CreateOperatorAccessAsync(new() { ExpiresAtUtc = clock.GetUtcNow().AddMinutes(1).ToString("O") }, headers);
                clock.Now = clock.Now.AddMinutes(2);
                await AuthDenied(async () => await client.ListEnvironmentsAsync(new(), OperatorHeaders(expired.Credential)));
                clock.Now = DateTimeOffset.UtcNow;
                var revoked = await client.RevokeOperatorAccessAsync(new() { GrantId = initialId.ToString() }, OperatorHeaders(replacement));
                Assert.Equal(OperatorCredential.Actor(replacementId), revoked.Grant.RevokedBy); Assert.False(revoked.HasCredential);
                Assert.Equal(revoked, await client.RevokeOperatorAccessAsync(new() { GrantId = initialId.ToString() }, OperatorHeaders(replacement)));
                await AuthDenied(async () => await client.ListEnvironmentsAsync(new(), headers));
                var metadata = await client.GetOperatorAccessAsync(new() { GrantId = replacementId.ToString() }, OperatorHeaders(replacement));
                Assert.False(metadata.HasCredential); Assert.DoesNotContain(replacement, metadata.ToString());
                var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
                var rejected = await Assert.ThrowsAsync<RpcException>(async () => await runtime.DeliverAsync(new() { RevisionId = Guid.CreateVersion7().ToString() }, OperatorHeaders(replacement)));
                Assert.Equal(StatusCode.PermissionDenied, rejected.StatusCode);
                await NativeOperatorAuthentication(host.Urls.Single(), initialFile, replacement, folder);
            }
            // Restart retains verifiers and original audit; no reissuance.
            await using (var host = await OperatorHostApplication.BuildMigratedAsync(AuthArguments(connection)))
            {
                await host.StartAsync(); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = new ManifestOperator.ManifestOperatorClient(channel);
                Assert.Empty((await client.ListEnvironmentsAsync(new(), OperatorHeaders(replacement))).Items);
                await AuthDenied(async () => await client.ListEnvironmentsAsync(new(), OperatorHeaders(initial)));
                await using var db = await host.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
                Assert.False(db.Database.HasPendingModelChanges());
                var rows = await db.OperatorAccess.AsNoTracking().ToArrayAsync(); savedCount = rows.Length;
                Assert.All(rows, r => { Assert.Equal(32, r.CredentialHash.Length); Assert.Equal("operator-admin/v1", r.Purpose); Assert.DoesNotContain(initial, r.ToString()); });
                Assert.Equal(original.Actor, rows.Single(x => x.Id == initialId).Actor);
                Assert.Equal(original.CreatedAt, rows.Single(x => x.Id == initialId).CreatedAt);
            }
            var sourceName = new NpgsqlConnectionStringBuilder(connection).Database!;
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + sourceName);
            // Revoke after the backup: an older snapshot legitimately restores its earlier revocation state.
            await using (var host = await OperatorHostApplication.BuildMigratedAsync(AuthArguments(connection)))
            {
                await host.StartAsync(); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = new ManifestOperator.ManifestOperatorClient(channel);
                await client.RevokeOperatorAccessAsync(new() { GrantId = replacementId.ToString() }, OperatorHeaders(replacement));
                await AuthDenied(async () => await client.ListEnvironmentsAsync(new(), OperatorHeaders(replacement)));
                // Local recovery while the Host remains running; independent contexts and EF migration locking.
                var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
                {
                    using var o = new StringWriter(); using var e = new StringWriter();
                    var path = Path.Combine(folder, "recovery-" + i + ".token");
                    var exit = await LocalOperatorIssuance.RunAsync(["--Haby:PostgreSql:ConnectionString=" + connection, "--token-output-file", path,
                        "--expires-at", DateTimeOffset.UtcNow.AddHours(1).ToString("O")], o, e);
                    Assert.Equal(0, exit); Assert.Empty(e.ToString());
                    var credential = (await File.ReadAllTextAsync(path)).TrimEnd();
                    Assert.Empty((await client.ListEnvironmentsAsync(new(), OperatorHeaders(credential))).Items);
                    return credential;
                }));
                Assert.Equal(4, results.Distinct().Count());
            }
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database!, dump);
            await using (var host = await OperatorHostApplication.BuildMigratedAsync(AuthArguments(restored)))
            {
                await host.StartAsync(); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = new ManifestOperator.ManifestOperatorClient(channel);
                Assert.Empty((await client.ListEnvironmentsAsync(new(), OperatorHeaders(replacement))).Items);
                await AuthDenied(async () => await client.ListEnvironmentsAsync(new(), OperatorHeaders(initial)));
                Assert.Equal(savedCount, (await client.ListOperatorAccessAsync(new(), OperatorHeaders(replacement))).Grants.Count);
            }
        }
        finally { Directory.Delete(folder, true); await fixture.RunClientUtilityAsync("rm", "-f", dump); }
    }
    [Fact]
    public async Task Operator_local_command_uses_the_web_Host_environment_catalog_configuration()
    {
        var connection = await NewDatabaseAsync();
        var folder = Path.Combine(Path.GetTempPath(), "operator-environment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "operator.token");
        var environment = "OperatorIssuanceTest";
        await File.WriteAllTextAsync(Path.Combine(folder, "appsettings." + environment + ".json"),
            "{\"Haby\":{\"PostgreSql\":{\"ConnectionString\":\"" + JsonEncodedText.Encode(connection) + "\"}}}");
        try
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { typeof(OperatorHostApplication).Assembly.Location, "operator-token-issue", "--token-output-file", destination }) start.ArgumentList.Add(arg);
            start.Environment.Remove("DOTNET_ENVIRONMENT"); start.Environment.Remove("Haby__PostgreSql__ConnectionString");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            Assert.Equal(0, process.ExitCode); Assert.Empty(await stderr);
            var credential = (await File.ReadAllTextAsync(destination)).TrimEnd();
            Assert.DoesNotContain(credential, await stdout);
            await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
            var grant = Assert.Single(await db.OperatorAccess.AsNoTracking().ToArrayAsync());
            Assert.True(OperatorCredential.Verify(credential, grant.CredentialHash)); Assert.Equal(LocalOperatorIssuance.LocalActor, grant.Actor);
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class OperatorClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Operator_protected_reflection_describes_both_contracts_and_can_be_disabled(bool enabled)
    {
        var connection = await NewDatabaseAsync();
        await using var host = await OperatorHostApplication.BuildMigratedAsync(AuthArguments(connection, enabled));
        var issued = await host.Services.GetRequiredService<IOperatorAccessStore>().CreateAsync(null, LocalOperatorIssuance.LocalActor, null, default);
        await host.StartAsync(); using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var admin = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Empty((await admin.ListEnvironmentsAsync(new(), OperatorHeaders(issued.Credential))).Items);
        var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await runtime.DeliverAsync(new()))).StatusCode);
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText!).Where(x => x.EndsWith("/ServerReflectionInfo", StringComparison.Ordinal)).ToArray();
        if (!enabled)
        {
            Assert.Empty(routes);
            using var call = new ServerReflection.ServerReflectionClient(channel).ServerReflectionInfo(OperatorHeaders(issued.Credential));
            await call.RequestStream.WriteAsync(new() { ListServices = "" }); await call.RequestStream.CompleteAsync();
            Assert.Equal(StatusCode.Unimplemented, (await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext())).StatusCode);
            return;
        }
        Assert.NotEmpty(routes);
        // Use the generated reflection message contract on every registered protocol route.
        foreach (var route in routes)
        {
            var parts = route.TrimStart('/').Split('/');
            var method = new Method<ServerReflectionRequest, ServerReflectionResponse>(MethodType.DuplexStreaming, parts[0], parts[1],
                Marshallers.Create(x => x.ToByteArray(), ServerReflectionRequest.Parser.ParseFrom), Marshallers.Create(x => x.ToByteArray(), ServerReflectionResponse.Parser.ParseFrom));
            foreach (var credential in new string?[] { null, ConfigurationDeliveryCredential.Create(Guid.CreateVersion7()) })
            {
                using var denied = channel.CreateCallInvoker().AsyncDuplexStreamingCall(method, null, new CallOptions(credential is null ? null : OperatorHeaders(credential)));
                await denied.RequestStream.WriteAsync(new() { ListServices = "" }); await denied.RequestStream.CompleteAsync();
                await AuthDenied(async () => await denied.ResponseStream.MoveNext());
            }
            using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(method, null, new CallOptions(OperatorHeaders(issued.Credential)));
            foreach (var service in new[] { "haby.operator.v1alpha1.ManifestOperator", "haby.configuration.v1alpha1.ConfigurationDelivery" })
            {
                await call.RequestStream.WriteAsync(new() { FileContainingSymbol = service });
                Assert.True(await call.ResponseStream.MoveNext()); Assert.NotEmpty(call.ResponseStream.Current.FileDescriptorResponse.FileDescriptorProto);
            }
            await call.RequestStream.CompleteAsync();
        }
    }

    private static async Task NativeOperatorAuthentication(string endpoint, string revokedFile, string credential, string folder)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
        if (binary is null) return;
        var retained = Path.Combine(folder, "native-admin.token"); await File.WriteAllTextAsync(retained, credential + "\n");
        var config = Path.Combine(folder, "native-admin.yaml");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: selected\ninstances:\n  local:\n    endpoint: {endpoint}\n    tokenFile: '{retained.Replace("'", "''", StringComparison.Ordinal)}'\ncontexts:\n  selected:\n    instance: local\n    environmentId: pilot\n");
        async Task<(int Exit, string Out, string Error)> Run(params string[] args)
        {
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args.Concat(["--config", config, "--output", "json"])) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            return (process.ExitCode, await stdout, await stderr);
        }
        var list = await Run("environment", "list"); Assert.Equal(0, list.Exit); Assert.Empty(list.Error);
        var nextFile = Path.Combine(folder, "native-next.token");
        var create = await Run("operator", "access", "create", "--token-output-file", nextFile); Assert.Equal(0, create.Exit); Assert.Empty(create.Error);
        var next = (await File.ReadAllTextAsync(nextFile)).TrimEnd(); Assert.True(OperatorCredential.TryIdentify(next, out var nextId));
        Assert.DoesNotContain(next, create.Out); Assert.DoesNotContain(credential, create.Out + create.Error);
        var metadata = await Run("operator", "access", "get", nextId.ToString()); Assert.Equal(0, metadata.Exit); Assert.DoesNotContain(next, metadata.Out);
        // Switch the selected file for verification, then revoke that same token.
        var configured = Path.Combine(folder, "native-configured.yaml");
        Assert.Equal(0, (await Run("config", "set-credential", "local", "--token-file", nextFile, "--normalize", "--new-config-file", configured)).Exit);
        config = configured;
        Assert.Equal(0, (await Run("environment", "list")).Exit);
        Assert.Equal(0, (await Run("operator", "access", "revoke", nextId.ToString())).Exit);
        Assert.Equal(3, (await Run("environment", "list")).Exit);
        await File.WriteAllTextAsync(nextFile, await File.ReadAllTextAsync(revokedFile));
        var denied = await Run("environment", "create", "anonymous-must-not-exist"); Assert.Equal(3, denied.Exit); Assert.Contains("Unauthenticated", denied.Error);
        var authenticatedConfig = await File.ReadAllTextAsync(config);
        await File.WriteAllTextAsync(config, authenticatedConfig.Split('\n').Where(x => !x.TrimStart().StartsWith("tokenFile:", StringComparison.Ordinal)).Aggregate("", (current, line) => current + line + "\n"));
        var anonymous = await Run("environment", "create", "anonymous-must-not-exist"); Assert.Equal(3, anonymous.Exit); Assert.Contains("Unauthenticated", anonymous.Error);
        await File.WriteAllTextAsync(config, authenticatedConfig);
        await File.WriteAllTextAsync(nextFile, credential);
    }
}
