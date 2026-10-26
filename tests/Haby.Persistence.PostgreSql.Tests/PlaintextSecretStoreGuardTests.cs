using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class PlaintextSecretStoreGuardTests
{
    [Fact]
    public void Unsupported_protection_format_fails_without_echoing_stored_metadata()
    {
        PlaintextProtectionFormat.ValidateForRead("Plaintext", 1);

        foreach (var (format, version) in new[] { ("Unknown-sensitive-marker", 1), ("Plaintext", 2) })
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                PlaintextProtectionFormat.ValidateForRead(format, version));
            Assert.Equal("Unsupported stored secret protection format.", error.Message);
            Assert.DoesNotContain("sensitive-marker", error.ToString());
        }
    }

    [Fact]
    public async Task Null_arguments_are_rejected_before_storage_access()
    {
        var store = new PostgreSqlPlaintextSecretStore(null!);
        var environment = EnvironmentRecordId.New();
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.CreateEnvironmentAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.CreateAsync(null!, reference, [], default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.AddVersionAsync(environment, null!, [], default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.CreateAsync(environment, reference, null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.ResolveAsync(null!, reference, default));
    }
}
