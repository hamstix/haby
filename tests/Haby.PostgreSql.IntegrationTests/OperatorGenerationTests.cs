using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Generation_requests_validate_exact_identity_and_never_dispatch_on_refusal()
    {
        await using var scenario = await ExecutionScenarioAsync();
        await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var channel = Channel(host, scenario.Certificate);
        var client = OperatorTestCredentials.Client(channel);
        var operation = await CommitExecutionAsync(client, scenario.Database);
        foreach (var (id, gen, status) in new[]
        {
            ("", 1L, GenerationExecutionStatus.InvalidRequest),
            ("bad", 1L, GenerationExecutionStatus.InvalidRequest),
            (operation.DeploymentId, 0L, GenerationExecutionStatus.InvalidRequest),
            (operation.DeploymentId, -1L, GenerationExecutionStatus.InvalidRequest),
            (operation.DeploymentId, 2L, GenerationExecutionStatus.UnsupportedGeneration),
            (Haby.Domain.ApplicationDeploymentId.New().ToString(), 1L, GenerationExecutionStatus.NotFound)
        })
        {
            var result = await client.ExecuteGenerationAsync(new() { DeploymentId = id, Generation = gen });
            Assert.Equal(status, result.Status);
            Assert.Null(result.Progress);
            Assert.False(result.HasStopOutcome);
            Assert.False(result.HasStoppedOperationId);
        }
        Assert.Empty((await client.GetOperationAsync(operation)).Attempts);
        await AssertExecutionCatalog(scenario, exists: false, applied: false, dispatched: 0);
        await ExecutionSql(scenario.Connection, $"CREATE DATABASE \"{scenario.Database}\"");
        var failed = await client.ExecuteGenerationAsync(new() { DeploymentId = operation.DeploymentId, Generation = 1 });
        Assert.Equal(GenerationExecutionStatus.Failed, failed.Status);
        Assert.Equal(OperationStatus.Failed, failed.StopOutcome);
        Assert.Equal(OperationReason.Conflict, Assert.Single(failed.Progress.Nodes).Operation.Reason);
        Assert.Equal(failed, await client.ExecuteGenerationAsync(new() { DeploymentId = operation.DeploymentId, Generation = 1 }));
        Assert.Single((await client.GetOperationAsync(operation)).Attempts);
    }

    [Fact]
    public async Task Production_generation_and_operation_RPC_share_gate_and_progress_remains_observable()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new ExecutionHooks { AfterMarker = async token => { entered.SetResult(); await release.Task.WaitAsync(token); } };
        await using var host = await StartExecutionFaultHost(scenario, hooks);
        using var channel = Channel(host, scenario.Certificate);
        var client = OperatorTestCredentials.Client(channel);
        var operation = await CommitExecutionAsync(client, scenario.Database);
        var request = new GenerationRequest { DeploymentId = operation.DeploymentId, Generation = 1 };
        var snapshot = await client.GetGenerationAsync(request);
        using var first = client.ExecuteGenerationAsync(request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var second = client.ExecuteGenerationAsync(request);
            using var direct = client.ExecuteOperationAsync(operation);
            using var cancel = new CancellationTokenSource();
            using var waiting = client.ExecuteGenerationAsync(request, cancellationToken: cancel.Token);
            // This fourth request waits for the same gate. Its cancellation cannot create another attempt.
            cancel.Cancel();
            Assert.Equal(StatusCode.Cancelled, (await Assert.ThrowsAsync<RpcException>(async () => await waiting)).StatusCode);
            var progress = await client.GetGenerationAsync(request).ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(snapshot.DesiredGeneration, progress.DesiredGeneration);
            Assert.Equal(0, progress.Progress.Completed);
            Assert.Equal(OperationState.Dispatching, Assert.Single(progress.Progress.Nodes).Operation.State);
            Assert.False(progress.Progress.Nodes[0].HasLastAttemptOutcome);
            Assert.False(second.ResponseAsync.IsCompleted);
            Assert.False(direct.ResponseAsync.IsCompleted);
            release.SetResult();
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await first.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(15))).Status);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await second.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(15))).Status);
            Assert.Equal(OperationStatus.Succeeded, (await direct.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(15))).Status);
            Assert.Single((await client.GetOperationAsync(operation)).Attempts);
        }
        finally { release.TrySetResult(); }
        await AssertExecutionCatalog(scenario, exists: true, applied: true, dispatched: 1);
    }

    [Fact]
    public async Task Generation_cancellation_after_real_CREATE_requires_read_only_inspection_and_never_replays()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new ExecutionHooks
        {
            BeforeSuccess = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { finished.SetResult(); }
        }
        };
        OperationRequest operation;
        await using (var host = await StartExecutionFaultHost(scenario, hooks))
        {
            using var channel = Channel(host, scenario.Certificate);
            var client = OperatorTestCredentials.Client(channel);
            operation = await CommitExecutionAsync(client, scenario.Database);
            using var cancel = new CancellationTokenSource();
            using var call = client.ExecuteGenerationAsync(new() { DeploymentId = operation.DeploymentId, Generation = 1 }, cancellationToken: cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await AssertExecutionCatalog(scenario, exists: true, applied: false, dispatched: 1);
            cancel.Cancel();
            Assert.Equal(StatusCode.Cancelled, (await Assert.ThrowsAsync<RpcException>(async () => await call)).StatusCode);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await host.StopAsync();
        }
        await using var restarted = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var nextChannel = Channel(restarted, scenario.Certificate);
        var next = OperatorTestCredentials.Client(nextChannel);
        var request = new GenerationRequest { DeploymentId = operation.DeploymentId, Generation = 1 };
        var before = await next.GetGenerationAsync(request);
        Assert.Equal(OperationState.Dispatching, Assert.Single(before.Progress.Nodes).Operation.State);
        Assert.Equal(before, await next.GetGenerationAsync(request)); // reads never recover
        Assert.Equal(GenerationExecutionStatus.NeedsAttention, (await next.ExecuteGenerationAsync(request)).Status);
        var attention = await next.GetGenerationAsync(request);
        Assert.Equal(OperationAttemptOutcome.Uncertain, attention.Progress.Nodes[0].LastAttemptOutcome);
        Assert.Equal(GenerationExecutionStatus.NeedsAttention, (await next.ExecuteGenerationAsync(request)).Status);
        Assert.Equal(attention, await next.GetGenerationAsync(request));
        Assert.Equal(OperationStatus.NeedsAttention, (await next.ExecuteOperationAsync(operation)).Status);
        Assert.Single((await next.GetOperationAsync(operation)).Attempts);
        await AssertExecutionCatalog(scenario, exists: true, applied: false, dispatched: 1);
    }
}
