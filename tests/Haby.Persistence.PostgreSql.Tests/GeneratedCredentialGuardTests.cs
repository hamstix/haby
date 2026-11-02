using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class GeneratedCredentialGuardTests
{
    [Fact]
    public async Task Invalid_slot_owner_and_cancellation_are_rejected_without_storage()
    {
        var service = new PostgreSqlGeneratedCredentialSlots(null!);
        var key = new ManagedResourceKey(EnvironmentRecordId.New(), ApplicationRecordId.New(), ManifestLocalId.Create("http-client"));
        foreach (var slot in new GeneratedCredentialSlot[] { null!, new(null!, "application-password"),
                     new(key with { ApplicationRecordId = null! }, "application-password"), new(key, "invalid-slot") })
            Assert.Equal(new(GeneratedCredentialOutcome.InvalidOwner),
                await service.GetOrCreateAsync(slot, GeneratedCredentialSlot.RandomPasswordV1, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetOrCreateAsync(
            new(key, GeneratedCredentialSlot.ApplicationPassword), GeneratedCredentialSlot.RandomPasswordV1,
            new CancellationToken(true)).AsTask());
    }

    [Fact]
    public async Task Storage_outage_is_not_invalid_owner_and_diagnostics_are_bounded()
    {
        var service = new PostgreSqlGeneratedCredentialSlots(new UnavailableFactory());
        var slot = new GeneratedCredentialSlot(new(EnvironmentRecordId.New(), ApplicationRecordId.New(), ManifestLocalId.Create("queue-user")),
            GeneratedCredentialSlot.ApplicationPassword);
        var result = await service.GetOrCreateAsync(slot, GeneratedCredentialSlot.RandomPasswordV1, default);
        Assert.Equal(new(GeneratedCredentialOutcome.StorageUnavailable), result);
        Assert.DoesNotContain("private", result.ToString());
    }

    [Fact]
    public async Task Only_explicit_supported_owner_purpose_combinations_are_accepted()
    {
        var env = EnvironmentRecordId.New();
        var resource = new SecretOwner.Resource(new(env, ApplicationRecordId.New(), ManifestLocalId.Create("queue-user")));
        Assert.True(new SecretDefinition(new SecretOwner.Environment(env), SecretPurpose.ProviderAdministrator).IsSupported);
        Assert.True(new SecretDefinition(resource, SecretPurpose.ApplicationLogin).IsSupported);
        var store = new PostgreSqlPlaintextSecretStore(null!);
        foreach (var definition in new[] { new SecretDefinition(resource, SecretPurpose.ProviderAdministrator),
                     new(new SecretOwner.Environment(env), SecretPurpose.ApplicationLogin), new(resource, (SecretPurpose)99),
                     new(null!, SecretPurpose.ProviderAdministrator) })
        {
            Assert.False(definition.IsSupported);
            await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(definition,
                new(SecretId.New(), SecretVersionId.New()), [1], default).AsTask());
        }
        // Resource producers cannot bypass slot/association/audit atomic acceptance.
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(new(resource, SecretPurpose.ApplicationLogin),
            new(SecretId.New(), SecretVersionId.New()), [1], default).AsTask());
    }

    private sealed class UnavailableFactory : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => throw new IOException("private-storage-diagnostic");
    }
}
