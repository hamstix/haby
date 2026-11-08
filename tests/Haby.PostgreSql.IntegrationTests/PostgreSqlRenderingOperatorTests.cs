using System.Text.Json.Nodes;
using System.Diagnostics;
using Google.Protobuf;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Fact]
    public async Task Generated_Host_import_and_plan_validate_partial_layers_compatibility_and_safe_refusals_before_outputs_exist()
    {
        var scenario = await Prepare(full: true, render: true);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection,
            "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = OperatorTestCredentials.Client(channel);
        var node = PostgreSqlPairPlanningTests.Example();
        node["manifest"]!["metadata"]!["id"] = "render-operator";
        foreach (var component in new[] { "api", "worker" })
            node["manifest"]!["spec"]!["components"]![component]!["bindings"]!["storage"]!["configurationSlot"] = "main-database";
        var options = node["manifest"]!["spec"]!["components"]!["api"]!["bindings"]!["storage"]!;
        options["options"] = JsonNode.Parse("{\"timeout\":0}"u8);
        Assert.NotNull((await client.ImportAsync(new() { OriginalEnvelope = Bytes(node) })).InvalidManifest);
        options["options"] = JsonNode.Parse("{\"timeout\":2e2}"u8);
        var imported = (await client.ImportAsync(new() { OriginalEnvelope = Bytes(node) })).Created!;
        Assert.NotNull(imported);
        var change = (await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = "pilot", Precondition = PlanningPrecondition.ExpectedAbsence, Reference = imported.Revision.Reference
        })).ChangeSet;
        var planned = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, planned.Status);
        var saved = (await host.Services.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(DeploymentPlanId.Parse(planned.Plan.PlanId), default))!;
        Assert.Equal(2, saved.Inputs.Configuration!.RenderPins.Count);
        Assert.DoesNotContain("sslMode", planned.ToString()); Assert.DoesNotContain("timeout", planned.ToString());
        await NativeRendererOutcome(host.Urls.Single(), "current", null, "plan", "get", planned.Plan.PlanId);
        foreach (var mode in new[] { "invalid-layer", "unknown-renderer", "incompatible" })
        {
            var profile = JsonNode.Parse(RenderingProfile)!;
            profile["metadata"]!["revision"] = mode == "invalid-layer" ? 2 : mode == "unknown-renderer" ? 3 : 4;
            var rule = profile["spec"]!["bindingRules"]!["connection"]!;
            if (mode == "invalid-layer") rule["options"]!["timeout"] = 0; // Binding's 200 must not hide this invalid partial layer.
            if (mode == "unknown-renderer") rule["rendererRef"] = "unavailable.connection/v1";
            Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(new() { Payload = Bytes(profile) })).Status);
            var environment = new Haby.Operator.Contracts.ProfileAssignmentTarget { Kind = "environment" };
            var prior = (await client.GetProfileAssignmentAsync(new() { Environment = "pilot", Target = environment })).Assignment;
            var assignment = JsonNode.Parse("""
                {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfileAssignment","spec":{"target":{"kind":"environment"},
                "decisions":[{"kind":"use","profileRef":{"profileId":"connection-layout","revision":1}}]}}
                """u8)!;
            assignment["spec"]!["decisions"]![0]!["profileRef"]!["revision"] = profile["metadata"]!["revision"]!.DeepClone();
            Assert.Equal(ProfileAssignmentStatus.Updated, (await client.ApplyProfileAssignmentAsync(new()
            { Environment = "pilot", Payload = Bytes(assignment), ExpectedConcurrencyToken = prior.ConcurrencyToken })).Status);
            string changeId = change.ChangeSetId;
            if (mode == "incompatible")
            {
                node["applicationVersion"] = "2";
                foreach (var component in new[] { "api", "worker" })
                {
                    var binding = node["manifest"]!["spec"]!["components"]![component]!["bindings"]!["storage"]!;
                    binding["target"]!["resourceRef"] = "db"; binding["options"] = new JsonObject();
                }
                var next = (await client.ImportAsync(new() { OriginalEnvelope = Bytes(node) })).Created!;
                changeId = (await client.CreateChangeSetAsync(new()
                { EnvironmentId = "pilot", Precondition = PlanningPrecondition.ExpectedAbsence, Reference = next.Revision.Reference })).ChangeSet.ChangeSetId;
            }
            var refused = await client.CreatePlanAsync(new() { ChangeSetId = changeId });
            Assert.Equal(PlanningStatus.ConfigurationRefused, refused.Status); Assert.Null(refused.Plan);
            Assert.Contains(mode == "invalid-layer" ? "render.options.invalid" : mode == "unknown-renderer" ? "render.renderer.unknown" : "render.output.incompatible", refused.ConfigurationErrorCodes);
            Assert.DoesNotContain("unavailable.connection", refused.ToString()); Assert.DoesNotContain("options", refused.ToString().Replace("render.options.invalid", "", StringComparison.Ordinal));
            await NativeRendererOutcome(host.Urls.Single(), "configurationRefused", refused.ConfigurationErrorCodes.Single(), "plan", "create", changeId);
        }
        await using var verify = Context(scenario.Connection);
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
        await host.StopAsync();
        static ByteString Bytes(JsonNode value) => ByteString.CopyFromUtf8(value.ToJsonString());
    }

    private static async Task NativeRendererOutcome(string endpoint, string outcome, string? code, params string[] args)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        var config = Path.Combine(Path.GetTempPath(), "haby-renderer-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: pilot\ninstances:\n  local:\n    endpoint: {endpoint}\n    token: {OperatorTestCredentials.Credential(endpoint)}\ncontexts:\n  pilot:\n    instance: local\n    environmentId: pilot\n");
        try
        {
            foreach (var format in new[] { "text", "json" })
            {
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args.Concat(new[] { "--config", config, "-o", format })) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                Assert.Equal(code is null ? 0 : 1, process.ExitCode); Assert.Equal("", await stderr);
                var text = await stdout; Assert.Contains(outcome, text);
                if (code is not null) Assert.Contains(code, text);
                foreach (var hidden in new[] { "sslMode", "secretRef", "template", "unavailable.connection", "deferred" }) Assert.DoesNotContain(hidden, text);
            }
        }
        finally { File.Delete(config); }
    }
}
