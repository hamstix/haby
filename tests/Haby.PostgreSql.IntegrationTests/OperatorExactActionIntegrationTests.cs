using Google.Protobuf;
using Haby.Application;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Theory]
    [InlineData(OperationState.Pending, false)]
    [InlineData(OperationState.Preparing, false)]
    [InlineData(OperationState.Preparing, true)]
    [InlineData(OperationState.Dispatching, false)]
    [InlineData(OperationState.NeedsAttention, false)]
    [InlineData(OperationState.Succeeded, false)]
    [InlineData(OperationState.Failed, false)]
    public async Task Missing_exact_action_preserves_history_barriers_and_restored_safe_work(OperationState state, bool closedAttempt)
    {
        await using var scenario = await ExecutionScenarioAsync();
        OperationRequest request;
        OperationReply before;
        await using (var host = await StartAsync(scenario.Connection, scenario.CertificatePath))
        {
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            request = await CommitExecutionAsync(client, scenario.Database);
            if (state == OperationState.Succeeded)
                Assert.Equal(OperationStatus.Succeeded, (await client.ExecuteOperationAsync(request)).Status);
            else if (state != OperationState.Pending)
            {
                var journal = host.Services.GetRequiredService<IDurableCreateOperationJournal>();
                var key = ExecutionKey(request);
                var begun = Assert.IsType<DurableCreateBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default));
                if (state is OperationState.Dispatching or OperationState.NeedsAttention)
                    Assert.IsType<DurableCreateMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, begun.AttemptId, DateTimeOffset.UtcNow, default));
                if (state == OperationState.NeedsAttention)
                    Assert.True(await journal.TryRecoverDispatchingAsync(key, DateTimeOffset.UtcNow, default));
                if (closedAttempt)
                    Assert.True(await journal.TryCloseAttemptBeforeDispatchAsync(key, begun.AttemptId,
                        DurableCreateAttemptOutcome.FailedBeforeDispatch, DurableCreateOperationReason.Connectivity,
                        DateTimeOffset.UtcNow, default));
                if (state == OperationState.Failed)
                    Assert.True(await journal.TryFinalizeFailureAsync(key, begun.AttemptId, DurableCreateOperationState.Failed,
                        DurableCreateOperationReason.InputInvalid, DateTimeOffset.UtcNow, default));
            }
            before = await client.GetOperationAsync(request);
        }
        var noCalls = new NoActionTarget();
        await using (var missing = OperatorHostApplication.Build(Arguments(scenario.Connection, scenario.CertificatePath), services =>
        {
            services.RemoveAll<IDurableActionHandler>().RemoveAll<IDurableActionAdmissionHandler>();
            services.RemoveAll<IProviderConfigurationCodec>().RemoveAll<IProviderDeclarationAdapter>().RemoveAll<IProviderPreflightHandler>();
            services.AddSingleton<IPostgreSqlDatabaseCreateTarget>(noCalls);
        }))
        {
            await missing.StartAsync();
            using var channel = Channel(missing, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(before, await client.GetOperationAsync(request));
            var expected = state switch
            {
                OperationState.Pending or OperationState.Preparing => OperationStatus.HandlerUnavailable,
                OperationState.Succeeded => OperationStatus.Succeeded,
                OperationState.Failed => OperationStatus.Failed,
                _ => OperationStatus.NeedsAttention
            };
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.ExecuteOperationAsync(request).ResponseAsync));
            Assert.All(results, result => Assert.Equal(expected, result.Status));
            Assert.All(results, result => Assert.Equal((int)expected, (int)OperationReply.Parser.ParseFrom(result.ToByteArray()).Status));
            var after = await client.GetOperationAsync(request);
            if (state == OperationState.Dispatching)
            {
                Assert.Equal(OperationState.NeedsAttention, after.Operation.State);
                Assert.Equal(OperationReason.UncertainAfterDispatch, after.Operation.Reason);
                Assert.Equal(OperationAttemptOutcome.Uncertain, Assert.Single(after.Attempts).Outcome);
            }
            else Assert.Equal(before, after);
            Assert.Equal(0, noCalls.Calls);
            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
            if (binary is not null && expected == OperationStatus.HandlerUnavailable)
            {
                Assert.Equal(11, (int)expected);
                foreach (var format in new[] { "text", "json" })
                {
                    var text = await RunNativeCommitCli(binary, missing.Urls.Single(),
                        ["operation", "execute", request.DeploymentId, "1", "db", "--action", "create"], 2, format);
                    Assert.Contains("handlerUnavailable", text);
                    Assert.DoesNotContain(scenario.Password, text);
                    if (format == "json")
                    {
                        using var json = System.Text.Json.JsonDocument.Parse(text);
                        var snapshot = json.RootElement.GetProperty("data"u8).GetProperty("operation"u8);
                        Assert.Equal(state == OperationState.Pending ? "pending" : "preparing", snapshot.GetProperty("state"u8).GetString());
                        if (closedAttempt) Assert.Equal("connectivity", snapshot.GetProperty("reason"u8).GetString());
                        else Assert.False(snapshot.TryGetProperty("reason"u8, out _));
                        Assert.Equal(before.Operation.AttemptNumber, snapshot.GetProperty("attemptNumber"u8).GetInt64());
                    }
                }
                Assert.Equal(after, await client.GetOperationAsync(request));
            }
        }
        await using (var restored = await StartAsync(scenario.Connection, scenario.CertificatePath))
        {
            using var channel = Channel(restored, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var result = await client.ExecuteOperationAsync(request);
            Assert.Equal(state switch
            {
                OperationState.Dispatching or OperationState.NeedsAttention => OperationStatus.NeedsAttention,
                OperationState.Failed => OperationStatus.Failed,
                _ => OperationStatus.Succeeded
            }, result.Status);
        }
        await AssertExecutionCatalog(scenario,
            exists: state is OperationState.Pending or OperationState.Preparing or OperationState.Succeeded,
            applied: state is OperationState.Pending or OperationState.Preparing or OperationState.Succeeded,
            dispatched: state is OperationState.Failed ? 0 : 1);
    }

    private sealed class NoActionTarget : IPostgreSqlDatabaseCreateTarget
    {
        public int Calls;
        public ValueTask<PostgreSqlDatabaseAbsenceOutcome> CheckDatabaseAbsentAsync(PostgreSqlDatabaseCreateInput input,
            CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException(); }
        public ValueTask<PostgreSqlDatabaseCreateDispatchOutcome> DispatchCreateAsync(PostgreSqlDatabaseCreateInput input,
            CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException(); }
    }
}
