using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Secret_generated_client_restart_and_native_CLI_preserve_exact_material_and_receipts()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        var requestId = Guid.NewGuid().ToString();
        var bytes = new byte[] { 0xef, 0xbb, 0xbf, 65, 13, 10, 10 };
        Haby.Operator.Contracts.SecretMetadata original;
        await using (var host = await OperatorHostApplication.BuildMigratedAsync(args))
        {
            await host.StartAsync();
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var request = new CreateSecretRequest { Environment = "pilot", Name = "admin", RequestId = requestId, Material = ByteString.CopyFrom(bytes) };
            Assert.Equal(SecretStatus.EnvironmentNotFound, (await client.CreateSecretAsync(request)).Status);
            Assert.Equal(EnvironmentStatus.NotFound, (await client.GetEnvironmentAsync(new() { Name = "pilot" })).Status);
            await client.CreateEnvironmentAsync(new() { Name = "pilot" });
            foreach (var size in new[] { 0, 1, 65_536, 65_537 })
            {
                var result = await client.CreateSecretAsync(new()
                { Environment = "pilot", Name = "size-" + size, RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFrom(new byte[size]) });
                Assert.Equal(size is 1 or 65_536 ? SecretStatus.Created : SecretStatus.InvalidRequest, result.Status);
            }
            Assert.Equal(SecretStatus.InvalidRequest, (await client.CreateSecretAsync(new()
            { Environment = "pilot", Name = "admin", RequestId = "bad", Material = request.Material })).Status);
            var first = await client.CreateSecretAsync(request);
            Assert.Equal(SecretStatus.Created, first.Status);
            original = first.Secret;
            Assert.Equal(requestId, first.RequestId);
            await host.StopAsync();
        }
        await using (var host = await OperatorHostApplication.BuildMigratedAsync(args))
        {
            await host.StartAsync();
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var retry = await client.CreateSecretAsync(new()
            { Environment = "pilot", Name = "admin", RequestId = requestId, Material = ByteString.CopyFrom(bytes) });
            Assert.Equal(SecretStatus.ExistingEquivalent, retry.Status);
            Assert.Equal(original, retry.Secret);
            Assert.Equal(original, (await client.GetSecretAsync(new() { Environment = "pilot", Name = "admin" })).Secret);
            Assert.Equal(original, (await client.GetSecretAsync(new() { Environment = "pilot", SecretId = original.SecretId })).Secret);
            var page = await client.ListSecretsAsync(new() { Environment = "pilot", PageSize = 1 });
            Assert.Equal(SecretStatus.Listed, page.Status);
            Assert.Equal(original, Assert.Single(page.Items));
            Assert.StartsWith("secret1:" + original.EnvironmentRecordId + ":", page.NextCursor);

            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
            if (binary is not null)
            {
                Assert.True(File.Exists(binary));
                foreach (var format in new[] { "text", "json" })
                    foreach (var verb in new[] { "create", "get", "list" })
                    {
                        var start = new ProcessStartInfo(binary)
                        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        var arguments = new List<string> { "secret", verb };
                        if (verb != "list") arguments.Add("admin");
                        arguments.AddRange(["--environment", "pilot", "-o", format]);
                        if (verb == "create") arguments.AddRange(["--stdin", "--request-id", requestId]);
                        foreach (var argument in arguments) start.ArgumentList.Add(argument);
                        start.Environment["HABY_OPERATOR_URL"] = host.Urls.Single();
                        using var process = Process.Start(start)!;
                        var stdout = process.StandardOutput.ReadToEndAsync();
                        var stderr = process.StandardError.ReadToEndAsync();
                        await process.StandardInput.BaseStream.WriteAsync(bytes);
                        process.StandardInput.Close();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(timeout.Token); }
                        catch { process.Kill(entireProcessTree: true); throw; }
                        Assert.Equal(0, process.ExitCode);
                        Assert.Empty(await stderr);
                        var text = await stdout;
                        Assert.Contains(original.SecretId, text);
                        Assert.Contains(original.Versions[0].VersionId, text);
                        Assert.True(!text.Contains(Convert.ToBase64String(bytes), StringComparison.Ordinal), "Command output must redact protected input.");
                        if (format == "json")
                        {
                            using var json = JsonDocument.Parse(text);
                            if (verb == "create") Assert.Equal(requestId, json.RootElement.GetProperty("data"u8).GetProperty("requestId"u8).GetString());
                        }
                        else Assert.DoesNotContain(requestId, text);
                    }
            }
            // Resolve only internally through the configured backend, with an independent factory context.
            using var material = await host.Services.GetRequiredService<ISecretStore>().ResolveAsync(
                EnvironmentRecordId.Parse(original.EnvironmentRecordId),
                new SecretRef(SecretId.Parse(original.SecretId), SecretVersionId.Parse(original.Versions[0].VersionId)), default);
            Assert.NotNull(material);
            Assert.True(material.CopyBytes().AsSpan().SequenceEqual(bytes), "Stored BOM/newline bytes must be exact.");
            await host.StopAsync();
        }
    }
}
