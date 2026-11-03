using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    // The successful rehearsal never replaces production services. The other cases only remove
    // capabilities or inject the existing internal post-commit fault around real access SQL.
    [Theory]
    [InlineData("success")]
    [InlineData("uncertain")]
    [InlineData("missing-handler")]
    [InlineData("missing-output")]
    public async Task Five_step_operator_path_uses_real_production_Host_and_published_native_client(string mode)
    {
        var connection = await NewDatabaseAsync();
        var target = "operator_pair_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        using var certificate = Certificate();
        var folder = Path.Combine(Path.GetTempPath(), "haby-five-step-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var certificatePath = Path.Combine(folder, "host.pfx");
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-only"));
        using var logs = new ExecutionLogs();
        var fault = new OperatorAccessFault();
        var native = new OperatorNativeClient(folder, admin.Password!);
        DurablePlanSummary plan;
        DurableCommitSummary commit;
        OperationReply[] history = [];
        string role;
        SecretRef? password = null;
        try
        {
            // Ordinary production configuration, with capture only; no admission/executor overrides.
            await using (var host = await StartExecutionLoggedHost(connection, certificatePath, logs))
            {
                native.Endpoint = host.Urls.Single();
                using var channel = Channel(host, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                if (native.Available) await native.Run(["environment", "create", "pilot"]);
                var environmentResult = await client.GetEnvironmentAsync(new() { Name = "pilot" });
                if (!native.Available) Assert.Equal(EnvironmentStatus.Created, (await client.CreateEnvironmentAsync(new() { Name = "pilot" })).Status);
                else Assert.Equal(EnvironmentStatus.Found, environmentResult.Status);
                var secretRequest = new CreateSecretRequest { Environment = "pilot", Name = "admin", RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8(admin.Password!) };
                if (native.Available)
                    await native.Run(["secret", "create", "admin", "--stdin", "--request-id", secretRequest.RequestId], input: admin.Password);
                else Assert.Equal(SecretStatus.Created, (await client.CreateSecretAsync(secretRequest)).Status);
                var declaration = $$$$$"""
                    {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"pg-main","environment":"pilot"},
                    "spec":{"revision":1,"providerKind":"postgresql/v1","configurationContract":"haby.postgresql.connection/v1",
                    "implementationId":"haby.postgresql.database/v1alpha1","properties":{"host":"{{{{{admin.Host}}}}}","port":{{{{{admin.Port}}}}},
                    "maintenanceDatabase":"postgres","administratorUserName":"{{{{{admin.Username}}}}}"},
                    "secretRefs":{"administrator-password":{"secretName":"admin","version":1}}}}
                    """;
                var providerFile = Path.Combine(folder, "provider.json");
                await File.WriteAllTextAsync(providerFile, declaration);
                if (native.Available)
                {
                    await native.Run(["provider", "apply", "--file", providerFile]);
                    await native.Run(["provider", "check", "pg-main", "--revision", "1"]);
                    await native.Run(["provider", "alias", "set", "postgresql.primary", "--provider", "pg-main", "--revision", "1", "--expect-absent"]);
                }
                else
                {
                    Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(declaration) })).Status);
                    Assert.Equal(ProviderAccessStatus.Ready, (await client.CheckProviderAsync(new() { Environment = "pilot", Name = "pg-main", Revision = 1 })).Status);
                    Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "postgresql.primary", Target = new() { Name = "pg-main", Revision = 1 }, ExpectedAbsent = true })).Status);
                }
                var envelope = PostgreSqlPairPlanningTests.Example();
                envelope["manifest"]!["metadata"]!["id"] = "operator.pair";
                envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = target;
                var bytes = PostgreSqlPairPlanningTests.Bytes(envelope);
                var manifestFile = Path.Combine(folder, "application.json");
                await File.WriteAllBytesAsync(manifestFile, bytes);
                if (native.Available)
                {
                    await native.Run(["manifest", "validate", manifestFile]);
                    await native.Run(["manifest", "import", manifestFile]);
                }
                var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(bytes) });
                var revision = imported.Created?.Revision.Reference ?? imported.ExistingEquivalent.Revision.Reference;
                var environment = (await client.GetEnvironmentAsync(new() { Name = "pilot" })).Environment;
                role = PostgreSqlPairResourceAdmission.RoleName(new(EnvironmentRecordId.Parse(environment.RecordId), ApplicationRecordId.Parse(revision.ApplicationRecordId), ManifestLocalId.Create("app-login")));
                if (native.Available)
                {
                    var changeJson = await native.Run(["changeset", "create", revision.ApplicationRecordId, revision.RevisionNumber.ToString(), "--expected-absence"]);
                    var changeId = changeJson.GetProperty("data"u8).GetProperty("changeSet"u8).GetProperty("changeSetId"u8).GetString()!;
                    var planned = await native.Run(["plan", "create", changeId]);
                    var planId = planned.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("planId"u8).GetString()!;
                    plan = (await client.GetPlanAsync(new() { PlanId = planId })).Plan;
                    AssertNativeGraph(planned.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("operations"u8), plan);
                    await native.Run(["plan", "get", planId], format: "text");
                    foreach (var node in plan.Operations)
                        Assert.Contains($"Operation: {node.OperationId}; Resource: {node.ResourceId}; action: {node.ActionContract}; dependencies: {string.Join(", ", node.Dependencies)}", native.LastText);
                    var checkedPlan = await native.Run(["plan", "check", planId], outcome: "current");
                    AssertNativeGraph(checkedPlan.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("operations"u8), plan);
                    var accepted = await native.Run(["changeset", "commit", changeId, "--plan", planId]);
                    AssertNativeGraph(accepted.GetProperty("data"u8).GetProperty("desiredGeneration"u8).GetProperty("operations"u8), plan);
                    commit = (await client.GetCommitAsync(new() { ChangeSetId = changeId })).Commit;
                }
                else
                {
                    var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedAbsence });
                    plan = (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Plan;
                    commit = (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Commit;
                }
                Assert.Equal(5, plan.Operations.Count);
                Assert.Equal(plan.Operations, (await client.GetGenerationAsync(new() { DeploymentId = commit.DeploymentId, Generation = 1 })).DesiredGeneration.Operations);
                Assert.Null(plan.IntendedWork.Action); // The single-action detail is not a pair description.
                Assert.Equal(Haby.Operator.Contracts.PlanDescriptionStatus.UnsupportedShape, plan.IntendedWork.Status);
                await AssertPairCatalog(connection, 0, 0, 0, 0, applied: false);
                await AssertPairExternalAbsent(target, role);
                foreach (var node in plan.Operations.Where(n => n.Dependencies.Count > 0))
                {
                    var request = PairRequest(commit, node.OperationId);
                    Assert.Equal(OperationStatus.Blocked, (await client.ExecuteOperationAsync(request)).Status);
                    Assert.Empty((await client.GetOperationAsync(request)).Attempts);
                    if (native.Available) await native.Run(OperationArgs("execute", request), expectedExit: 1, outcome: "blocked");
                }
                await AssertPairExternalAbsent(target, role);
                // Move the mutable alias after acceptance. All five nodes must retain revision 1.
                await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(declaration.Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal)) });
                Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "postgresql.primary", Target = new() { Name = "pg-main", Revision = 2 }, Expected = new() { Name = "pg-main", Revision = 1 } })).Status);
                if (mode == "success")
                    await ExecutePair(client, native, connection, plan, commit);
                if (mode == "success") history = await PairHistory(client, plan, commit);
                await host.StopAsync();
            }
            if (mode != "success")
            {
                var host = OperatorHostApplication.Build(Arguments(connection, certificatePath), services =>
                {
                    services.AddLogging(b => b.AddProvider(logs));
                    if (mode == "uncertain") services.AddSingleton<IPostgreSqlAccessFaultHooks>(fault);
                    if (mode == "missing-handler")
                        RemoveRegistration<IDurableActionHandler>(services, typeof(PostgreSqlApplicationAccessAction));
                    if (mode == "missing-output")
                        RemoveRegistration<IExternalOperationOutputContract>(services, typeof(PostgreSqlApplicationConnectionContract));
                });
                await using (host)
                {
                    await host.StartAsync();
                    native.Endpoint = host.Urls.Single();
                    using var channel = Channel(host, certificate);
                    var client = new ManifestOperator.ManifestOperatorClient(channel);
                    if (mode == "uncertain") await ExecutePair(client, native, connection, plan, commit, uncertain: true);
                    else
                    {
                        var first = PairRequest(commit, Ordered(plan).First().OperationId);
                        Assert.Equal(OperationStatus.HandlerUnavailable, (await client.ExecuteOperationAsync(first)).Status);
                        if (native.Available) await native.Run(OperationArgs("execute", first), expectedExit: 2, outcome: "handlerUnavailable");
                        await AssertPairCatalog(connection, 0, 0, 0, 0, applied: false);
                        await AssertPairExternalAbsent(target, role);
                        return;
                    }
                    history = await PairHistory(client, plan, commit);
                    await host.StopAsync();
                }
            }
            var originalIdentities = await PairIdentities(connection);
            // Trusted test-only internal resolution. There is no production material/reveal endpoint.
            await using (var verify = PairContext(connection))
            {
                var slot = await verify.GeneratedCredentialSlots.AsNoTracking().SingleAsync();
                password = new(SecretId.Parse(slot.SecretId.ToString()), SecretVersionId.Parse(slot.VersionId.ToString()));
            }
            await using (var restarted = await StartExecutionLoggedHost(connection, certificatePath, logs))
            {
                native.Endpoint = restarted.Urls.Single();
                using var channel = Channel(restarted, certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                var owner = new ManagedResourceKey(EnvironmentRecordId.Parse(plan.EnvironmentRecordId), ApplicationRecordId.Parse(plan.Reference.ApplicationRecordId), ManifestLocalId.Create("app-login"));
                using var material = await restarted.Services.GetRequiredService<ISecretStore>().ResolveAsync(new(new SecretOwner.Resource(owner), SecretPurpose.ApplicationLogin), password, default);
                Assert.NotNull(material);
                var saved = material.CopyBytes();
                try
                {
                    native.ForbiddenMaterial = Encoding.ASCII.GetString(saved);
                    if (mode == "success")
                    {
                        await using var context = PairContext(connection);
                        var row = await context.OperationOutputs.AsNoTracking().SingleAsync(o => o.OperationId == Ordered(plan).Last().OperationId);
                        var output = PostgreSqlApplicationConnectionContract.Decode(new(row.Contract, row.Key, row.Payload))!;
                        Assert.Equal(password, output.Password);
                        Assert.Equal(role, output.UserName);
                        Assert.Equal(target, output.DatabaseName);
                        var application = new NpgsqlConnectionStringBuilder { Host = output.Host, Port = output.Port, Database = output.DatabaseName, Username = output.UserName, Password = Encoding.ASCII.GetString(saved), Pooling = false };
                        try { await OwnerMigrationEvidence.RunAsync(application.ConnectionString); }
                        finally { application.Password = null; }
                        await AssertPairOwnership(target, role);
                    }
                    Assert.Equal(history, await PairHistory(client, plan, commit));
                    foreach (var original in history)
                    {
                        Assert.Equal(1, original.Operation.ConfigurationRevision);
                        var expected = original.Operation.State == OperationState.Succeeded ? OperationStatus.Succeeded : OperationStatus.NeedsAttention;
                        Assert.Equal(expected, (await client.ExecuteOperationAsync(original.Operation.Key)).Status);
                        if (native.Available)
                        {
                            await native.Run(OperationArgs("get", original.Operation.Key));
                            await native.Run(OperationArgs("get", original.Operation.Key), format: "text");
                            await native.Run(OperationArgs("execute", original.Operation.Key), expectedExit: expected == OperationStatus.Succeeded ? 0 : 1,
                                outcome: expected == OperationStatus.Succeeded ? "succeeded" : "needsAttention");
                        }
                        Assert.Equal(original, await client.GetOperationAsync(original.Operation.Key));
                    }
                    Assert.False(logs.Text.Contains(native.ForbiddenMaterial, StringComparison.Ordinal));
                    Assert.False(native.Transcript.Contains(native.ForbiddenMaterial, StringComparison.Ordinal));
                }
                finally { Array.Clear(saved); }
            }
            await AssertPairCatalog(connection, 5, mode == "success" ? 4 : 3, 1, 1, applied: mode == "success");
            Assert.True(originalIdentities == await PairIdentities(connection), "Successful/uncertain repeats changed retained identities or outputs.");
            Assert.Equal(mode == "uncertain" ? 1 : 0, fault.Commits);
            Assert.False((native.Transcript + logs.Text).Contains(admin.Password!, StringComparison.Ordinal));
            Assert.DoesNotContain("SCRAM-SHA-256$", native.Transcript + logs.Text);
            Assert.DoesNotContain("Npgsql.PostgresException", native.Transcript + logs.Text);
        }
        finally
        {
            // Only files this test created; external resources belong to its disposable fixture.
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private static void RemoveRegistration<T>(IServiceCollection services, Type implementation)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(T) && d.ImplementationType == implementation);
        services.Remove(descriptor);
    }

    private static ManifestCatalogDbContext PairContext(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static Task<OperationReply[]> PairHistory(ManifestOperator.ManifestOperatorClient client, DurablePlanSummary plan, DurableCommitSummary commit) =>
        Task.WhenAll(Ordered(plan).Select(n => client.GetOperationAsync(PairRequest(commit, n.OperationId)).ResponseAsync));

    private static void AssertNativeGraph(JsonElement nodes, DurablePlanSummary plan)
    {
        Assert.Equal(plan.Operations.Count, nodes.GetArrayLength());
        foreach (var node in nodes.EnumerateArray())
        {
            var expected = Assert.Single(plan.Operations, n => n.OperationId == node.GetProperty("operationId"u8).GetString());
            Assert.Equal(expected.ResourceId, node.GetProperty("resourceId"u8).GetString());
            Assert.Equal(expected.ActionContract, node.GetProperty("actionContract"u8).GetString());
            Assert.Equal(expected.Dependencies, node.GetProperty("dependencies"u8).EnumerateArray().Select(d => d.GetString()));
        }
    }
    private static OperationRequest PairRequest(DurableCommitSummary commit, string id) => new() { DeploymentId = commit.DeploymentId, Generation = commit.Generation, OperationId = id };
    private static string[] OperationArgs(string verb, OperationRequest request) => ["operation", verb, request.DeploymentId, request.Generation.ToString(), request.OperationId];
    private static PlannedOperationSummary[] Ordered(DurablePlanSummary plan)
    {
        var remaining = plan.Operations.ToList();
        var result = new List<PlannedOperationSummary>();
        while (remaining.Count > 0)
        {
            var next = Assert.Single(remaining, n => n.Dependencies.All(d => result.Any(done => done.OperationId == d)));
            result.Add(next);
            remaining.Remove(next);
        }
        return result.ToArray();
    }

    private static async Task ExecutePair(ManifestOperator.ManifestOperatorClient client, OperatorNativeClient native,
        string connection, DurablePlanSummary plan, DurableCommitSummary commit, bool uncertain = false)
    {
        var ordered = Ordered(plan);
        for (var index = 0; index < ordered.Length; index++)
        {
            var request = PairRequest(commit, ordered[index].OperationId);
            var expected = uncertain && index == 4 ? OperationStatus.NeedsAttention : OperationStatus.Succeeded;
            if (native.Available) await native.Run(OperationArgs("execute", request), expectedExit: expected == OperationStatus.Succeeded ? 0 : 1,
                outcome: expected == OperationStatus.Succeeded ? "succeeded" : "needsAttention");
            else Assert.Equal(expected, (await client.ExecuteOperationAsync(request)).Status);
            var view = await client.GetOperationAsync(request);
            Assert.Single(view.Attempts);
            Assert.Equal(expected == OperationStatus.Succeeded ? OperationState.Succeeded : OperationState.NeedsAttention, view.Operation.State);
            await using var verify = PairContext(connection);
            Assert.Equal(index == 4 && !uncertain ? 1L : null, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
    }

    private static async Task<string> PairIdentities(string connection)
    {
        await using var context = PairContext(connection);
        var outputs = await context.OperationOutputs.AsNoTracking().OrderBy(r => r.OperationId).Select(r => r.Payload).ToArrayAsync();
        var slots = await context.GeneratedCredentialSlots.AsNoTracking().Select(r => new { r.SecretId, r.VersionId }).ToArrayAsync();
        var names = await context.ProviderResourceReservations.AsNoTracking().Select(r => r.Value).ToArrayAsync();
        return string.Join('|', outputs.Concat(slots.Select(r => r.SecretId + ":" + r.VersionId)).Concat(names));
    }

    private static async Task AssertPairCatalog(string connection, int attempts, int outputs, int slots, int reservations, bool applied)
    {
        await using var verify = PairContext(connection);
        Assert.Equal(attempts, await verify.DurableOperationAttempts.CountAsync());
        Assert.Equal(outputs, await verify.OperationOutputs.CountAsync());
        Assert.Equal(slots, await verify.GeneratedCredentialSlots.CountAsync());
        Assert.Equal(slots, await verify.GeneratedCredentialCreationAudits.CountAsync());
        Assert.Equal(reservations, await verify.ProviderResourceReservations.CountAsync());
        Assert.Equal(applied ? 1L : null, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    private async Task AssertPairExternalAbsent(string database, string role)
    {
        await using var external = new NpgsqlConnection(fixture.ConnectionString);
        await external.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM pg_database WHERE datname = $1) + (SELECT count(*) FROM pg_roles WHERE rolname = $2)", external);
        command.Parameters.AddWithValue(database);
        command.Parameters.AddWithValue(role);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private async Task AssertPairOwnership(string database, string role)
    {
        await using var external = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString);
        await external.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT d.datdba = r.oid AND n.nspowner = r.oid AND p.nspowner = 'pg_database_owner'::regrole
              AND r.rolcanlogin AND NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
              AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member = r.oid)
              AND NOT EXISTS (SELECT 1 FROM aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) a WHERE a.grantee = 0)
              AND NOT EXISTS (SELECT 1 FROM aclexplode(COALESCE(p.nspacl, acldefault('n', p.nspowner))) a WHERE a.grantee = 0)
              AND NOT EXISTS (SELECT 1 FROM aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) a WHERE a.grantee = 0)
              AND EXISTS (SELECT 1 FROM pg_db_role_setting s WHERE s.setdatabase = d.oid AND s.setrole = r.oid AND 'search_path=app' = ANY(s.setconfig))
              AND (SELECT count(*) FROM pg_default_acl a WHERE a.defaclrole = r.oid AND a.defaclnamespace = 0) = 2
            FROM pg_database d CROSS JOIN pg_roles r CROSS JOIN pg_namespace n CROSS JOIN pg_namespace p
            WHERE d.datname = current_database() AND r.rolname = $1 AND n.nspname = 'app' AND p.nspname = 'public'
            """, external);
        command.Parameters.AddWithValue(role);
        Assert.True(await command.ExecuteScalarAsync() is true);
    }

    private sealed class OperatorAccessFault : IPostgreSqlAccessFaultHooks
    {
        public int Commits;
        public ValueTask AfterMarkerAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask AfterOwnershipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask AfterSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask BeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask AfterCommitAsync(CancellationToken token) { Commits++; throw new IOException("Injected interruption."); }
        public ValueTask BeforeVerificationAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask BeforeAuthenticationAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask BeforeFinalizationAsync(CancellationToken token) => ValueTask.CompletedTask;
    }

    private sealed class OperatorNativeClient(string folder, string administrativePassword)
    {
        private readonly string? binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        public bool Available
        {
            get { Assert.False(Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1" && string.IsNullOrEmpty(binary)); return !string.IsNullOrEmpty(binary); }
        }
        public string Endpoint = "";
        public string? ForbiddenMaterial;
        public string Transcript = "";
        public string LastText = "";
        public async Task<JsonElement> Run(string[] args, int expectedExit = 0, string? input = null, string format = "json", string? outcome = null)
        {
            var config = Path.Combine(folder, "config.yaml");
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: pilot
                instances:
                  local:
                    endpoint: {Endpoint}
                contexts:
                  pilot:
                    instance: local
                    environmentId: pilot
                """);
            var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args.Concat(new[] { "--config", config, "-o", format })) start.ArgumentList.Add(arg);
            start.Environment["HABY_OPERATOR_SKIP_TLS_VALIDATION"] = "true";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            var text = await stdout;
            var errors = await stderr;
            Assert.False((text + errors).Contains(administrativePassword, StringComparison.Ordinal));
            Assert.False(ForbiddenMaterial is not null && (text + errors).Contains(ForbiddenMaterial, StringComparison.Ordinal));
            Assert.DoesNotContain("SCRAM-SHA-256$", text + errors);
            Assert.True(process.ExitCode == expectedExit, $"{string.Join(' ', args.Take(2))} exit {process.ExitCode}, expected {expectedExit}; safe stderr length {errors.Length}.");
            Assert.Equal("", errors);
            LastText = text;
            Transcript += text;
            if (format == "text") return default;
            using var json = JsonDocument.Parse(text); // Exactly one object; never a text fallback.
            Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
            if (outcome is not null) Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
            return json.RootElement.Clone();
        }
    }
}
