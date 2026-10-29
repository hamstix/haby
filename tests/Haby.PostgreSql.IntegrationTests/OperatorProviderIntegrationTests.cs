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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Provider_generated_client_and_native_CLI_apply_and_read_safe_metadata_after_restart()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        Haby.Operator.Contracts.ProviderMetadata original;
        await using (var host = await OperatorHostApplication.BuildMigratedAsync(args))
        {
            await host.StartAsync();
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            await client.CreateEnvironmentAsync(new() { Name = "pilot" });
            await client.CreateSecretAsync(new() { Environment = "pilot", Name = "admin", RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8("protected-material") });
            var request = new ApplyProviderRequest { Declaration = ByteString.CopyFromUtf8(ProviderDeclarationTests.Declaration) };
            var first = await client.ApplyProviderAsync(request);
            Assert.Equal(ProviderStatus.Created, first.Status);
            original = first.Provider;
            request.Environment = "other";
            Assert.Equal(ProviderStatus.EnvironmentMismatch, (await client.ApplyProviderAsync(request)).Status);
            request.ClearEnvironment();
            request.Declaration = ByteString.CopyFromUtf8(ProviderDeclarationTests.Declaration.Replace("\"environment\":\"pilot\"", "\"environment\":null", StringComparison.Ordinal));
            Assert.Equal(ProviderStatus.InvalidRequest, (await client.ApplyProviderAsync(request)).Status);
            await host.StopAsync();
        }
        await using (var host = await OperatorHostApplication.BuildMigratedAsync(args))
        {
            await host.StartAsync();
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(original, (await client.GetProviderAsync(new() { Environment = "pilot", Name = "pg-main", Revision = 1 })).Provider);
            var first = await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(ProviderDeclarationTests.Declaration) });
            Assert.Equal(ProviderStatus.ExistingEquivalent, first.Status);
            Assert.Equal(original, first.Provider);
            Assert.Equal(original, Assert.Single((await client.ListProvidersAsync(new() { Environment = "pilot" })).Items));
            Assert.DoesNotContain("unreachable.invalid", first.ToString());
            Assert.DoesNotContain("protected-material", first.ToString());
            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
            if (binary is not null)
            {
                var file = Path.Combine(Path.GetTempPath(), "haby-provider-" + Guid.NewGuid().ToString("N") + ".json");
                var config = file + ".yaml";
                try
                {
                    await File.WriteAllTextAsync(file, ProviderDeclarationTests.Declaration);
                    await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: wrong-env\ninstances:\n  local:\n    endpoint: {host.Urls.Single()}\ncontexts:\n  wrong-env:\n    instance: local\n    environmentId: other\n");
                    foreach (var format in new[] { "text", "json" })
                        foreach (var command in new[] { "apply", "get", "list", "revision" })
                        {
                            var arguments = command switch
                            {
                                "apply" => new[] { "provider", "apply", "--file", file },
                                "get" => ["provider", "get", "pg-main", "--environment", "pilot"],
                                "revision" => ["provider", "revision", "get", "pg-main", "--revision", "1", "--environment", "pilot"],
                                _ => ["provider", "list", "--environment", "pilot"]
                            };
                            var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                            foreach (var argument in arguments.Concat(["--config", config, "-o", format])) start.ArgumentList.Add(argument);
                            using var process = Process.Start(start)!;
                            var stdout = process.StandardOutput.ReadToEndAsync();
                            var stderr = process.StandardError.ReadToEndAsync();
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            try { await process.WaitForExitAsync(timeout.Token); }
                            catch { process.Kill(entireProcessTree: true); throw; }
                            Assert.Equal(0, process.ExitCode);
                            Assert.Empty(await stderr);
                            var text = await stdout;
                            Assert.Contains(original.InstanceId, text);
                            Assert.DoesNotContain("unreachable.invalid", text);
                            Assert.DoesNotContain("protected-material", text);
                            if (format == "json")
                            {
                                using var json = JsonDocument.Parse(text);
                                Assert.Equal(1, json.RootElement.GetProperty("formatVersion"u8).GetInt32());
                            }
                        }
                }
                finally { File.Delete(file); File.Delete(config); }
            }
            await using var check = await host.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Single(await check.ProviderInstances.AsNoTracking().ToArrayAsync());
            Assert.Single(await check.ProviderConfigurationRevisions.AsNoTracking().ToArrayAsync());
            Assert.Empty(await check.ProviderAliases.AsNoTracking().ToArrayAsync());
            Assert.Empty(await check.DurablePlans.AsNoTracking().ToArrayAsync());
            Assert.Empty(await check.DurableCreateOperations.AsNoTracking().ToArrayAsync());
            await host.StopAsync();
        }
        // Missing support is not a startup or metadata failure, and cannot substitute an adapter.
        await using (var host = OperatorHostApplication.Build(args, services =>
        {
            services.RemoveAll<IProviderDeclarationAdapter>();
            services.RemoveAll<IProviderConfigurationCodec>();
        }))
        {
            await host.StartAsync();
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(original, (await client.GetProviderAsync(new() { Environment = "pilot", Name = "pg-main", Revision = 1 })).Provider);
            Assert.Single((await client.ListProvidersAsync(new() { Environment = "pilot" })).Items);
            Assert.Equal(ProviderStatus.UnsupportedConfiguration, (await client.ApplyProviderAsync(new()
            { Declaration = ByteString.CopyFromUtf8(ProviderDeclarationTests.Declaration) })).Status);
            await host.StopAsync();
        }
        Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(args, services =>
            services.AddSingleton<IProviderDeclarationAdapter, PostgreSqlProviderDeclarationAdapter>()));
    }
}
