using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Operator.Contracts;
using Haby.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Target = Haby.Operator.Contracts.PlacementSelectionTarget;
using LogicalTarget = Haby.Application.PlacementSelectionTarget;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    private static Target SelectionTarget(string kind) => new()
    {
        Kind = kind,
        Selector = kind == "named" ? "production" : "",
        ApplicationId = kind is "application" or "component" ? "assigned" : "",
        ComponentRef = kind == "component" ? "api" : ""
    };
    private static ApplyPlacementSelectionRequest SelectionApply(Target target, int revision = 1, string? token = null, string? version = null)
    {
        var request = new ApplyPlacementSelectionRequest { Environment = "pilot", ProviderAlias = "cluster", Target = target, PlacementName = "standard-runtime", Revision = revision };
        if (token is null) request.ExpectedAbsence = true; else request.ExpectedToken = token;
        if (version is not null) request.ApplicationVersion = version;
        return request;
    }
    [Fact]
    public async Task Placement_selection_real_Host_all_targets_exact_versions_CAS_restart_bridge_and_native_CLI()
    {
        var connection = await NewDatabaseAsync(); var logs = new ProfileLog();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        var states = new List<PlacementSelectionState>();
        await using (var host = await ProfileHost(args, logs))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var anonymous = new ManifestOperator.ManifestOperatorClient(channel);
            foreach (var action in new Func<Task>[] { async () => await anonymous.GetPlacementSelectionAsync(new()), async () => await anonymous.ApplyPlacementSelectionAsync(new()) })
                Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(action)).StatusCode);
            var client = OperatorTestCredentials.Client(channel); var env = SelectionTarget("environment"); var component = SelectionTarget("component");
            Assert.Equal(PlacementSelectionStatus.EnvironmentNotFound, (await client.ApplyPlacementSelectionAsync(SelectionApply(env))).Status);
            await client.CreateEnvironmentAsync(new() { Name = "pilot" }); await client.CreateEnvironmentAsync(new() { Name = "other" });
            Assert.Equal(PlacementSelectionStatus.NotFound, (await client.GetPlacementSelectionAsync(new() { Environment = "pilot", ProviderAlias = "cluster", Target = env })).Status);
            Assert.Equal(PlacementSelectionStatus.RevisionNotFound, (await client.ApplyPlacementSelectionAsync(SelectionApply(env))).Status);
            foreach (var revision in new[] { 1, 2 }) Assert.Equal(PlacementStatus.Created, (await client.ImportPlacementAsync(PlacementPayload(PlacementSamples.Minimal.Replace("\"revision\":1", "\"revision\":" + revision)))).Status);
            Assert.Equal(PlacementSelectionStatus.ApplicationNotFound, (await client.ApplyPlacementSelectionAsync(SelectionApply(SelectionTarget("application")))).Status);
            var v2 = await ImportAssignmentApp(client, "2", "api", "worker"); await ImportAssignmentApp(client, "1", "worker");
            foreach (var (version, outcome) in new (string?, PlacementSelectionStatus)[] { (null, PlacementSelectionStatus.InvalidContext), ("", PlacementSelectionStatus.InvalidContext),
                ("missing", PlacementSelectionStatus.ApplicationVersionNotFound), ("1", PlacementSelectionStatus.ComponentNotFound) })
                Assert.Equal(outcome, (await client.ApplyPlacementSelectionAsync(SelectionApply(component, version: version))).Status);
            foreach (var kind in new[] { "environment", "named", "application" })
                Assert.Equal(PlacementSelectionStatus.InvalidContext, (await client.ApplyPlacementSelectionAsync(SelectionApply(SelectionTarget(kind), version: "2"))).Status);
            foreach (var scope in new[] { "alias", "environment" })
            {
                var invalid = SelectionApply(env); if (scope == "alias") invalid.ProviderAlias = "other"; else invalid.Environment = "other";
                Assert.Equal(PlacementSelectionStatus.RevisionNotFound, (await client.ApplyPlacementSelectionAsync(invalid)).Status);
            }
            foreach (var invalid in new[] { new Target(), new Target { Kind = "environment", Selector = "extra" }, new Target { Kind = "named", ApplicationId = "extra", Selector = "name" },
                new Target { Kind = "application", ApplicationId = "assigned", ComponentRef = "extra" }, new Target { Kind = "component", ComponentRef = "api" },
                Target.Parser.ParseFrom(new Target { Kind = "environment" }.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray()) })
            {
                Assert.Equal(PlacementSelectionStatus.InvalidContext, (await client.ApplyPlacementSelectionAsync(SelectionApply(invalid))).Status);
                Assert.Equal(PlacementSelectionStatus.InvalidContext, (await client.GetPlacementSelectionAsync(new() { Environment = "pilot", ProviderAlias = "cluster", Target = invalid })).Status);
            }
            foreach (var mutation in new[] { "missing", "false", "ambiguous", "token", "name", "revision", "alias", "scope", "target" })
            {
                var invalid = SelectionApply(env);
                switch (mutation)
                {
                    case "missing": invalid.ClearExpectedAbsence(); invalid.ClearExpectedToken(); break;
                    case "false": invalid.ExpectedAbsence = false; break;
                    case "ambiguous": invalid.ExpectedToken = Guid.NewGuid().ToString(); break;
                    case "token": invalid.ExpectedToken = "invalid"; break;
                    case "name": invalid.PlacementName = ""; break;
                    case "revision": invalid.Revision = 0; break;
                    case "alias": invalid.ProviderAlias = ""; break;
                    case "scope": invalid.Environment = ""; break;
                    case "target": invalid.Target = null; break;
                }
                Assert.Equal(PlacementSelectionStatus.InvalidContext, (await client.ApplyPlacementSelectionAsync(invalid)).Status);
            }
            await using (var verify = ProfileContext(connection)) Assert.Empty(await verify.PlacementSelections.AsNoTracking().ToArrayAsync());
            foreach (var kind in new[] { "named", "environment", "application", "component" })
            {
                var target = SelectionTarget(kind); var version = kind == "component" ? "2" : null;
                var created = await client.ApplyPlacementSelectionAsync(SelectionApply(target, version: version)); Assert.Equal(PlacementSelectionStatus.Created, created.Status);
                if (kind == "component") Assert.Equal(v2, created.Selection.CheckedRevision); else Assert.Null(created.Selection.CheckedRevision);
                Assert.Equal(PlacementSelectionStatus.Conflict, (await client.ApplyPlacementSelectionAsync(SelectionApply(target, version: version))).Status);
                var unchanged = await client.ApplyPlacementSelectionAsync(SelectionApply(target, token: created.Selection.ConcurrencyToken, version: version));
                Assert.Equal(PlacementSelectionStatus.Unchanged, unchanged.Status); Assert.Equal(created.Selection, unchanged.Selection);
                var updated = await client.ApplyPlacementSelectionAsync(SelectionApply(target, 2, created.Selection.ConcurrencyToken, version));
                Assert.Equal(PlacementSelectionStatus.Updated, updated.Status); Assert.NotEqual(created.Selection.ConcurrencyToken, updated.Selection.ConcurrencyToken);
                Assert.Equal(2, updated.Selection.Reference.Revision);
                Assert.Equal(PlacementSelectionStatus.Conflict, (await client.ApplyPlacementSelectionAsync(SelectionApply(target, 2, created.Selection.ConcurrencyToken, version))).Status);
                states.Add(updated.Selection);
            }
            var old = states.Single(x => x.Target.Kind == "component"); var v0 = await ImportAssignmentApp(client, "0", "api");
            var equalContext = await client.ApplyPlacementSelectionAsync(SelectionApply(component, 2, old.ConcurrencyToken, "0"));
            Assert.Equal(PlacementSelectionStatus.Unchanged, equalContext.Status); Assert.Equal(old, equalContext.Selection); Assert.NotEqual(v0, equalContext.Selection.CheckedRevision);
            Assert.Equal(PlacementSelectionStatus.ComponentNotFound, (await client.ApplyPlacementSelectionAsync(SelectionApply(component, 2, old.ConcurrencyToken, "1"))).Status);
            await ImportAssignmentApp(client, "9", "renamed");
            await AssertPersistedPlacementBridge(host);
            for (var i = 0; i < states.Count; i++) states[i] = (await client.GetPlacementSelectionAsync(new() { Environment = "pilot", ProviderAlias = "cluster", Target = states[i].Target })).Selection;
            await PlacementSelectionNative(host, states);
            await host.StopAsync();
        }
        await using (var host = await ProfileHost(args, logs))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            foreach (var state in states) Assert.Equal(state, (await client.GetPlacementSelectionAsync(new() { Environment = "pilot", ProviderAlias = "cluster", Target = state.Target })).Selection);
            await PlacementSelectionNative(host, states);
            await using (var verify = ProfileContext(connection))
            {
                Assert.Equal(4, await verify.PlacementSelections.CountAsync(x => x.ProviderAlias == "cluster")); Assert.Equal(4, await verify.PlacementRevisions.CountAsync(x => x.ProviderAlias == "cluster"));
                Assert.Empty(await verify.ProviderInstances.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.Secrets.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.DurablePlans.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
            }
            await using (var arrange = ProfileContext(connection)) await arrange.Database.ExecuteSqlRawAsync("UPDATE placement_revisions SET payload = 'private-selection-marker'");
            foreach (var action in new Func<Task>[] { async () => await client.GetPlacementSelectionAsync(new() { Environment = "pilot", ProviderAlias = "cluster", Target = SelectionTarget("environment") }),
                async () => await client.ApplyPlacementSelectionAsync(SelectionApply(SelectionTarget("environment"), 2, states[1].ConcurrencyToken)) })
            {
                var error = await Assert.ThrowsAsync<RpcException>(action); Assert.Equal(StatusCode.Internal, error.StatusCode); Assert.Equal("Placement selection operation failed.", error.Status.Detail);
            }
            await host.StopAsync();
        }
        Assert.DoesNotContain("private-selection-marker", logs.Text); Assert.DoesNotContain("Npgsql.PostgresException", logs.Text);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("checked-context")]
    public async Task Placement_selection_corrupt_audit_or_checked_context_refuses_without_repair(string corruption)
    {
        var connection = await NewDatabaseAsync(); var logs = new ProfileLog();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        await using var host = await ProfileHost(args, logs);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        await client.CreateEnvironmentAsync(new() { Name = "pilot" }); await client.ImportPlacementAsync(PlacementPayload(PlacementSamples.Minimal));
        await ImportAssignmentApp(client, "2", "api");
        var original = (await client.ApplyPlacementSelectionAsync(SelectionApply(SelectionTarget("component"), version: "2"))).Selection;
        await using (var arrange = ProfileContext(connection))
            await arrange.Database.ExecuteSqlRawAsync(corruption == "audit" ? "UPDATE placement_selections SET actor_id = E'private\\nmarker'" :
                "UPDATE placement_selections SET checked_application_record_id = '11111111-1111-4111-8111-111111111111'");
        foreach (var action in new Func<Task>[] {
            async () => await client.GetPlacementSelectionAsync(new() { Environment = "pilot", ProviderAlias = "cluster", Target = SelectionTarget("component") }),
            async () => await client.ApplyPlacementSelectionAsync(SelectionApply(SelectionTarget("component"), 1, original.ConcurrencyToken, "2")) })
        {
            var error = await Assert.ThrowsAsync<RpcException>(action); Assert.Equal(StatusCode.Internal, error.StatusCode); Assert.Equal("Placement selection operation failed.", error.Status.Detail);
        }
        await using var verify = ProfileContext(connection); var saved = await verify.PlacementSelections.AsNoTracking().SingleAsync();
        Assert.Equal(Guid.Parse(original.ConcurrencyToken), saved.Token); Assert.Equal(DateTimeOffset.Parse(original.ChangedAtUtc), saved.ChangedAtUtc);
        Assert.DoesNotContain("private\nmarker", logs.Text); await host.StopAsync();
    }

    private static async Task AssertPersistedPlacementBridge(WebApplication host)
    {
        var service = host.Services.GetRequiredService<PlacementSelectionAdministration>();
        var states = new List<PersistedPlacementSelection>(); var revisions = new List<AcceptedPlacementRevision>();
        foreach (var kind in new[] { "named", "environment", "application", "component" })
        {
            var t = SelectionTarget(kind); var state = (await service.GetAsync("pilot", "cluster", LogicalTarget.Create(t.Kind, t.Selector, t.ApplicationId, t.ComponentRef), default)).Selection!;
            states.Add(state);
        }
        foreach (var number in new[] { 3, 4 })
            await host.Services.GetRequiredService<PlacementAdministration>().ImportAsync("pilot", "cluster",
                Encoding.UTF8.GetBytes(PlacementSamples.Minimal.Replace("\"revision\":1", "\"revision\":" + number).Replace("\"namespace\":\"apps\"", "\"namespace\":\"apps\",\"replicas\":" + number)), "bridge-test", default);
        foreach (var state in states.ToArray())
        {
            var number = state.Target.Kind switch { "named" => 4, "component" => 3, "application" => 2, _ => 1 };
            var updated = await service.ApplyAsync("pilot", "cluster", state.Target, "standard-runtime", number, state.Target.Kind == "component" ? "2" : null,
                new PlacementSelectionPrecondition.ExpectedToken(state.Token), "bridge-test", default);
            Assert.Contains(updated.Outcome, new[] { PlacementSelectionOutcome.Updated, PlacementSelectionOutcome.Unchanged });
            states[states.IndexOf(state)] = updated.Selection!;
        }
        foreach (var revision in new[] { 1, 2, 3, 4 }) revisions.Add((await host.Services.GetRequiredService<PlacementAdministration>().GetAsync("pilot", "cluster", "standard-runtime", revision, default)).Revision!);
        var environment = states[0].Reference.Environment;
        NormalizedApplicationManifest Manifest(string component = "api", string selector = "", string alias = "cluster")
        {
            var body = "{\"applicationVersion\":\"2\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"assigned\"},\"spec\":{\"components\":{\"" + component + "\":{\"workloads\":{\"main\":{\"type\":\"kubernetes.workload/v1alpha1\",\"providerRef\":\"" + alias + "\"," + (selector == "" ? "" : "\"placementRef\":\"" + selector + "\",") + "\"properties\":{\"image\":{\"reference\":\"example.test/api:1\"}}}}}}}}}";
            return Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes(body), new ManifestContractCatalog([], [KubernetesContract.Definition], [], []))).Candidate.Declaration;
        }
        int Resolve(NormalizedApplicationManifest manifest, IEnumerable<PersistedPlacementSelection> selections)
        {
            var c = manifest.Spec.Components.Single(); var workload = c.Value.Workloads.Single();
            var exact = new ExactProviderResolutionSet(manifest, environment, [new(new ProviderInput.Workload(c.Key, workload.Key), environment, workload.Value.ProviderRef,
                ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), ProviderImplementationId.Create("kubernetes.test"))]);
            var inputs = PersistedPlacementInputs.Create(revisions, selections, exact);
            var result = Assert.Single(KubernetesWorkloadPlanner.Resolve(manifest, ApplicationVersion.Create("2"), exact, inputs));
            Assert.Equal("apps", result.Placement.Declaration.Namespace); Assert.Equal(result.Placement.Declaration.Reference.Revision >= 3 ? result.Placement.Declaration.Reference.Revision : 1, result.Settings.Replicas); return result.Placement.Declaration.Reference.Revision;
        }
        Assert.Equal(3, Resolve(Manifest(), states));
        Assert.Equal(2, Resolve(Manifest(), states.Where(x => x.Target.Kind != "component")));
        Assert.Equal(1, Resolve(Manifest(), states.Where(x => x.Target.Kind == "environment")));
        Assert.Equal(4, Resolve(Manifest(selector: "production"), states));
        Assert.Equal(2, Resolve(Manifest(component: "renamed"), states));
        Assert.Throws<WorkloadContractException>(() => Resolve(Manifest(selector: "missing"), states));
        Assert.Throws<WorkloadContractException>(() => Resolve(Manifest(alias: "other"), states));
        // Keep Host state assertions stable; bridge writes are metadata only and explicit CAS.
        foreach (var state in states)
            await service.ApplyAsync("pilot", "cluster", state.Target, "standard-runtime", 2, state.Target.Kind == "component" ? "2" : null, new PlacementSelectionPrecondition.ExpectedToken(state.Token), "bridge-test", default);
    }

    private static async Task PlacementSelectionNative(WebApplication host, List<PlacementSelectionState> states)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
        if (binary is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "haby-selection-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); var config = Path.Combine(folder, "config.yaml");
        try
        {
            await OperatorTestCredentials.WriteConfigAsync(config, host.Urls.Single());
            var nativeAlias = "native-" + Guid.NewGuid().ToString("N");
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            foreach (var number in new[] { 1, 2 })
                Assert.Equal(PlacementStatus.Created, (await client.ImportPlacementAsync(new()
                {
                    Environment = "pilot",
                    ProviderAlias = nativeAlias,
                    Payload = ByteString.CopyFromUtf8(PlacementSamples.Minimal.Replace("\"revision\":1", "\"revision\":" + number))
                })).Status);
            foreach (var kind in new[] { "named", "environment", "application", "component" })
            {
                List<string> scope = [kind, "--provider-alias", nativeAlias];
                if (kind == "named") scope.AddRange(["--selector", "production"]);
                if (kind is "application" or "component") scope.AddRange(["--application", "assigned"]);
                if (kind == "component") scope.AddRange(["--component", "api"]);
                List<string> apply = ["placement", "selection", "apply", .. scope, "--placement", "standard-runtime"];
                if (kind == "component") apply.AddRange(["--application-version", "2"]);
                var missing = await Invoke(["placement", "selection", "get", .. scope], "json"); Assert.Equal(1, missing.Exit); Assert.Contains("notFound", missing.Output);
                var created = await Invoke([.. apply, "--revision", "1", "--expected-absence"], "json"); Assert.Equal(0, created.Exit);
                using var first = JsonDocument.Parse(created.Output); Assert.Equal("created", first.RootElement.GetProperty("outcome"u8).GetString());
                var firstToken = first.RootElement.GetProperty("data"u8).GetProperty("concurrencyToken"u8).GetString()!;
                var changed = await Invoke([.. apply, "--revision", "2", "--expected-token", firstToken], "json"); Assert.Equal(0, changed.Exit);
                using var second = JsonDocument.Parse(changed.Output); Assert.Equal("updated", second.RootElement.GetProperty("outcome"u8).GetString());
                var secondToken = second.RootElement.GetProperty("data"u8).GetProperty("concurrencyToken"u8).GetString()!; Assert.NotEqual(firstToken, secondToken);
                var stale = await Invoke([.. apply, "--revision", "2", "--expected-token", firstToken], "json"); Assert.Equal(1, stale.Exit); Assert.Contains("conflict", stale.Output);
                var equal = await Invoke([.. apply, "--revision", "2", "--expected-token", secondToken], "json"); Assert.Equal(0, equal.Exit);
                using var unchanged = JsonDocument.Parse(equal.Output); Assert.Equal("unchanged", unchanged.RootElement.GetProperty("outcome"u8).GetString());
                Assert.True(JsonElement.DeepEquals(second.RootElement.GetProperty("data"u8), unchanged.RootElement.GetProperty("data"u8)));
                var noRevision = await Invoke([.. apply, "--revision", "3", "--expected-token", secondToken], "json"); Assert.Equal(1, noRevision.Exit); Assert.Contains("revisionNotFound", noRevision.Output);
                if (kind == "component")
                {
                    var invalid = apply.Select(x => x == "2" ? "9" : x).ToArray();
                    var missingComponent = await Invoke([.. invalid, "--revision", "2", "--expected-token", secondToken], "json"); Assert.Equal(1, missingComponent.Exit); Assert.Contains("componentNotFound", missingComponent.Output);
                }
                var found = await Invoke(["placement", "selection", "get", .. scope], "text"); Assert.Equal(0, found.Exit); Assert.Contains(secondToken, found.Output);
                var read = await client.GetPlacementSelectionAsync(new() { Environment = "pilot", ProviderAlias = nativeAlias, Target = SelectionTarget(kind) });
                Assert.Equal(PlacementSelectionStatus.Found, read.Status); Assert.Equal(secondToken, read.Selection.ConcurrencyToken); Assert.Equal(2, read.Selection.Reference.Revision);
            }
            foreach (var state in states)
                foreach (var format in new[] { "json", "text" })
                {
                    var target = state.Target;
                    var scope = new List<string> { target.Kind, "--provider-alias", "cluster" };
                    if (target.Selector != "") scope.AddRange(["--selector", target.Selector]);
                    if (target.ApplicationId != "") scope.AddRange(["--application", target.ApplicationId]);
                    if (target.ComponentRef != "") scope.AddRange(["--component", target.ComponentRef]);
                    var get = await Invoke(["placement", "selection", "get", .. scope], format);
                    Assert.Equal(0, get.Exit); Assert.Equal("", get.Error);
                    string current;
                    if (format == "json")
                    {
                        using var json = JsonDocument.Parse(get.Output); var data = json.RootElement.GetProperty("data"u8);
                        Assert.Equal("found", json.RootElement.GetProperty("outcome"u8).GetString());
                        current = data.GetProperty("concurrencyToken"u8).GetString()!; Assert.Equal(2, data.GetProperty("reference"u8).GetProperty("revision"u8).GetInt32());
                        Assert.Equal(target.Kind == "component", data.TryGetProperty("checkedRevision"u8, out _));
                    }
                    else { Assert.Contains("found", get.Output); Assert.Contains("concurrencyToken:", get.Output); current = state.ConcurrencyToken; }
                    List<string> apply = ["placement", "selection", "apply", .. scope, "--placement", "standard-runtime", "--revision", "2"];
                    if (target.Kind == "component") apply.AddRange(["--application-version", "2"]);
                    var unchanged = await Invoke([.. apply, "--expected-token", current], format);
                    // Native test reads fresh tokens, including metadata changed by the bridge test.
                    if (format == "json") { Assert.Equal(0, unchanged.Exit); Assert.Contains("unchanged", unchanged.Output); }
                    var conflict = await Invoke([.. apply, "--expected-absence"], format); Assert.Equal(1, conflict.Exit); Assert.Contains("conflict", conflict.Output);
                    Assert.Equal(2, (await Invoke([.. apply], format)).Exit);
                    Assert.Equal(2, (await Invoke([.. apply, "--expected-absence", "--expected-token", current], format)).Exit);
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
        finally { File.Delete(config); Directory.Delete(folder); }
    }
}
