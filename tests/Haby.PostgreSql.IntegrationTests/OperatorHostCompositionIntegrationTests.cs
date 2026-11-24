using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Operator.Composition.Sample.Wire;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Published_external_composition_uses_own_configuration_generated_RPC_auth_actor_and_catalog_restart()
    {
        var binary = Environment.GetEnvironmentVariable("HABY_COMPOSITION_SAMPLE_PATH");
        Assert.False(string.IsNullOrWhiteSpace(binary), "Publish the sample and set HABY_COMPOSITION_SAMPLE_PATH; no fallback is permitted.");
        Assert.True(Path.IsPathFullyQualified(binary) && File.Exists(binary));
        var published = Path.GetDirectoryName(binary)!;
        Assert.True(File.Exists(Path.Combine(published, "Haby.Operator.Composition.Sample.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(published, "Haby.Operator.Composition.Sample.deps.json")));
        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(published, "appsettings.json"))))
            Assert.Equal("published-downstream-configuration", settings.RootElement.GetProperty("Sample"u8).GetProperty("Value"u8).GetString());
        output.WriteLine("Published composition SHA256=" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(binary))));
        var connection = await NewDatabaseAsync();
        var root = Path.Combine(Path.GetTempPath(), "haby239-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "content");
        var working = Path.Combine(root, "working");
        Directory.CreateDirectory(content); Directory.CreateDirectory(working);
        await File.WriteAllTextAsync(Path.Combine(content, "appsettings.json"),
            """{"Logging":{"LogLevel":{"Default":"None"}},"Sample":{"Value":"isolated-content-root"}}""");
        string? credential = null;
        Guid grantId = default;
        var phase = HostFailurePhase.Build;
        await HostFailureDiagnostics.RunAsync(async defer =>
        {
            defer(() => { Directory.Delete(root, true); return ValueTask.CompletedTask; });
            for (var startup = 0; startup < 2; startup++)
            {
                var port = OperatorHostCompositionTests.AvailablePort();
                await OperatorHostCompositionTests.RequireNotListeningAsync(port);
                var child = new CompositionProcess(binary!, connection, working,
                    ["--contentRoot=" + content, "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"]);
                defer(child.DisposeAsync);
                await child.ExpectAsync("composition-built");
                await OperatorHostCompositionTests.RequireNotListeningAsync(port);
                await using (var db = new NpgsqlConnection(connection))
                {
                    await db.OpenAsync();
                    await using var query = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", db);
                    Assert.True((long)(await query.ExecuteScalarAsync())! > 0);
                }
                if (startup == 0)
                {
                    var tokenPath = Path.Combine(root, "operator.token");
                    var issuance = new CompositionProcess(binary!, connection, working,
                        ["operator-token-issue", "--token-output-file", tokenPath]);
                    defer(issuance.DisposeAsync);
                    await issuance.RequireExitAsync(0);
                    credential = (await File.ReadAllTextAsync(tokenPath)).TrimEnd();
                    Assert.True(OperatorCredential.TryIdentify(credential, out grantId));
                }
                phase = HostFailurePhase.Start;
                await child.SendAsync("start");
                await child.ExpectAsync("composition-started");
                using var channel = GrpcChannel.ForAddress("http://127.0.0.1:" + port);
                var custom = new CompositionProbe.CompositionProbeClient(channel);
                var stock = new ManifestOperator.ManifestOperatorClient(channel);
                phase = HostFailurePhase.AdministrativeRead;
                await AuthDenied(async () => await custom.ReadAsync(new()));
                await AuthDenied(async () => await custom.ReadAsync(new(), OperatorHeaders("invalid")));
                await AuthDenied(async () => await custom.ReadAsync(new(), OperatorHeaders(ConfigurationDeliveryCredential.Create(Guid.CreateVersion7()))));
                var reply = await custom.ReadAsync(new(), OperatorHeaders(credential!));
                Assert.Equal("isolated-content-root", reply.Value);
                Assert.Equal(OperatorCredential.Actor(grantId), reply.Actor);
                Assert.Equal(DateTimeOffset.UtcNow.Year, reply.UtcYear);
                Assert.Empty((await stock.ListEnvironmentsAsync(new(), OperatorHeaders(credential!))).Items);
                var revoked = await stock.CreateOperatorAccessAsync(new() { DisplayName = "composition-revoked" }, OperatorHeaders(credential!));
                Assert.Equal(OperatorCredential.Actor(Guid.Parse(revoked.Grant.GrantId)),
                    (await custom.ReadAsync(new(), OperatorHeaders(revoked.Credential))).Actor);
                await stock.RevokeOperatorAccessAsync(new() { GrantId = revoked.Grant.GrantId }, OperatorHeaders(credential!));
                await AuthDenied(async () => await custom.ReadAsync(new(), OperatorHeaders(revoked.Credential)));
                await child.SendAsync("stop");
                await child.ExpectAsync("composition-stopped");
                await child.RequireExitAsync(0);
                await OperatorHostCompositionTests.RequireNotListeningAsync(port);
            }
        }, () => phase, () => null, (failedPhase, _, _) => output.WriteLine("Composition failure phase=" + failedPhase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composed_migration_failure_or_cancellation_disposes_owned_dependencies_preserves_error_and_never_listens(bool canceled)
    {
        var connection = await NewDatabaseAsync();
        if (!canceled)
        {
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            await using var obstruction = new NpgsqlCommand("CREATE TABLE applications (unexpected integer)", db);
            await obstruction.ExecuteNonQueryAsync();
        }
        var port = OperatorHostCompositionTests.AvailablePort();
        var resource = new MigrationResource();
        using var cancellation = new CancellationTokenSource();
        if (canceled) cancellation.Cancel();
        var calls = 0;
        var error = await Record.ExceptionAsync(async () => await OperatorHostApplication.BuildComposedMigratedAsync(
            ["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"],
            (services, _) =>
            {
                calls++;
                services.AddSingleton(_ => resource);
                // Test registration eagerly resolves an owned resource without changing migration.
                services.AddSingleton(provider =>
                {
                    provider.GetRequiredService<MigrationResource>();
                    return new ProviderPreflightRegistry(provider.GetServices<IProviderPreflightHandler>(),
                        provider.GetServices<IProviderConfigurationCodec>(), provider.GetServices<IProviderDeclarationAdapter>());
                });
            }, cancellation.Token));
        if (canceled) Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        else Assert.IsType<PostgresException>(error);
        Assert.Equal(1, calls);
        Assert.True(resource.Disposed);
        await OperatorHostCompositionTests.RequireNotListeningAsync(port);
    }

    private sealed class MigrationResource : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }
        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Disposed = true;
            throw new InvalidOperationException("cleanup sentinel must not replace migration failure");
        }
    }

    private sealed class CompositionProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _error;
        internal int? DiagnosticExitCode { get; private set; }
        internal CompositionProcess(string binary, string connection, string working, string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = working,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            start.ArgumentList.Add(binary);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["Haby__PostgreSql__ConnectionString"] = connection;
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            _process = Process.Start(start)!;
            _error = _process.StandardError.ReadToEndAsync();
        }
        internal async Task ExpectAsync(string expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.Equal(expected, await _process.StandardOutput.ReadLineAsync(timeout.Token));
        }
        internal async Task SendAsync(string command) => await _process.StandardInput.WriteLineAsync(command);
        internal async Task RequireExitAsync(int expected)
        {
            var stdout = _process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _process.WaitForExitAsync(timeout.Token);
            Assert.Equal(expected, _process.ExitCode);
            // Never reproduce credential-bearing child output in assertions or logs.
            Assert.DoesNotContain("haby_operator_v1.", await stdout);
            Assert.DoesNotContain("haby_operator_v1.", await _error);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _process.WaitForExitAsync(timeout.Token);
                DiagnosticExitCode = _process.ExitCode;
                await _error;
            }
            finally { _process.Dispose(); }
        }
    }
}
