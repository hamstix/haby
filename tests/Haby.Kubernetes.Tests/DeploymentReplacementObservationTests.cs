using System.Net;
using Haby.Tests;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class DeploymentReplacementObservationTests
{
    [Fact]
    public async Task Original_uid_must_disappear_before_replacement_can_continue()
    {
        var observations = new Queue<string?>(["original", "original", null]);
        var absent = false; var pauses = 0;
        using var deadline = new CancellationTokenSource();
        await DeploymentReplacementObservation.WaitAsync("original", token =>
        {
            Assert.Equal(deadline.Token, token);
            var uid = observations.Dequeue(); absent = uid is null;
            return Task.FromResult(uid);
        }, token =>
        {
            Assert.Equal(deadline.Token, token); Assert.False(absent); pauses++;
            return Task.CompletedTask;
        }, deadline.Token);
        Assert.True(absent); Assert.Empty(observations); Assert.Equal(2, pauses);
    }

    [Theory]
    [InlineData("unexpected")]
    [InlineData("")]
    public async Task Present_object_with_other_uid_refuses_without_adoption(string uid)
    {
        var reads = 0; var pauses = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeploymentReplacementObservation.WaitAsync("original", _ =>
        {
            reads++; return Task.FromResult<string?>(uid);
        }, _ => { pauses++; return Task.CompletedTask; }, default));
        Assert.Equal(1, reads); Assert.Equal(0, pauses);
    }

    [Fact]
    public async Task Read_conflict_propagates_without_retry_or_pause()
    {
        var error = new HttpRequestException("Fixture conflict.", null, HttpStatusCode.Conflict);
        var reads = 0; var pauses = 0;
        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => DeploymentReplacementObservation.WaitAsync("original", _ =>
        {
            reads++; throw error;
        }, _ => { pauses++; return Task.CompletedTask; }, default));
        Assert.Same(error, actual); Assert.Equal(1, reads); Assert.Equal(0, pauses);
    }

    [Fact]
    public async Task Present_original_exhausts_finite_read_budget_without_success()
    {
        var reads = 0; var pauses = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => DeploymentReplacementObservation.WaitAsync("original", _ =>
        {
            reads++; return Task.FromResult<string?>("original");
        }, _ => { pauses++; return Task.CompletedTask; }, default));
        Assert.Equal(50, reads); Assert.Equal(49, pauses);
    }

    [Fact]
    public async Task Existing_cancellation_stops_before_any_read()
    {
        using var deadline = new CancellationTokenSource(); deadline.Cancel(); var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeploymentReplacementObservation.WaitAsync("original", _ =>
        {
            reads++; return Task.FromResult<string?>(null);
        }, _ => Task.CompletedTask, deadline.Token));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Cancellation_during_pause_stops_before_another_read()
    {
        using var deadline = new CancellationTokenSource(); var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeploymentReplacementObservation.WaitAsync("original", _ =>
        {
            reads++; return Task.FromResult<string?>("original");
        }, token => { deadline.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, deadline.Token));
        Assert.Equal(1, reads);
    }
}
