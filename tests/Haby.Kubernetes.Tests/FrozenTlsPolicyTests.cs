using Haby.Application;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class FrozenTlsPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deployment_execution_and_observation_reload_the_accepted_policy(bool observe)
    {
        var context = Capture(InternalDeploymentPlacementTests.Contexts(false)[0]);
        KubernetesTlsPolicy? seen = null;
        var handler = new InternalDeploymentAction(null!, connection =>
        {
            seen = connection.Tls;
            return new(connection.Binding, _ => ValueTask.FromException<KernelClient>(new IOException("test-only unavailable")));
        });
        var payload = handler.Prepare(context);
        var result = observe ? await handler.ObserveAsync(context, payload, default) : await handler.ExecuteAsync(context, payload, default);
        Assert.NotEqual(InternalActionOutcome.Confirmed, result.Outcome);
        Assert.Equal(new(KubernetesTlsMode.System, KubernetesRevocationMode.Offline), seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_identity_execution_and_observation_reload_the_accepted_policy(bool observe)
    {
        var context = Capture(InternalDeploymentPlacementTests.Contexts(false, configured: true)[0]) with { InstallationId = Guid.NewGuid() };
        KubernetesTlsPolicy? seen = null;
        var handler = new InternalRuntimeIdentityAction(null!, (connection, _) =>
        {
            seen = connection.Tls;
            return ValueTask.FromException<KernelClient>(new IOException("test-only unavailable"));
        });
        var payload = handler.Prepare(context);
        var result = observe ? await handler.ObserveAsync(context, payload, default) : await handler.ExecuteAsync(context, payload, default);
        Assert.NotEqual(InternalActionOutcome.Confirmed, result.Outcome);
        Assert.Equal(new(KubernetesTlsMode.System, KubernetesRevocationMode.Offline), seen);
    }

    private static InternalActionContext Capture(InternalActionContext context)
    {
        var codec = new KubernetesProviderConfigurationCodec();
        var accepted = codec.DecodeTyped(context.Configuration.PayloadJson) with { Tls = new(KubernetesTlsMode.System, KubernetesRevocationMode.Offline) };
        // Persisted payload is the sole input to newly constructed consumers after restart.
        return context with { Configuration = codec.Encode(codec.DecodeTyped(codec.Encode(accepted).PayloadJson)) };
    }
}
