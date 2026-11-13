using Grpc.Core;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class HostFailureDiagnosticsTests
{
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
