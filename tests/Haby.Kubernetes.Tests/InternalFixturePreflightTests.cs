using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.PostgreSql;
using Haby.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class InternalFixturePreflightTests
{
    [Fact]
    public async Task Fixture_registration_routes_Kubernetes_to_its_exact_typed_probe_instead_of_the_original_PostgreSql_only_reader()
    {
        var scenario = new Scenario();
        Assert.Equal(ProviderPreflightOutcome.ConfigurationUnavailable,
            (await scenario.Legacy.CheckAsync(scenario.Environment, scenario.Binding, default)).Outcome);
        Assert.Equal(typeof(PostgreSqlProviderConfiguration), scenario.Store.RequestedType);
        await using var services = scenario.Services();
        Assert.Equal(ProviderPreflightOutcome.Ready, (await services.GetRequiredService<IProviderRevisionPreflight>()
            .CheckAsync(scenario.Environment, scenario.Binding, default)).Outcome);
        Assert.Equal(1, scenario.Secrets.Reads); Assert.Equal(1, scenario.Reader.Reads); Assert.Equal(1, scenario.Store.Reads);
    }
    [Fact]
    public async Task Typed_fixture_preflight_refuses_wrong_credential_material()
    {
        var scenario = new Scenario(); scenario.Secrets.Material = "wrong-fixture-token"u8.ToArray();
        await using var services = scenario.Services();
        Assert.Equal(ProviderPreflightOutcome.AuthenticationFailed, (await services.GetRequiredService<IProviderRevisionPreflight>()
            .CheckAsync(scenario.Environment, scenario.Binding, default)).Outcome);
        Assert.Equal(1, scenario.Secrets.Reads);
    }
    [Fact]
    public async Task Unsupported_exact_registration_refuses_before_configuration_or_secret_material()
    {
        var scenario = new Scenario(); scenario.Reader.Key = scenario.Reader.Key with { ImplementationId = "unsupported/v99" };
        await using var services = scenario.Services();
        Assert.Equal(ProviderPreflightOutcome.HandlerUnavailable, (await services.GetRequiredService<IProviderRevisionPreflight>()
            .CheckAsync(scenario.Environment, scenario.Binding, default)).Outcome);
        Assert.Equal(0, scenario.Reader.Reads); Assert.Equal(0, scenario.Secrets.Reads);
    }
    private sealed class Scenario
    {
        internal readonly EnvironmentRecordId Environment = EnvironmentRecordId.New();
        internal readonly ProviderAliasBinding Binding = new(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1));
        internal readonly KubernetesProviderConfigurationCodec Codec = new();
        internal readonly Secrets Secrets;
        internal readonly Reader Reader;
        internal readonly PostgreSqlProviderRevisionPreflight Legacy;
        internal readonly WrongTypedStore Store = new();
        internal Scenario()
        {
            var reference = new SecretRef(SecretId.New(), SecretVersionId.New()); Secrets = new(Environment, reference);
            var configuration = new KubernetesProviderConfiguration(new Uri("https://127.0.0.1:6443"), null, reference, null, null);
            Reader = new(Codec, configuration);
            Legacy = new(Store, Secrets);
        }
        internal ServiceProvider Services() => new ServiceCollection().AddSingleton<IProviderPreflightRevisionReader>(Reader)
            .AddSingleton<IProviderConfigurationCodec>(Codec).AddSingleton<IProviderRevisionPreflight>(Legacy)
            .AddInternalKubernetesFixturePreflight(_ => new InternalKubernetesFixturePreflight(Codec, Secrets, InternalKubernetesFixturePreflight.DeterministicProbe))
            .BuildServiceProvider();
    }
    private sealed class Reader(KubernetesProviderConfigurationCodec codec, KubernetesProviderConfiguration configuration) : IProviderPreflightRevisionReader
    {
        internal ProviderAdapterKey Key = new(codec.ProviderKindId, codec.ShapeId, KubernetesProviderConfigurationCodec.Implementation);
        internal int Reads;
        public ValueTask<ProviderAdapterKey?> FindKeyAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken token) => ValueTask.FromResult<ProviderAdapterKey?>(Key);
        public ValueTask<LoadedProviderRevision?> ReadAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken token)
        { Reads++; return ValueTask.FromResult<LoadedProviderRevision?>(new(Key, codec, configuration)); }
    }
    private sealed class WrongTypedStore : IProviderConfigurationStore
    {
        internal Type? RequestedType; internal int Reads;
        public ValueTask<ProviderConfigurationRevision<T>?> FindRevisionAsync<T>(EnvironmentRecordId environment, ProviderInstanceId instance,
            ProviderConfigurationRevisionNumber revision, CancellationToken token) where T : class
        { RequestedType = typeof(T); Reads++; throw new InvalidDataException("Provider configuration type is unavailable."); }
        public ValueTask<ProviderInstanceId> CreateInstanceAsync<T>(EnvironmentRecordId environment, T configuration, CancellationToken token) where T : class => throw new NotSupportedException();
        public ValueTask AddRevisionAsync<T>(EnvironmentRecordId environment, ProviderInstanceId instance, ProviderConfigurationRevisionNumber revision, T configuration, CancellationToken token) where T : class => throw new NotSupportedException();
    }
    private sealed class Secrets(EnvironmentRecordId environment, SecretRef reference) : ISecretStore
    {
        internal byte[] Material = "fixture-token"u8.ToArray(); internal int Reads;
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef requested, int maximumBytes, CancellationToken token)
        {
            Assert.Equal(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), definition); Assert.Equal(reference, requested);
            Assert.Equal(1_048_576, maximumBytes); Reads++; return ValueTask.FromResult<SecretMaterial?>(new(Material));
        }
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => throw new InvalidOperationException("Wrong preflight read material.");
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
    }
}
