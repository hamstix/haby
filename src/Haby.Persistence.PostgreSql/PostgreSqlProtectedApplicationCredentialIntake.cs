using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Private durable input in the explicitly selected plaintext catalog backend.
/// No public endpoint, password digest, normalization, implicit replacement or lost-response cleanup.</summary>
public sealed class PostgreSqlProtectedApplicationCredentialIntake(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IProtectedApplicationCredentialIntake
{
    public async ValueTask<ProtectedCredentialStageResult> StageAsync(Guid requestId, ManagedResourceKey owner,
        SecretMaterial material, CancellationToken cancellationToken)
    {
        var bytes = material.CopyBytes();
        try
        {
            if (requestId == Guid.Empty || !Valid(bytes)) return new(ProtectedCredentialStageStatus.InvalidInput);
            var environment = Guid.Parse(owner.EnvironmentRecordId.ToString());
            var application = Guid.Parse(owner.ApplicationRecordId.ToString());
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            if (!await db.Environments.AnyAsync(x => x.Id == environment, cancellationToken) ||
                !await db.Applications.AnyAsync(x => x.Id == application, cancellationToken))
                return new(ProtectedCredentialStageStatus.InvalidInput);
            var previous = await ReadAsync(db, environment, requestId, owner, bytes, cancellationToken);
            if (previous is not null) return previous;
            var row = new ProtectedApplicationCredentialRow
            {
                Id = Guid.CreateVersion7(),
                Request = requestId,
                Environment = environment,
                Application = application,
                Resource = owner.ResourceLocalId.Value,
                Secret = Guid.CreateVersion7(),
                Version = Guid.CreateVersion7(),
                Material = bytes
            };
            db.ProtectedApplicationCredentials.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new(ProtectedCredentialStageStatus.Staged, Reference(row));
            }
            catch (Exception e) when (e is DbUpdateException or NpgsqlException or IOException)
            {
                await using var reread = await contexts.CreateDbContextAsync(CancellationToken.None);
                return await ReadAsync(reread, environment, requestId, owner, bytes, CancellationToken.None) ??
                    new(ProtectedCredentialStageStatus.StorageUnavailable);
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool Valid(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 65536) return false;
        try
        {
            var chars = new UTF8Encoding(false, true).GetChars(bytes);
            try { return !chars.Contains('\0'); }
            finally { Array.Clear(chars); }
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static async Task<ProtectedCredentialStageResult?> ReadAsync(ManifestCatalogDbContext db, Guid environment,
        Guid request, ManagedResourceKey owner, byte[] bytes, CancellationToken token)
    {
        var application = Guid.Parse(owner.ApplicationRecordId.ToString());
        var rows = await db.ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Request == request ||
            x.Environment == environment && x.Application == application && x.Resource == owner.ResourceLocalId.Value).Take(2).ToArrayAsync(token);
        if (rows.Length > 1) { foreach (var item in rows) CryptographicOperations.ZeroMemory(item.Material); return new(ProtectedCredentialStageStatus.Conflict); }
        var row = rows.SingleOrDefault();
        if (row is null) return null;
        try
        {
            return row.Request == request && row.Environment == environment && row.Application == application && row.Resource == owner.ResourceLocalId.Value &&
                CryptographicOperations.FixedTimeEquals(row.Material, bytes)
                ? new(ProtectedCredentialStageStatus.Existing, Reference(row)) : new(ProtectedCredentialStageStatus.Conflict);
        }
        finally { CryptographicOperations.ZeroMemory(row.Material); }
    }

    public async ValueTask<SecretMaterial?> ResolveStagedAsync(ProtectedCredentialReference reference,
        ManagedResourceKey owner, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var environment = Guid.Parse(owner.EnvironmentRecordId.ToString());
        var application = Guid.Parse(owner.ApplicationRecordId.ToString());
        var row = await db.ProtectedApplicationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reference.Id &&
            x.Environment == environment && x.Application == application && x.Resource == owner.ResourceLocalId.Value, cancellationToken);
        if (row is null) return null;
        try { return Reference(row) == reference && Valid(row.Material) ? new(row.Material) : null; }
        finally { CryptographicOperations.ZeroMemory(row.Material); }
    }

    internal static ProtectedCredentialReference Reference(ProtectedApplicationCredentialRow row) =>
        new(row.Id, new(SecretId.Parse(row.Secret.ToString()), SecretVersionId.Parse(row.Version.ToString())));
}
