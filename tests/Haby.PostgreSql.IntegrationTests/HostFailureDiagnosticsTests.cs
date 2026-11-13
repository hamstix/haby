using Grpc.Core;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class HostFailureDiagnosticsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Anonymous_reflection_assertion_accepts_exact_denial_from_write_or_read(bool earlyWrite)
    {
        var phase = HostFailurePhase.Channel;
        StatusCode? observed = null;
        var reads = 0;
        var denial = new RpcException(new Status(StatusCode.Unauthenticated, string.Empty));
        await AnonymousReflectionAssertion.RequireUnauthenticatedAsync(
            () => earlyWrite ? Task.FromException(denial) : Task.CompletedTask,
            () => { reads++; return Task.FromException(denial); },
            value => phase = value, value => observed = value);
        Assert.Equal(earlyWrite ? 0 : 1, reads);
        Assert.Equal(HostFailurePhase.StatusAssertion, phase);
        Assert.Equal(StatusCode.Unauthenticated, observed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Anonymous_reflection_assertion_refuses_wrong_status_and_non_rpc_errors(bool earlyWrite, bool nonRpc)
    {
        var phase = HostFailurePhase.Channel;
        StatusCode? observed = null;
        var reads = 0;
        Exception error = nonRpc ? new InvalidOperationException() : new RpcException(new Status(StatusCode.PermissionDenied, string.Empty));
        var failure = await Record.ExceptionAsync(() => AnonymousReflectionAssertion.RequireUnauthenticatedAsync(
            () => earlyWrite ? Task.FromException(error) : Task.CompletedTask,
            () => { reads++; return Task.FromException(error); },
            value => phase = value, value => observed = value));
        Assert.NotNull(failure);
        Assert.Equal(earlyWrite ? 0 : 1, reads);
        if (nonRpc)
        {
            Assert.Null(observed);
            Assert.Equal(earlyWrite ? HostFailurePhase.ReflectionWrite : HostFailurePhase.ReflectionRead, phase);
        }
        else
        {
            Assert.Equal(StatusCode.PermissionDenied, observed);
            Assert.Equal(HostFailurePhase.StatusAssertion, phase);
        }
    }

    [Fact]
    public async Task Anonymous_reflection_assertion_refuses_missing_denial()
    {
        var phase = HostFailurePhase.Channel;
        StatusCode? observed = null;
        var failure = await Record.ExceptionAsync(() => AnonymousReflectionAssertion.RequireUnauthenticatedAsync(
            () => Task.CompletedTask, () => Task.CompletedTask,
            value => phase = value, value => observed = value));
        Assert.NotNull(failure);
        Assert.Null(observed);
        Assert.Equal(HostFailurePhase.ReflectionRead, phase);
    }

    [Theory]
    [InlineData((int)HostFailurePhase.ReflectionWrite)]
    [InlineData((int)HostFailurePhase.ReflectionRead)]
    [InlineData((int)HostFailurePhase.StatusAssertion)]
    public async Task Body_failure_survives_every_failing_disposal_with_separate_safe_evidence(int phaseValue)
    {
        var phase = (HostFailurePhase)phaseValue;
        var primary = new RpcException(new Status(StatusCode.Unauthenticated, "PROTECTED-body"));
        var cleanup = new RpcException(new Status(StatusCode.Internal, "PROTECTED-cleanup"));
        var evidence = new List<string>();
        var disposed = new List<int>();
        var error = await Record.ExceptionAsync(() => HostFailureDiagnostics.RunAsync(defer =>
        {
            for (var i = 0; i < 3; i++)
            {
                var index = i;
                defer(() =>
                {
                    disposed.Add(index);
                    if (index == 0) return ValueTask.FromException(cleanup);
                    throw cleanup;
                });
            }
            throw primary;
        }, () => phase, () => StatusCode.Unauthenticated,
            (stage, failure, observed) =>
            {
                if (stage != HostFailurePhase.Cleanup) Assert.Empty(disposed);
                evidence.Add(HostFailureDiagnostics.Render(stage, failure, observed));
            }));
        Assert.Same(primary, error);
        Assert.Equal([2, 1, 0], disposed);
        Assert.Equal(4, evidence.Count);
        Assert.Equal(HostFailureDiagnostics.Render(phase, primary, null), evidence[0]);
        Assert.All(evidence.Skip(1), line => Assert.Equal("cleanup,internal", line));
    }

    [Fact]
    public async Task Cleanup_only_failure_is_reported_and_rethrown_without_stale_body_status()
    {
        var cleanup = new InvalidOperationException("PROTECTED-cleanup");
        var evidence = new List<string>();
        var error = await Record.ExceptionAsync(() => HostFailureDiagnostics.RunAsync(defer =>
        {
            defer(() => throw cleanup);
            return Task.CompletedTask;
        }, () => HostFailurePhase.StatusAssertion, () => StatusCode.Unauthenticated,
            (stage, failure, observed) => evidence.Add(HostFailureDiagnostics.Render(stage, failure, observed))));
        Assert.Same(cleanup, error);
        Assert.Equal(["cleanup,unavailable-evidence"], evidence);
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated, "unauthenticated")]
    [InlineData(StatusCode.Internal, "internal")]
    public void Transport_diagnostic_keeps_phase_and_code_without_driver_text(StatusCode code, string expected)
    {
        const string secret = "PROTECTED-driver-credential-payload";
        var error = new RpcException(new Status(code, secret));
        var line = HostFailureDiagnostics.Render(HostFailurePhase.ReflectionWrite, error, null);
        Assert.Equal("reflection-write," + expected, line);
        Assert.DoesNotContain(secret, line);
    }

    [Fact]
    public void Assertion_diagnostic_keeps_observed_status_without_assertion_payload()
    {
        var line = HostFailureDiagnostics.Render(HostFailurePhase.StatusAssertion,
            new InvalidOperationException("PROTECTED-assertion-payload"), StatusCode.Unavailable);
        Assert.Equal("status-assertion,unavailable", line);
    }
}
