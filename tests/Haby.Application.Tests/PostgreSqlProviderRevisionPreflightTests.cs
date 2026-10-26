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

    [Fact]
    public async Task Unexpected_storage_exceptions_do_not_cross_preflight_boundary()
    {
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var environment = EnvironmentRecordId.New();
        var binding = new ProviderAliasBinding(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1));
        var brokenRevision = new PostgreSqlProviderRevisionPreflight(
            new InterruptingRevisionStore(false, reference, new ObjectDisposedException("private-host-value")),
            new InterruptingSecretStore());
        var brokenSecret = new PostgreSqlProviderRevisionPreflight(
            new InterruptingRevisionStore(true, reference),
            new InterruptingSecretStore(new Exception("private-user-value")));

        var revisionResult = await brokenRevision.CheckAsync(environment, binding, default);
        var secretResult = await brokenSecret.CheckAsync(environment, binding, default);

        Assert.Equal(ProviderPreflightOutcome.ConfigurationUnavailable, revisionResult.Outcome);
        Assert.Equal(ProviderPreflightOutcome.SecretUnavailable, secretResult.Outcome);
        Assert.DoesNotContain("private-host-value", revisionResult.ToString());
        Assert.DoesNotContain("private-user-value", secretResult.ToString());
    }

    [Fact]
    public void Disposed_secret_material_cannot_be_copied()
    {
        var source = new byte[] { 1, 2, 3 };
        var material = new SecretMaterial(source);
        material.Dispose();

        Assert.Throws<ObjectDisposedException>(() => material.CopyBytes());
        Assert.Equal(new byte[] { 1, 2, 3 }, source);
    }

    private sealed class InterruptingRevisionStore(bool available, SecretRef reference, Exception? error = null)
        : IProviderConfigurationStore
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
            if (!available) throw error ?? new OperationCanceledException("private storage diagnostic");
            var configuration = new PostgreSqlProviderConfiguration(1, "localhost", 5432, "postgres",
                "administrator", reference, PostgreSqlDatabaseProvisioner.ImplementationId);
            return ValueTask.FromResult<ProviderConfigurationRevision<TConfiguration>?>(
                new(instanceId, revision, environment, (TConfiguration)(object)configuration));
        }
    }

    private sealed class InterruptingSecretStore(Exception? error = null) : ISecretStore
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
            throw error ?? new OperationCanceledException("private secret diagnostic");
        }
    }
}
