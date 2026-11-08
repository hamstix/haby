using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Npgsql;
using static Haby.Tests.ProfilePersistenceSamples;
using Target = Haby.Operator.Contracts.ProfileAssignmentTarget;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    private static Target AssignmentTarget(string kind = "environment", string app = "", string component = "") => new() { Kind = kind, ApplicationId = app, ComponentRef = component };
    private static ByteString AssignmentPayload(Target target, string decisions = "[]") => ByteString.CopyFromUtf8(
        "{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"ConfigurationProfileAssignment\",\"spec\":{\"target\":" +
        (target.Kind == "environment" ? "{\"kind\":\"environment\"}" : target.Kind == "application" ?
            "{\"kind\":\"application\",\"applicationId\":\"" + target.ApplicationId + "\"}" :
            "{\"kind\":\"component\",\"applicationId\":\"" + target.ApplicationId + "\",\"componentRef\":\"" + target.ComponentRef + "\"}") + ",\"decisions\":" + decisions + "}}");
    private const string UseLayout = """[{"kind":"use","profileRef":{"profileId":"layout","revision":1}}]""";
    private static ApplyProfileAssignmentRequest AssignmentApply(Target target, string environment = "production", string? version = null, string decisions = "[]", string? token = null)
    {
        var request = new ApplyProfileAssignmentRequest { Environment = environment, Payload = AssignmentPayload(target, decisions) };
        if (version is not null) request.ApplicationVersion = version;
        if (token is null) request.ExpectedAbsence = true; else request.ExpectedConcurrencyToken = token;
        return request;
    }
    private static async Task<RevisionReference> ImportAssignmentApp(ManifestOperator.ManifestOperatorClient client, string version, params string[] components)
    {
        var node = JsonNode.Parse(Envelope("assigned"))!;
        node["applicationVersion"] = version;
        var declarations = new JsonObject();
        foreach (var name in components) declarations[name] = new JsonObject();
        node["manifest"]!["spec"]!["components"] = declarations;
        var reply = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(Bytes(node)) });
        return (reply.Created ?? reply.ExistingEquivalent)!.Revision.Reference;
    }

    [Fact]
    public async Task Assignment_generated_Host_exact_scopes_versions_unchanged_audit_restart_and_native_CLI()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        var logs = new ProfileLog();
        var commands = new ProfileQueryGate();
        var env = AssignmentTarget(); var application = AssignmentTarget("application", "assigned"); var component = AssignmentTarget("component", "assigned", "api");
        var states = new List<ProfileAssignmentState>();
        await using (var host = await ProfileHost(args, logs, commands))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = OperatorTestCredentials.Client(channel);
            var before = commands.Count;
            var initialSecretReads = commands.SecretReads;
            Assert.Equal(ProfileAssignmentStatus.Valid, (await client.ValidateProfileAssignmentAsync(new() { Payload = AssignmentPayload(component, UseLayout) })).Status);
            Assert.Equal(before, commands.Count);
            Assert.Equal(ProfileAssignmentStatus.EnvironmentNotFound, (await client.ApplyProfileAssignmentAsync(AssignmentApply(env))).Status);
            await client.CreateEnvironmentAsync(new() { Name = "production" });
            Assert.Equal(ProfileAssignmentStatus.NotFound, (await client.GetProfileAssignmentAsync(new() { Environment = "production", Target = env })).Status);
            // Environment defaults exist before any Application. Empty is durable state.
            states.Add((await client.ApplyProfileAssignmentAsync(AssignmentApply(env))).Assignment);
            Assert.Equal(ProfileAssignmentStatus.ApplicationNotFound, (await client.ApplyProfileAssignmentAsync(AssignmentApply(application))).Status);
            // Import a higher version first; api is in v2 only. Latest import and ordering cannot satisfy v1.
            var v2 = await ImportAssignmentApp(client, "2", "api", "worker");
            await ImportAssignmentApp(client, "1", "worker");
            states.Add((await client.ApplyProfileAssignmentAsync(AssignmentApply(application))).Assignment);
            foreach (var (version, expected) in new (string?, ProfileAssignmentStatus)[]
            {
                (null, ProfileAssignmentStatus.InvalidContext), ("", ProfileAssignmentStatus.InvalidContext),
                ("3", ProfileAssignmentStatus.ApplicationVersionNotFound), ("1", ProfileAssignmentStatus.ComponentNotFound)
            }) Assert.Equal(expected, (await client.ApplyProfileAssignmentAsync(AssignmentApply(component, version: version))).Status);
            Assert.Equal(ProfileAssignmentStatus.InvalidContext, (await client.ApplyProfileAssignmentAsync(AssignmentApply(env, version: "2"))).Status);
            Assert.Equal(ProfileAssignmentStatus.InvalidContext, (await client.ApplyProfileAssignmentAsync(AssignmentApply(application, version: "2"))).Status);
            Assert.Equal(ProfileAssignmentStatus.ProfileRevisionNotFound, (await client.ApplyProfileAssignmentAsync(AssignmentApply(component, version: "2", decisions: UseLayout))).Status);
            var profile = Node(); profile["spec"]!["documents"]!["settings"]!["template"] = new JsonObject { ["private"] = "sensitive-assignment-marker" };
            await client.ImportProfileAsync(Payload(profile));
            var missingRevision = AssignmentApply(component, version: "2", decisions: UseLayout.Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal));
            Assert.Equal(ProfileAssignmentStatus.ProfileRevisionNotFound, (await client.ApplyProfileAssignmentAsync(missingRevision)).Status);
            // Missing named secrets are accepted; the store/query graph has no SecretStore dependency.
            var accepted = await client.ApplyProfileAssignmentAsync(AssignmentApply(component, version: "2", decisions: UseLayout));
            Assert.Equal(ProfileAssignmentStatus.Created, accepted.Status);
            Assert.Equal(v2, accepted.Assignment.CheckedRevision);
            states.Add(accepted.Assignment);
            var v0 = await ImportAssignmentApp(client, "0", "api");
            var unchanged = await client.ApplyProfileAssignmentAsync(AssignmentApply(component, version: "0", decisions: UseLayout, token: accepted.Assignment.ConcurrencyToken));
            Assert.Equal(ProfileAssignmentStatus.Unchanged, unchanged.Status);
            Assert.Equal(accepted.Assignment, unchanged.Assignment);
            Assert.NotEqual(v0, unchanged.Assignment.CheckedRevision);
            // Scope validation applies even to an otherwise matching unchanged request.
            Assert.Equal(ProfileAssignmentStatus.ComponentNotFound, (await client.ApplyProfileAssignmentAsync(AssignmentApply(component, version: "1", decisions: UseLayout, token: accepted.Assignment.ConcurrencyToken))).Status);
            Assert.Equal(ProfileAssignmentStatus.Conflict, (await client.ApplyProfileAssignmentAsync(AssignmentApply(component, version: "2", decisions: UseLayout))).Status);
            Assert.Equal(ProfileAssignmentStatus.Conflict, (await client.ApplyProfileAssignmentAsync(AssignmentApply(component, version: "2", decisions: UseLayout, token: Guid.NewGuid().ToString()))).Status);
            var malformed = AssignmentApply(component, version: "2"); malformed.ExpectedConcurrencyToken = "not-a-token";
            Assert.Equal(ProfileAssignmentStatus.InvalidContext, (await client.ApplyProfileAssignmentAsync(malformed)).Status);
            malformed.ClearPrecondition();
            Assert.Equal(ProfileAssignmentStatus.InvalidContext, (await client.ApplyProfileAssignmentAsync(malformed)).Status);
            malformed.ExpectedAbsence = false;
            Assert.Equal(ProfileAssignmentStatus.InvalidContext, (await client.ApplyProfileAssignmentAsync(malformed)).Status);
            Assert.Equal(ProfileAssignmentStatus.InvalidContext, (await client.GetProfileAssignmentAsync(new() { Environment = "production", Target = AssignmentTarget("environment", "foreign") })).Status);
            await ImportAssignmentApp(client, "9", "renamed");
            var query = host.Services.GetRequiredService<IConfigurationProfileRevisionQuery>();
            var loaded = await query.FindAsync(new(ProfileId.Create("layout"), 1));
            var assignment = (await host.Services.GetRequiredService<ProfileAssignmentAdministration>().GetAsync("production",
                new Haby.Domain.ProfileAssignmentTarget.Component(Haby.Domain.ApplicationId.Create("assigned"), ManifestLocalId.Create("api")))).Assignment!;
            var exact = await host.Services.GetRequiredService<IProfileAssignmentScopeQuery>().FindVersionAsync(Haby.Domain.ApplicationId.Create("assigned"), ApplicationVersion.Create("2"), default);
            var expansion = new ConfigurationProfileExpander(new([loaded!.Declaration])).Expand([], [],
                [new(ManifestLocalId.Create("api"), exact!.Declaration.Spec.Components[ManifestLocalId.Create("api")], assignment.Declaration.Decisions)]);
            Assert.False(expansion.IsSuccess); Assert.Null(expansion.Layouts);
            Assert.Contains(expansion.Diagnostics, x => x.Code == "profile.secret-projections.unsupported");
            // Exact read remains available despite latest revision lacking the Component.
            Assert.Equal(accepted.Assignment, (await client.GetProfileAssignmentAsync(new() { Environment = "production", Target = component })).Assignment);
            Assert.Equal(ProfileAssignmentStatus.NotFound, (await client.GetProfileAssignmentAsync(new() { Environment = "production", Target = AssignmentTarget("component", "assigned", "renamed") })).Status);
            Assert.Equal(initialSecretReads, commands.SecretReads);
            await AssignmentNative(host);
            await host.StopAsync();
        }
        await using (var host = await ProfileHost(args, logs))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            foreach (var state in states) Assert.Equal(state, (await client.GetProfileAssignmentAsync(new() { Environment = "production", Target = state.Target })).Assignment);
            await host.StopAsync();
        }
        await AssignmentRestore(connection, states);
        foreach (var marker in new[] { "sensitive-assignment-marker", "future-token", "future-otel", "Npgsql.PostgresException" }) Assert.DoesNotContain(marker, logs.Text);
        await using var check = ProfileContext(connection);
        Assert.Empty(await check.Secrets.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.SecretPayloads.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.ProviderInstances.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.DurablePlans.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.DurableOperations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Assignment_corrupt_unavailable_references_and_state_fail_safely_without_repair_or_false_absence()
    {
        var connection = await NewDatabaseAsync(); var logs = new ProfileLog();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        await using var host = await ProfileHost(args, logs);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        await client.CreateEnvironmentAsync(new() { Name = "production" });
        await client.ImportProfileAsync(Payload(Node()));
        var target = AssignmentTarget();
        var created = await client.ApplyProfileAssignmentAsync(AssignmentApply(target, decisions: UseLayout));
        var token = created.Assignment.ConcurrencyToken;
        await using (var arrange = ProfileContext(connection))
        { var row = await arrange.ProfileRevisions.SingleAsync(); row.Snapshot = "private-corrupt-profile"; await arrange.SaveChangesAsync(); }
        await SafeFailure(async () => await client.ApplyProfileAssignmentAsync(AssignmentApply(target, decisions: UseLayout, token: token)));
        await using (var arrange = ProfileContext(connection))
        {
            var row = await arrange.ProfileRevisions.SingleAsync();
            row.Snapshot = Haby.Application.ProfileSnapshotCodec.Encode(Parse(Node()));
            row.SnapshotFormatVersion++;
            await arrange.SaveChangesAsync();
        }
        await SafeFailure(async () => await client.ApplyProfileAssignmentAsync(AssignmentApply(target, decisions: UseLayout, token: token)));
        await using (var arrange = ProfileContext(connection))
            await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE configuration_profile_revisions RENAME TO unavailable_assignment_profiles");
        await SafeFailure(async () => await client.ApplyProfileAssignmentAsync(AssignmentApply(target, decisions: UseLayout, token: token)));
        await using (var arrange = ProfileContext(connection))
            await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE unavailable_assignment_profiles RENAME TO configuration_profile_revisions");
        await using (var arrange = ProfileContext(connection))
        {
            Assert.Equal(token, (await arrange.ProfileAssignments.AsNoTracking().SingleAsync()).Token.ToString());
            var row = await arrange.ProfileAssignments.SingleAsync(); row.Snapshot = "private-corrupt-assignment"; await arrange.SaveChangesAsync();
        }
        await SafeFailure(async () => await client.GetProfileAssignmentAsync(new() { Environment = "production", Target = target }));
        await SafeFailure(async () => await client.ApplyProfileAssignmentAsync(AssignmentApply(target, token: token)));
        await using (var arrange = ProfileContext(connection))
            await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE configuration_profile_assignments RENAME TO unavailable_assignments");
        await SafeFailure(async () => await client.GetProfileAssignmentAsync(new() { Environment = "production", Target = target }));
        await using (var arrange = ProfileContext(connection))
            await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE unavailable_assignments RENAME TO configuration_profile_assignments");
        await using (var check = ProfileContext(connection))
        { Assert.Equal("private-corrupt-assignment", (await check.ProfileAssignments.AsNoTracking().SingleAsync()).Snapshot); }
        await host.StopAsync();
        Assert.DoesNotContain("private-corrupt", logs.Text); Assert.DoesNotContain("unavailable_assignments", logs.Text); Assert.DoesNotContain("Npgsql.PostgresException", logs.Text);
        static async Task SafeFailure(Func<Task> action)
        {
            var error = await Assert.ThrowsAsync<RpcException>(action);
            Assert.Equal(StatusCode.Internal, error.StatusCode); Assert.Equal("Profile operation failed.", error.Status.Detail);
        }
    }


    private async Task AssignmentRestore(string source, List<ProfileAssignmentState> states)
    {
        var restored = await NewDatabaseAsync();
        var directory = "/tmp/haby-assignment-restore-" + Guid.NewGuid().ToString("N");
        var dump = directory + "/catalog.dump";
        Exception? failure = null;
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(source).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await using var host = await ProfileHost(["--Haby:PostgreSql:ConnectionString=" + restored, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], new ProfileLog());
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            foreach (var expected in states)
                Assert.Equal(expected, (await client.GetProfileAssignmentAsync(new() { Environment = expected.Environment, Target = expected.Target })).Assignment);
            await host.StopAsync();
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
            catch when (failure is not null) { /* Preserve the original failure while attempting both cleanup steps. */ }
        }
    }

    [Fact]
    public async Task Assignment_transport_cancellation_reaches_database_without_mutation()
    {
        var connection = await NewDatabaseAsync(); var gate = new ProfileQueryGate { WatchedTable = "configuration_profile_assignments" };
        await using var host = await ProfileHost(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], new ProfileLog(), gate);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        await client.CreateEnvironmentAsync(new() { Name = "production" });
        foreach (var apply in new[] { false, true })
        {
            gate.Arm(); using var cancellation = new CancellationTokenSource();
            using var call = apply ? client.ApplyProfileAssignmentAsync(AssignmentApply(AssignmentTarget()), cancellationToken: cancellation.Token) :
                client.GetProfileAssignmentAsync(new() { Environment = "production", Target = AssignmentTarget() }, cancellationToken: cancellation.Token);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); cancellation.Cancel();
            Assert.Equal(StatusCode.Cancelled, (await Assert.ThrowsAsync<RpcException>(async () => await call)).StatusCode);
            await gate.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        await using var check = ProfileContext(connection);
        Assert.Empty(await check.ProfileAssignments.AsNoTracking().ToArrayAsync());
        await host.StopAsync();
    }

    private static async Task AssignmentNative(WebApplication host)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
        if (binary is null) return;
        Assert.True(File.Exists(binary));
        var folder = Path.Combine(Path.GetTempPath(), "haby-assignment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var config = Path.Combine(folder, "config.yaml");
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: wrong
                instances:
                  local:
                    endpoint: {host.Urls.Single()}
                    token: {OperatorTestCredentials.Credential(host.Urls.Single())}
                contexts:
                  wrong:
                    instance: local
                    environmentId: missing
                  selected:
                    instance: local
                    environmentId: production
                """);
            foreach (var target in new[] { AssignmentTarget(), AssignmentTarget("application", "assigned"), AssignmentTarget("component", "assigned", "worker") })
            {
                var file = Path.Combine(folder, "assignment.json"); await File.WriteAllBytesAsync(file, AssignmentPayload(target).ToByteArray());
                var scope = target.Kind == "environment" ? Array.Empty<string>() : target.Kind == "application" ? new[] { "--application", "assigned" } : new[] { "--application", "assigned", "--component", "worker" };
                var version = target.Kind == "component" ? new[] { "--application-version", "2" } : Array.Empty<string>();
                foreach (var format in new[] { "text", "json" })
                {
                    await Run(["validate", file], "valid", 0);
                    await Run(["apply", file, .. version], target.Kind == "component" && format == "text" ? "created" : "unchanged", 0);
                    await Run(["get", .. scope], "found", 0);
                    await Run(["apply", file, .. version, "--if-absent"], "conflict", 1);
                    await Run(["apply", file, .. version, "--if-match", Guid.NewGuid().ToString()], "conflict", 1);
                    await Run(["apply", file, .. version, "--environment", "absent"], "environmentNotFound", 1);
                    if (target.Kind == "component") await Run(["apply", file], "invalidContext", 1);
                    await File.WriteAllBytesAsync(file, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(AssignmentPayload(target).ToByteArray()).ToArray());
                    await Run(["validate", file], "invalidDeclaration", 1);
                    await File.WriteAllBytesAsync(file, AssignmentPayload(target).ToByteArray());
                    async Task Run(string[] command, string outcome, int exit)
                    {
                        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                        foreach (var arg in new[] { "profile", "assignment" }.Concat(command).Concat(["--config", config, "--context", "selected", "-o", format])) start.ArgumentList.Add(arg);
                        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                        var text = await stdout;
                        Assert.Equal("", await stderr); Assert.Equal(exit, process.ExitCode); Assert.Contains(outcome, text);
                        foreach (var marker in new[] { "sensitive-", "future-token", "future-otel", "snapshot", "template" }) Assert.DoesNotContain(marker, text);
                        if (format == "json")
                        {
                            using var json = JsonDocument.Parse(text);
                            Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                        }
                    }
                }
            }
        }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }
}
