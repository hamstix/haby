using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resource_reuse_commit_revalidates_significant_continuity_and_missing_evidence_atomically(bool continuityChange)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var revision = await ImportUpdate(client, UpdateEnvelope(scenario, 2)); var plan = await UpdatePlan(client, revision, 1);
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + (continuityChange
            ? "UPDATE resource_management SET id = '00000000-0000-0000-0000-000000000158' WHERE resource_local_id = 'app-login'"
            : "DELETE FROM operation_outputs WHERE operation_id = 'app-login-access'"));
        var history = await ReuseHistory(scenario.Connection);
        var result = await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId });
        Assert.Equal(continuityChange ? CommitStatus.PlanStale : CommitStatus.PlanInputUnavailable, result.Status);
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using var verify = Context(scenario.Connection); Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
        Assert.Equal(1, await verify.DeploymentGenerations.CountAsync()); await host.StopAsync();
    }

    [Theory]
    [InlineData("database")]
    [InlineData("schema")]
    [InlineData("resource-add")]
    [InlineData("resource-rename")]
    [InlineData("resource-remove")]
    [InlineData("component-add")]
    [InlineData("component-remove")]
    [InlineData("binding-add")]
    public async Task Resource_reuse_unsupported_declaration_changes_refuse_without_effects(string mutation)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var history = await ReuseHistory(scenario.Connection);
        var envelope = UpdateEnvelope(scenario, 2);
        var spec = envelope["manifest"]!["spec"]!;
        switch (mutation)
        {
            case "database": spec["resources"]!["db"]!["properties"]!["name"] = "unsupported_replacement"; break;
            case "schema": spec["resources"]!["app-login"]!["properties"]!["schema"] = "other_schema"; break;
            case "resource-add": spec["resources"]!["other"] = spec["resources"]!["db"]!.DeepClone(); break;
            case "resource-rename":
                spec["resources"]!["renamed"] = spec["resources"]!["db"]!.DeepClone();
                spec["resources"]!.AsObject().Remove("db");
                spec["resources"]!["app-login"]!["properties"]!["databaseRef"] = "renamed"; break;
            case "resource-remove": spec["resources"]!.AsObject().Remove("app-login"); spec["components"]!.AsObject().Clear(); break;
            case "component-add": spec["components"]!["extra"] = spec["components"]!["worker"]!.DeepClone(); break;
            case "component-remove": spec["components"]!.AsObject().Remove("worker"); break;
            case "binding-add":
                spec["components"]!["api"]!["bindings"]!["other"] = spec["components"]!["api"]!["bindings"]!["storage"]!.DeepClone();
                spec["components"]!["api"]!["bindings"]!["other"]!.AsObject().Remove("configurationSlot"); break;
        }
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var revision = await ImportUpdate(client, envelope);
        var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 1 });
        var result = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Contains(result.Status, new[] { PlanningStatus.UnsupportedShape, PlanningStatus.ConfigurationRefused }); Assert.Null(result.Plan);
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using var verify = Context(scenario.Connection); var deployment = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
        Assert.Equal(1, deployment.DesiredGeneration); Assert.Equal(1, deployment.AppliedGeneration);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("missing-management", false)]
    [InlineData("foreign-management", false)]
    [InlineData("missing-output", false)]
    [InlineData("corrupt-output", false)]
    [InlineData("missing-attempt", false)]
    [InlineData("manual-success", false)]
    [InlineData("missing-material", false)]
    [InlineData("foreign-secret", false)]
    [InlineData("wrong-contract", false)]
    [InlineData("missing-slot", false)]
    [InlineData("missing-source", false)]
    [InlineData("missing-registration-and-output", false)]
    [InlineData("missing-management", true)]
    [InlineData("foreign-management", true)]
    [InlineData("missing-output", true)]
    [InlineData("corrupt-output", true)]
    [InlineData("missing-attempt", true)]
    [InlineData("manual-success", true)]
    [InlineData("missing-material", true)]
    [InlineData("foreign-secret", true)]
    [InlineData("wrong-contract", true)]
    [InlineData("missing-slot", true)]
    [InlineData("missing-source", true)]
    [InlineData("corrupt-material", true)]
    public async Task Resource_reuse_missing_corrupt_and_foreign_original_evidence_never_advances(string fault, bool afterCommit)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            services =>
            {
                if (fault == "missing-registration-and-output")
                    foreach (var registration in services.Where(x => x.ServiceType == typeof(IExternalOperationOutputContract) &&
                        x.ImplementationType == typeof(PostgreSqlApplicationConnectionContract)).ToArray()) services.Remove(registration);
            });
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var revision = await ImportUpdate(client, UpdateEnvelope(scenario, 2));
        DurablePlanSummary? plan = afterCommit ? await UpdatePlan(client, revision, 1) : null;
        if (plan is not null) Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + (fault switch
        {
            "missing-management" => "DELETE FROM resource_management WHERE resource_local_id = 'app-login'",
            "foreign-management" => "UPDATE resource_management SET environment_record_id = '00000000-0000-0000-0000-000000000001' WHERE resource_local_id = 'app-login'",
            "missing-output" or "missing-registration-and-output" => "DELETE FROM operation_outputs WHERE operation_id = 'app-login-access'",
            "corrupt-output" => "UPDATE operation_outputs SET payload = '{}' WHERE operation_id = 'app-login-access'",
            "missing-attempt" => "DELETE FROM durable_create_operation_attempts WHERE operation_id = 'app-login-access'",
            "manual-success" => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'db-create'",
            "foreign-secret" => "UPDATE secrets SET application_record_id = '00000000-0000-0000-0000-000000000001' WHERE resource_local_id = 'app-login'",
            "wrong-contract" => "UPDATE operation_outputs SET output_contract = 'sample.wrong-output/v1' WHERE operation_id = 'app-login-access'",
            "missing-slot" => "DELETE FROM generated_credential_slots",
            "missing-source" => "UPDATE resource_management SET source_generation = 999 WHERE resource_local_id = 'app-login'",
            "corrupt-material" => "UPDATE secret_payloads SET bytes = decode(repeat('00', 43), 'hex') WHERE secret_version_id IN (SELECT version_id FROM generated_credential_slots)",
            _ => "DELETE FROM secret_payloads WHERE secret_version_id IN (SELECT version_id FROM generated_credential_slots)"
        }));
        var history = await ReuseHistory(scenario.Connection);
        if (afterCommit)
        {
            var execution = await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 });
            Assert.Equal(GenerationExecutionStatus.InputUnavailable, execution.Status);
            Assert.Equal(1, execution.Progress.AppliedGeneration);
        }
        else
        {
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 1 });
            Assert.Equal(PlanningStatus.PlanInputUnavailable, (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Status);
        }
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using var verify = Context(scenario.Connection); var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
        Assert.Equal(afterCommit ? 2 : 1, owner.DesiredGeneration); Assert.Equal(1, owner.AppliedGeneration);
        await host.StopAsync();
    }

    [Fact]
    public async Task Resource_reuse_competing_commits_wait_on_real_database_lock_then_have_one_atomic_winner()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var history = await ReuseHistory(scenario.Connection);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var revision = await ImportUpdate(client, UpdateEnvelope(scenario, 2));
        var first = await UpdatePlan(client, revision, 1); var second = await UpdatePlan(client, revision, 1);
        var lockKey = $"haby-deployment:{Guid.Parse(scenario.Plan.Inputs.Environment.ToString()):N}:{Guid.Parse(scenario.Plan.Inputs.Revision.ApplicationRecordId.ToString()):N}";
        await using var blocker = new NpgsqlConnection(scenario.Connection); await blocker.OpenAsync(); await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", blocker, tx))
        { hold.Parameters.AddWithValue(lockKey); await hold.ExecuteNonQueryAsync(); }
        var one = client.CommitDesiredStateAsync(new() { ChangeSetId = first.ChangeSetId, PlanId = first.PlanId }).ResponseAsync;
        var two = client.CommitDesiredStateAsync(new() { ChangeSetId = second.ChangeSetId, PlanId = second.PlanId }).ResponseAsync;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var observer = new NpgsqlConnection(scenario.Connection))
        {
            await observer.OpenAsync(deadline.Token);
            while (true)
            {
                await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", observer);
                if ((long)(await waiting.ExecuteScalarAsync(deadline.Token))! >= 2) break;
                Assert.False(one.IsCompleted); Assert.False(two.IsCompleted);
                await Task.Delay(10, deadline.Token);
            }
        }
        await tx.CommitAsync();
        var results = await Task.WhenAll(one, two);
        Assert.Single(results, x => x.Status == CommitStatus.Committed); Assert.Single(results, x => x.Status == CommitStatus.ChangeSetStale);
        await using var verify = Context(scenario.Connection);
        Assert.Equal(2, await verify.DeploymentGenerations.CountAsync()); Assert.Equal(2, await verify.ChangeSetCommits.CountAsync());
        var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync(); Assert.Equal(2, owner.DesiredGeneration); Assert.Equal(1, owner.AppliedGeneration);
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); await host.StopAsync();
    }
}
