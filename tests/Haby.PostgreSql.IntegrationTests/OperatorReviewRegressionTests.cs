using Google.Protobuf;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Theory]
    [InlineData("snapshot")]
    [InlineData("graph-version")]
    [InlineData("graph-json")]
    [InlineData("graph-fingerprint")]
    public async Task Unsupported_saved_plan_is_a_typed_read_only_refusal_through_check_get_and_native_cli(string invalid)
    {
        await using var scenario = await ExecutionScenarioAsync();
        var target = new NoActionTarget();
        await using var host = OperatorHostApplication.Build(Arguments(scenario.Connection, scenario.CertificatePath),
            services => services.AddSingleton<Haby.PostgreSql.IPostgreSqlDatabaseCreateTarget>(target));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = Channel(host, scenario.Certificate);
        var client = OperatorTestCredentials.Client(channel);
        var imported = await client.ImportAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(Envelope("unsupported-saved-plan")) });
        var change = await client.CreateChangeSetAsync(new CreateChangeSetRequest
        {
            EnvironmentId = "pilot",
            Reference = imported.Created.Revision.Reference,
            Precondition = PlanningPrecondition.ExpectedAbsence
        });
        var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, plan.Status);
        var request = new PlanRequest { PlanId = plan.Plan.PlanId };
        // Only this disposable fixture bypasses the immutable-row guard to model unreadable storage.
        await using (var arrange = new NpgsqlConnection(scenario.Connection))
        {
            await arrange.OpenAsync();
            await new NpgsqlCommand("SET session_replication_role = replica", arrange).ExecuteNonQueryAsync();
            var sql = invalid switch
            {
                "snapshot" => "UPDATE deployment_plans SET snapshot_format_version = 99 WHERE id = @id",
                "graph-version" => "UPDATE deployment_plans SET graph_json = '{\"Version\":99,\"Nodes\":[]}' WHERE id = @id",
                "graph-json" => "UPDATE deployment_plans SET graph_json = '{private-format-sentinel' WHERE id = @id",
                _ => "UPDATE deployment_plans SET fingerprint = 'v1:' || repeat('0', 64) WHERE id = @id"
            };
            await using var command = new NpgsqlCommand(sql, arrange);
            command.Parameters.AddWithValue("id", Guid.Parse(request.PlanId));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var before = await RestoreCatalogDigests(scenario.Connection, false);
        Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await host.Services.GetRequiredService<IDurableDeploymentPlanner>()
            .CheckPlanAsync(DeploymentPlanId.Parse(request.PlanId), default));
        foreach (var reply in new[] { await client.CheckPlanAsync(request), await client.GetPlanAsync(request) })
        {
            Assert.Equal(PlanningStatus.PlanInputUnavailable, reply.Status);
            Assert.Null(reply.Plan);
            Assert.DoesNotContain("private-format-sentinel", reply.ToString());
            Assert.DoesNotContain(scenario.Password, reply.ToString());
        }
        var binary = System.Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (System.Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is not null)
            foreach (var verb in new[] { "get", "check" })
            {
                var output = await RunNativeCommitCli(binary, host.Urls.Single(), ["plan", verb, request.PlanId], expectedExit: 1);
                Assert.Contains("planInputUnavailable", output);
                Assert.DoesNotContain("private-format-sentinel", output);
                Assert.DoesNotContain(scenario.Password, output);
            }
        Assert.Equal(before, await RestoreCatalogDigests(scenario.Connection, false));
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task Removed_wire_identity_cannot_select_an_existing_operation_or_enter_recovery()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var target = new NoActionTarget();
        await using var host = OperatorHostApplication.Build(Arguments(scenario.Connection, scenario.CertificatePath),
            services => services.AddSingleton<Haby.PostgreSql.IPostgreSqlDatabaseCreateTarget>(target));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = Channel(host, scenario.Certificate);
        var client = OperatorTestCredentials.Client(channel);
        var current = await CommitExecutionAsync(client, scenario.Database);
        Assert.Equal(OperationStatus.Found, (await client.GetOperationAsync(current)).Status);
        using var bytes = new MemoryStream();
        using (var wire = new CodedOutputStream(bytes, leaveOpen: true))
        {
            new OperationRequest { DeploymentId = current.DeploymentId, Generation = current.Generation }.WriteTo(wire);
            // The removed resource field deliberately equals a real current OperationId.
            wire.WriteTag(3, WireFormat.WireType.LengthDelimited); wire.WriteString(current.OperationId);
            wire.WriteTag(4, WireFormat.WireType.Varint); wire.WriteEnum(1);
        }
        var removed = OperationRequest.Parser.ParseFrom(bytes.ToArray());
        var before = await RestoreCatalogDigests(scenario.Connection, false);
        Assert.Equal(OperationStatus.InvalidRequest, (await client.GetOperationAsync(removed)).Status);
        Assert.Equal(OperationStatus.InvalidRequest, (await client.ExecuteOperationAsync(removed)).Status);
        Assert.Equal(RecoveryStatus.InvalidRequest, (await client.InspectOperationRecoveryAsync(removed)).Status);
        Assert.Equal(RecoveryStatus.InvalidRequest, (await client.AssignExistingOperationAsync(new AssignExistingOperationRequest
        {
            Key = removed,
            EnvironmentRecordId = scenario.Environment.ToString(),
            ExpectedAttemptId = Guid.NewGuid().ToString("D"),
            Acknowledged = true,
            Reason = RecoveryReason.OperatorAcceptedExistingDatabase,
            Note = "checked"
        })).Status);
        Assert.Equal(before, await RestoreCatalogDigests(scenario.Connection, false));
        Assert.Equal(0, target.Calls);
        Assert.Empty((await client.GetOperationAsync(current)).Attempts);
    }
}
