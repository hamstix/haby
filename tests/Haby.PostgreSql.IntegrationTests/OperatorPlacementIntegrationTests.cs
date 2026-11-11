using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Kubernetes.Contracts;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Placement_authenticated_RPC_and_native_CLI_restart_full_reimport_and_safe_refusals()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        var logs = new ProfileLog(); PlacementReply accepted;
        await using (var host = await ProfileHost(args, logs))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var anonymous = new ManifestOperator.ManifestOperatorClient(channel);
            foreach (var operation in new Func<Task>[] {
                async () => await anonymous.ValidatePlacementAsync(new()), async () => await anonymous.ImportPlacementAsync(new()),
                async () => await anonymous.GetPlacementRevisionAsync(new()) })
                Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(operation)).StatusCode);
            var client = OperatorTestCredentials.Client(channel);
            foreach (var import in new[] { false, true })
            {
                var request = new PlacementPayloadRequest { Environment = "pilot", ProviderAlias = "cluster", Payload = ByteString.CopyFrom(new byte[1048577]) };
                var error = await Assert.ThrowsAsync<RpcException>(async () =>
                { if (import) await client.ImportPlacementAsync(request); else await client.ValidatePlacementAsync(request); });
                Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
            }
            foreach (var source in new[] { PlacementSamples.Full, PlacementSamples.Minimal })
            {
                var valid = await client.ValidatePlacementAsync(new() { Payload = ByteString.CopyFromUtf8(source) });
                Assert.Equal(PlacementStatus.Valid, valid.Status); Assert.Null(valid.Metadata);
            }
            foreach (var source in new[] { "null", "{}", "{bad", "{\"private-marker\":null}",
                PlacementSamples.Full.Replace("\"namespace\":", "\"namespace\":\"duplicate\",\"namespace\":"),
                PlacementSamples.Full.Replace("\"namespace\":", "\"private-marker\":true,\"namespace\":"),
                PlacementSamples.Full.Replace("\"namespace\":\"apps\"", "\"namespace\":null"),
                PlacementSamples.Full.Replace("\"cpu\":\"1\"", "\"cpu\":\"100m\""),
                PlacementSamples.Full.Replace("\"revision\":1", "\"revision\":0"), new string('[',65) + "0" + new string(']',65) })
            {
                var invalid = await client.ValidatePlacementAsync(new() { Payload = ByteString.CopyFromUtf8(source) });
                Assert.Equal(PlacementStatus.InvalidDeclaration, invalid.Status); Assert.Null(invalid.Reference); Assert.Null(invalid.Metadata);
                Assert.NotEmpty(invalid.DiagnosticCode); Assert.DoesNotContain("private-marker", invalid.ToString());
            }
            Assert.Equal(PlacementStatus.EnvironmentNotFound, (await client.ImportPlacementAsync(PlacementPayload())).Status);
            await client.CreateEnvironmentAsync(new() { Name = "pilot" });
            Assert.Equal(PlacementStatus.InvalidContext, (await client.ImportPlacementAsync(new() { Environment = "pilot", ProviderAlias = "BAD", Payload = ByteString.CopyFromUtf8(PlacementSamples.Full) })).Status);
            Assert.Equal(PlacementStatus.InvalidDeclaration, (await client.ImportPlacementAsync(PlacementPayload("{}"))).Status);
            await using (var verify = ProfileContext(connection)) Assert.Empty(await verify.PlacementRevisions.AsNoTracking().ToArrayAsync());
            accepted = await client.ImportPlacementAsync(PlacementPayload());
            Assert.Equal(PlacementStatus.Created, accepted.Status); Assert.StartsWith("operator-grant:", accepted.Metadata.TechnicalActorId);
            Assert.True(Guid.TryParse(accepted.Metadata.RecordId, out _));
            var full = await client.GetPlacementRevisionAsync(accepted.Reference);
            Assert.Equal(PlacementStatus.Found, full.Status);
            var codec = new KubernetesPlacementCodec();
            Assert.Equal(codec.Decode(Encoding.UTF8.GetBytes(PlacementSamples.Full)), codec.Decode(full.DeclarationJson.Memory));
            Assert.Equal(PlacementStatus.Created, (await client.ImportPlacementAsync(PlacementPayload(PlacementSamples.Minimal.Replace("standard-runtime", "minimal")))).Status);
            await PlacementNative(host, accepted);
            await host.StopAsync();
        }
        await using (var host = await ProfileHost(args, logs))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            var found = await client.GetPlacementRevisionAsync(accepted.Reference);
            Assert.Equal(PlacementStatus.Found, found.Status); Assert.Equal(accepted.Metadata, found.Metadata);
            var retry = await client.ImportPlacementAsync(new() { Environment = "pilot", ProviderAlias = "cluster", Payload = found.DeclarationJson });
            Assert.Equal(PlacementStatus.ExistingEquivalent, retry.Status); Assert.Equal(accepted.Metadata, retry.Metadata);
            Assert.Equal(PlacementStatus.Valid, (await client.ValidatePlacementAsync(new() { Payload = found.DeclarationJson })).Status);
            Assert.Equal(PlacementStatus.RevisionContentConflict, (await client.ImportPlacementAsync(PlacementPayload(PlacementSamples.Full.Replace("\"replicas\":3", "\"replicas\":4")))).Status);
            foreach (var reference in new[] {
                new PlacementRevisionRequest { Environment = "pilot", ProviderAlias = "other", Name = "standard-runtime", Revision = 1 },
                new PlacementRevisionRequest { Environment = "pilot", ProviderAlias = "cluster", Name = "other", Revision = 1 },
                new PlacementRevisionRequest { Environment = "pilot", ProviderAlias = "cluster", Name = "standard-runtime", Revision = 2 } })
                Assert.Equal(PlacementStatus.NotFound, (await client.GetPlacementRevisionAsync(reference)).Status);
            Assert.Equal(PlacementStatus.InvalidContext, (await client.GetPlacementRevisionAsync(new())).Status);
            var minimal = await client.GetPlacementRevisionAsync(new() { Environment = "pilot", ProviderAlias = "cluster", Name = "minimal", Revision = 1 });
            Assert.Equal(PlacementStatus.Found, minimal.Status);
            Assert.Null(KubernetesPlacementCodec.ToTyped(new KubernetesPlacementCodec().Decode(minimal.DeclarationJson.Memory)).Settings.Replicas);
            await PlacementNative(host, accepted);
            await using (var verify = ProfileContext(connection))
            {
                Assert.Empty(await verify.ProviderInstances.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.ProviderAliases.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.Secrets.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.DurablePlans.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
            }
            await using (var arrange = ProfileContext(connection))
                await arrange.Database.ExecuteSqlRawAsync("UPDATE placement_revisions SET payload = 'private-corrupt-marker' WHERE name = 'standard-runtime'");
            foreach (var operation in new Func<Task>[] { async () => await client.GetPlacementRevisionAsync(accepted.Reference), async () => await client.ImportPlacementAsync(PlacementPayload()) })
            {
                var error = await Assert.ThrowsAsync<RpcException>(operation); Assert.Equal(StatusCode.Internal, error.StatusCode);
                Assert.Equal("Placement operation failed.", error.Status.Detail);
            }
            await host.StopAsync();
        }
        Assert.DoesNotContain("private-corrupt-marker", logs.Text); Assert.DoesNotContain("Npgsql.PostgresException", logs.Text);
    }

    private static PlacementPayloadRequest PlacementPayload(string source = PlacementSamples.Full) => new()
    { Environment = "pilot", ProviderAlias = "cluster", Payload = ByteString.CopyFromUtf8(source) };

    private static async Task PlacementNative(WebApplication host, PlacementReply original)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
        if (binary is null) return;
        Assert.True(File.Exists(binary));
        var folder = Path.Combine(Path.GetTempPath(), "haby-placement-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.yaml"); var file = Path.Combine(folder, "placement.json"); var extracted = Path.Combine(folder, "extracted.json");
        try
        {
            await OperatorTestCredentials.WriteConfigAsync(config, host.Urls.Single()); await File.WriteAllTextAsync(file, PlacementSamples.Full);
            foreach (var format in new[] { "text", "json" })
                foreach (var (arguments, outcome, exit) in new (string[], string, int)[] {
                    (["placement", "validate", file], "valid", 0),
                    (["placement", "import", file, "--provider-alias", "cluster"], "existingEquivalent", 0),
                    (["placement", "revision", "get", "standard-runtime", "1", "--provider-alias", "cluster"], "found", 0),
                    (["placement", "revision", "get", "standard-runtime", "2", "--provider-alias", "cluster"], "notFound", 1) })
                {
                    var result = await Invoke(arguments, format); Assert.Equal(exit, result.Exit); Assert.Equal("", result.Error);
                    if (format == "text") { Assert.Contains(outcome, result.Output); continue; }
                    using var json = JsonDocument.Parse(result.Output); Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                    var data = json.RootElement.GetProperty("data"u8);
                    if (outcome == "found")
                    {
                        Assert.Equal(original.Metadata.RecordId, data.GetProperty("recordId"u8).GetString());
                        Assert.Equal(original.Metadata.ImportedAtUtc, data.GetProperty("importedAtUtc"u8).GetString());
                        var declaration = data.GetProperty("declaration"u8).GetRawText();
                        var codec = new KubernetesPlacementCodec(); Assert.Equal(codec.Decode(Encoding.UTF8.GetBytes(PlacementSamples.Full)), codec.Decode(Encoding.UTF8.GetBytes(declaration)));
                        await File.WriteAllTextAsync(extracted, declaration);
                        foreach (var command in new[] { "validate", "import" })
                        {
                            var reimport = await Invoke(command == "validate" ? ["placement", command, extracted] : ["placement", command, extracted, "--provider-alias", "cluster"], "json");
                            Assert.Equal(0, reimport.Exit); Assert.Equal("", reimport.Error);
                            using var parsed = JsonDocument.Parse(reimport.Output); Assert.Equal(command == "validate" ? "valid" : "existingEquivalent", parsed.RootElement.GetProperty("outcome"u8).GetString());
                        }
                    }
                    else Assert.False(data.TryGetProperty("declaration"u8, out _));
                }
            async Task<(int Exit, string Output, string Error)> Invoke(string[] arguments, string format)
            {
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in arguments.Concat(new[] { "--config", config, "-o", format })) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                return (process.ExitCode, await stdout, await stderr);
            }
        }
        finally { foreach (var path in new[] { config, file, extracted }) File.Delete(path); Directory.Delete(folder); }
    }
}
