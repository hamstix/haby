using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlProviderRevisionPreflightTests
{
    [Theory]
    [InlineData(false, ProviderPreflightOutcome.ConfigurationUnavailable)]
    [InlineData(true, ProviderPreflightOutcome.SecretUnavailable)]
    public async Task Interrupted_storage_read_returns_value_free_outcome(bool revisionAvailable,
        ProviderPreflightOutcome expected)
    {
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var environment = EnvironmentRecordId.New();
        var binding = new ProviderAliasBinding(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1));
        var store = new InterruptingRevisionStore(revisionAvailable, reference);
        var secrets = new InterruptingSecretStore();
        var preflight = new PostgreSqlProviderRevisionPreflight(store, secrets);

        var outcome = await preflight.CheckAsync(environment, binding, default);

        Assert.Equal(expected, outcome.Outcome);
        Assert.Equal(revisionAvailable, secrets.ResolveCalled);
        Assert.Equal(nameof(ProviderPreflightOutcome), outcome.Outcome.GetType().Name);
    }

    private sealed class InterruptingRevisionStore(bool available, SecretRef reference) : IProviderConfigurationStore
    {
        public ValueTask<ProviderInstanceId> CreateInstanceAsync<TConfiguration>(EnvironmentRecordId environment,
            TConfiguration configuration, CancellationToken cancellationToken) where TConfiguration : class =>
            throw new NotSupportedException();

        public ValueTask AddRevisionAsync<TConfiguration>(EnvironmentRecordId environment,
            ProviderInstanceId instanceId, ProviderConfigurationRevisionNumber revision,
            TConfiguration configuration, CancellationToken cancellationToken) where TConfiguration : class =>
            throw new NotSupportedException();

        public ValueTask<ProviderConfigurationRevision<TConfiguration>?> FindRevisionAsync<TConfiguration>(
            EnvironmentRecordId environment, ProviderInstanceId instanceId,
            ProviderConfigurationRevisionNumber revision, CancellationToken cancellationToken)
            where TConfiguration : class
        {
            if (!available) throw new OperationCanceledException("private storage diagnostic");
            var configuration = new PostgreSqlProviderConfiguration(1, "localhost", 5432, "postgres",
                "administrator", reference, PostgreSqlDatabaseProvisioner.ImplementationId);
            return ValueTask.FromResult<ProviderConfigurationRevision<TConfiguration>?>(
                new(instanceId, revision, environment, (TConfiguration)(object)configuration));
        }
    }

    private sealed class InterruptingSecretStore : ISecretStore
    {
        public bool ResolveCalled { get; private set; }
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask CreateAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<SecretMaterial?> ResolveAsync(EnvironmentRecordId environment, SecretRef reference,
            CancellationToken cancellationToken)
        {
            ResolveCalled = true;
            throw new OperationCanceledException("private secret diagnostic");
        }
    }
}
