using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_recovery_assigns_only_standalone_uncertain_Create_and_never_a_pair(bool pair)
    {
        await using var scenario = await ExecutionScenarioAsync();
        OperationRequest request;
        // Only the existing internal lost-confirmation hook is injected. All admissions,
        // executors, target SQL and recovery services are the production registrations.
        await using (var host = await StartExecutionFaultHost(scenario, new ExecutionHooks
        { BeforeSuccess = _ => throw new IOException("Test-only lost confirmation.") }))
        {
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            if (pair)
            {
                var envelope = PostgreSqlPairPlanningTests.Example();
                envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = scenario.Database;
                var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) });
                var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = imported.Created.Revision.Reference, Precondition = PlanningPrecondition.ExpectedAbsence });
                var plan = (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Plan;
                Assert.Equal(5, plan.Operations.Count);
                var accepted = await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId });
                request = new()
                {
                    DeploymentId = accepted.Commit.DeploymentId,
                    Generation = 1,
                    OperationId = Assert.Single(plan.Operations, n => n.ActionContract == PostgreSqlDatabaseCreateAction.Contract.Value).OperationId
                };
            }
            else request = await CommitExecutionAsync(client, scenario.Database);
            await Assert.ThrowsAsync<RpcException>(async () => await client.ExecuteOperationAsync(request));
            Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(request)).Status);
        }
        Assert.Equal(1L, await ExecutionScalar(scenario.Connection, "SELECT count(*) FROM pg_database WHERE datname = @name",
            c => c.Parameters.AddWithValue("name", scenario.Database)));
        for (var restart = 0; restart < 2; restart++)
        {
            await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var operation = await client.GetOperationAsync(request);
            var attempt = Assert.Single(operation.Attempts);
            Assert.Equal(OperationAttemptOutcome.Uncertain, attempt.Outcome);
            var before = await RecoveryCatalog(scenario);
            var preview = await client.InspectOperationRecoveryAsync(request);
            Assert.Equal(RecoveryStatus.Found, preview.Status);
            Assert.Equal(!pair && restart == 0, preview.Preview.HasUncertainAttemptId);
            Assert.Equal(before, await RecoveryCatalog(scenario));
            var assignment = new AssignExistingOperationRequest
            {
                Key = request,
                EnvironmentRecordId = operation.Operation.EnvironmentRecordId,
                ExpectedAttemptId = attempt.AttemptId,
                Acknowledged = true,
                Reason = RecoveryReason.OperatorAcceptedExistingDatabase,
                Note = RecoveryNote
            };
            var assigned = await client.AssignExistingOperationAsync(assignment);
            Assert.Equal(pair ? RecoveryStatus.Rejected : restart == 0 ? RecoveryStatus.Assigned : RecoveryStatus.AlreadyAssigned, assigned.Status);
            if (pair)
            {
                Assert.Null(assigned.Receipt);
                Assert.Equal(before, await RecoveryCatalog(scenario));
                var command = new AssignExistingPostgreSqlDatabase(ExecutionKey(request), scenario.Environment,
                    Guid.Parse(attempt.AttemptId), true, PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, RecoveryNote, "tester");
                // The transactional boundary must also refuse a trusted direct caller.
                Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Rejected,
                    (await host.Services.GetRequiredService<IPostgreSqlDatabaseRecoveryJournal>().TryAssignAsync(command, DateTimeOffset.UtcNow, default)).Outcome);
                Assert.Equal(before, await RecoveryCatalog(scenario));
                Assert.Equal(operation, await client.GetOperationAsync(request));
            }
            await using var verify = PairContext(scenario.Connection);
            Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
            Assert.Equal(pair ? 0 : 1, await verify.PostgreSqlDatabaseRecoveryDecisions.CountAsync());
            Assert.Equal(pair ? null : 1L, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Equal("Uncertain", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync()).Outcome);
        }
    }
}
