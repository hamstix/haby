using Grpc.Core;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

// Test-only boundary for the deliberately anonymous duplex reflection call.
internal static class AnonymousReflectionAssertion
{
    internal static async Task RequireUnauthenticatedAsync(Func<Task> write, Func<Task> read,
        Action<HostFailurePhase> setPhase, Action<StatusCode> observeStatus)
    {
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            setPhase(HostFailurePhase.ReflectionWrite);
            await write();
            setPhase(HostFailurePhase.ReflectionRead);
            await read();
        });
        observeStatus(error.StatusCode);
        setPhase(HostFailurePhase.StatusAssertion);
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }
}
