using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;

namespace Haby.PostgreSql.IntegrationTests;

/// <summary>Seeds the historical schema without using the current EF model's newly added columns.</summary>
internal static class LegacySecretFixture
{
    internal static async Task CreateAsync(IDbContextFactory<ManifestCatalogDbContext> factory,
        EnvironmentRecordId environment, SecretRef reference, byte[] material)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var env = Guid.Parse(environment.ToString());
        var secret = Guid.Parse(reference.SecretId.ToString());
        var version = Guid.Parse(reference.SecretVersionId.ToString());
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO secrets (id, environment_record_id, owner, purpose, sensitivity, content_type, storage_backend, ownership_policy) VALUES ({secret}, {env}, 'Environment', 'ProviderAdministrator', 'Credential', 'application/octet-stream', 'PostgreSqlPlaintext', 'Managed')");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO secret_versions (id, secret_id, protection_format, format_version) VALUES ({version}, {secret}, 'Plaintext', 1)");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO secret_payloads (secret_version_id, bytes) VALUES ({version}, {material})");
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE secrets SET current_version_id = {version} WHERE id = {secret}");
        await transaction.CommitAsync();
    }
}
