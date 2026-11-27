using Haby.Application;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class DeploymentSerializationTests
{
    [Fact]
    public async Task Different_generations_of_one_deployment_wait_for_the_current_action_boundary()
    {
        var serialization = new DurableOperationSerialization();
        var deployment = ApplicationDeploymentId.New();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = serialization.RunAsync(Key(deployment, 1), async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            return 1;
        }, timeout.Token).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        var successor = serialization.RunAsync(Key(deployment, 2), () => ValueTask.FromResult(2), timeout.Token).AsTask();
        try
        {
            // The callback is synchronous: without the shared deployment gate it has already completed.
            Assert.False(successor.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, successor).WaitAsync(timeout.Token);
        }
        Assert.Equal(1, await first);
        Assert.Equal(2, await successor);
    }

    [Fact]
    public async Task Cancelled_successor_never_enters_or_releases_the_active_deployment_gate()
    {
        var serialization = new DurableOperationSerialization();
        var deployment = ApplicationDeploymentId.New();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var active = serialization.RunAsync(Key(deployment, 1), async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            return 1;
        }, timeout.Token).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        using var cancelled = new CancellationTokenSource();
        var calls = 0;
        var waiting = serialization.RunAsync(Key(deployment, 2), () =>
        {
            calls++;
            return ValueTask.FromResult(2);
        }, cancelled.Token).AsTask();
        cancelled.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(0, calls);
            var peer = serialization.RunAsync(Key(ApplicationDeploymentId.New(), 2), () => ValueTask.FromResult(3), timeout.Token);
            Assert.Equal(3, await peer);
            Assert.False(active.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await active.WaitAsync(timeout.Token);
        }
        Assert.Equal(4, await serialization.RunAsync(Key(deployment, 3), () => ValueTask.FromResult(4), timeout.Token));
    }

    [Fact]
    public async Task Deployment_transition_uses_the_same_gate_and_an_exception_releases_only_its_owner()
    {
        var serialization = new DurableOperationSerialization();
        var deployment = ApplicationDeploymentId.New();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transition = serialization.RunDeploymentAsync<int>(deployment, async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            throw new InvalidOperationException("transition refused");
        }, timeout.Token).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        var action = serialization.RunAsync(Key(deployment, 2), () => ValueTask.FromResult(2), timeout.Token).AsTask();
        try { Assert.False(action.IsCompleted); }
        finally { release.TrySetResult(); }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transition);
        Assert.Equal("transition refused", error.Message);
        Assert.Equal(2, await action.WaitAsync(timeout.Token));
    }

    private static DurableOperationKey Key(ApplicationDeploymentId deployment, long generation) =>
        new(deployment, DeploymentGeneration.Create(generation), OperationId.Create("effect"));
}
