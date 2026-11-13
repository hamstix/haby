using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Microsoft.Extensions.DependencyInjection;

namespace Haby.Tests;

/// <summary>Fixture composition only; never a Host registration or production preflight claim.</summary>
internal static class InternalKubernetesFixturePreflightRegistration
{
    internal static IServiceCollection AddInternalKubernetesFixturePreflight(this IServiceCollection services,
        Func<IServiceProvider, IProviderPreflightHandler> handler)
    {
        services.AddSingleton<IProviderPreflightHandler>(handler);
        services.AddSingleton<ProviderPreflightRegistry>();
        services.AddSingleton<IProviderRevisionPreflight, ExactProviderRevisionPreflight>();
        return services;
    }
}

internal delegate ValueTask<ProviderPreflightResult> InternalKubernetesFixtureProbe(ISecretStore secrets,
    EnvironmentRecordId environment, KubernetesProviderConfiguration configuration, CancellationToken token);

internal sealed class InternalKubernetesFixturePreflight(KubernetesProviderConfigurationCodec codec, ISecretStore secrets,
    InternalKubernetesFixtureProbe probe) : ProviderPreflightHandler<KubernetesProviderConfiguration>(codec)
{
    public override IReadOnlyList<ProviderAdapterKey> Keys { get; } = [new(KubernetesProviderConfigurationCodec.Kind,
        KubernetesProviderConfigurationCodec.Shape, KubernetesProviderConfigurationCodec.Implementation)];
    public override ValueTask<ProviderPreflightResult> CheckTypedAsync(EnvironmentRecordId environment,
        KubernetesProviderConfiguration configuration, CancellationToken token)
    {
        _ = codec.Encode(configuration);
        return probe(secrets, environment, configuration, token);
    }
    // Explicit deterministic double for catalog-only tests. It accepts only this
    // fixture's endpoint, credential alternative and exact scoped sentinel material.
    // It does not establish real API availability, permissions or production preflight.
    internal static async ValueTask<ProviderPreflightResult> DeterministicProbe(ISecretStore secrets, EnvironmentRecordId environment,
        KubernetesProviderConfiguration configuration, CancellationToken token)
    {
        if (configuration.Endpoint.AbsoluteUri != "https://127.0.0.1:6443/" || configuration.ServerCaPem is not null ||
            configuration.BearerToken is null || configuration.ClientCertificate is not null || configuration.ClientPrivateKey is not null)
            return new(ProviderPreflightOutcome.ConfigurationUnavailable);
        using var material = await secrets.ResolveBoundedAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator),
            configuration.BearerToken, 1_048_576, token);
        if (material is null) return new(ProviderPreflightOutcome.SecretUnavailable);
        var bytes = material.CopyBytes();
        try { return new(bytes.AsSpan().SequenceEqual("fixture-token"u8) ? ProviderPreflightOutcome.Ready : ProviderPreflightOutcome.AuthenticationFailed); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}
