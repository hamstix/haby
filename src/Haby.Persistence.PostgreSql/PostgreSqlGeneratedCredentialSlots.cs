using System.Security.Cryptography;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Local catalog work only. No Provider resolution, registration or external network effects.</summary>
public sealed class PostgreSqlGeneratedCredentialSlots(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IGeneratedCredentialSlots
{
    internal const string TechnicalActor = "haby.generated-credential-slots/v1";

    public async ValueTask<GeneratedCredentialResult> GetOrCreateAsync(GeneratedCredentialSlot slot,
        string generationProfile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (slot?.Owner is not { EnvironmentRecordId: not null, ApplicationRecordId: not null, ResourceLocalId: not null } ||
            slot.SlotId != GeneratedCredentialSlot.ApplicationPassword)
            return new(GeneratedCredentialOutcome.InvalidOwner);
        var env = Guid.Parse(slot.Owner.EnvironmentRecordId.ToString());
        var app = Guid.Parse(slot.Owner.ApplicationRecordId.ToString());
        var resource = slot.Owner.ResourceLocalId.Value;
        byte[]? bytes = null;
        var committing = false;
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await context.Environments.AnyAsync(x => x.Id == env, cancellationToken) ||
                !await context.Applications.AnyAsync(x => x.Id == app, cancellationToken))
                return new(GeneratedCredentialOutcome.InvalidOwner);
            var existing = await ReadAsync(context, slot, generationProfile, cancellationToken);
            if (existing is not null) return existing;
            // A lost association is not permission to replace retained Resource material.
            if (await context.Secrets.AnyAsync(x => x.EnvironmentRecordId == env && x.ApplicationRecordId == app &&
                x.ResourceLocalId == resource, cancellationToken))
                // Under Read Committed, a winner may have committed since the absent-slot read.
                // Distinguish that complete winner from a genuinely orphaned Secret without creating a candidate.
                return await ReadAsync(context, slot, generationProfile, cancellationToken) ??
                    new(GeneratedCredentialOutcome.MaterialUnavailable);
            if (generationProfile != GeneratedCredentialSlot.RandomPasswordV1)
                return new(GeneratedCredentialOutcome.ProfileConflict);

            bytes = Generate();
            var secret = SecretRowMapping.Create(new(new SecretOwner.Resource(slot.Owner), SecretPurpose.ApplicationLogin),
                Guid.Parse(SecretId.New().ToString()));
            var clock = DateTimeOffset.UtcNow;
            var now = new DateTimeOffset(clock.Ticks - clock.Ticks % 10, TimeSpan.Zero);
            secret.CreatedAt = now;
            var version = new SecretVersionRow
            {
                Id = Guid.Parse(SecretVersionId.New().ToString()),
                SecretId = secret.Id,
                Number = 1,
                CreatedAt = now,
                ProtectionFormat = PlaintextProtectionFormat.Name,
                FormatVersion = PlaintextProtectionFormat.Version
            };
            context.Secrets.Add(secret);
            context.SecretVersions.Add(version);
            context.SecretPayloads.Add(new() { SecretVersionId = version.Id, Bytes = bytes });
            await context.SaveChangesAsync(cancellationToken);
            secret.CurrentVersionId = version.Id;
            // Establish the exact current version before the association's insert guard runs.
            await context.SaveChangesAsync(cancellationToken);
            context.GeneratedCredentialSlots.Add(new()
            {
                EnvironmentRecordId = env,
                ApplicationRecordId = app,
                ResourceLocalId = resource,
                SlotId = slot.SlotId,
                GenerationProfile = generationProfile,
                SecretId = secret.Id,
                VersionId = version.Id
            });
            context.GeneratedCredentialCreationAudits.Add(new()
            {
                SecretId = secret.Id,
                TechnicalActor = TechnicalActor,
                CreatedAt = now
            });
            await context.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Cancellation is honored up to commit. Once started, confirm via the same natural key.
            committing = true;
            await transaction.CommitAsync(CancellationToken.None);
            return new(GeneratedCredentialOutcome.Created, Reference(secret.Id, version.Id));
        }
        catch (Exception error) when (StorageFailure(error) || (committing && error is OperationCanceledException))
        {
            if (!committing) cancellationToken.ThrowIfCancellationRequested();
            // The candidate transaction/context have been disposed before this independent reread.
            // Never attempt a second creation in this invocation, even if no winner is visible.
            try
            {
                var confirmationToken = committing ? CancellationToken.None : cancellationToken;
                await using var context = await contexts.CreateDbContextAsync(confirmationToken);
                return await ReadAsync(context, slot, generationProfile, confirmationToken) ??
                    new(GeneratedCredentialOutcome.StorageUnavailable);
            }
            catch (Exception reread) when (StorageFailure(reread))
            {
                return new(GeneratedCredentialOutcome.StorageUnavailable);
            }
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool StorageFailure(Exception error) =>
        error is DbUpdateException or NpgsqlException or InvalidOperationException or IOException or TimeoutException;

    internal static async Task<GeneratedCredentialResult?> ReadAsync(ManifestCatalogDbContext context,
        GeneratedCredentialSlot slot, string profile, CancellationToken cancellationToken, bool validateMaterial = true)
    {
        var env = Guid.Parse(slot.Owner.EnvironmentRecordId.ToString());
        var app = Guid.Parse(slot.Owner.ApplicationRecordId.ToString());
        var resource = slot.Owner.ResourceLocalId.Value;
        var row = await context.GeneratedCredentialSlots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.EnvironmentRecordId == env && x.ApplicationRecordId == app && x.ResourceLocalId == resource &&
            x.SlotId == slot.SlotId, cancellationToken);
        if (row is null) return null;
        if (row.GenerationProfile != profile) return new(GeneratedCredentialOutcome.ProfileConflict);
        if (row.GenerationProfile != GeneratedCredentialSlot.RandomPasswordV1)
            return new(GeneratedCredentialOutcome.MaterialUnavailable);
        if (!await context.Environments.AnyAsync(x => x.Id == env, cancellationToken) ||
            !await context.Applications.AnyAsync(x => x.Id == app, cancellationToken))
            return new(GeneratedCredentialOutcome.MaterialUnavailable);
        var secret = await context.Secrets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.SecretId, cancellationToken);
        var version = await context.SecretVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.VersionId, cancellationToken);
        var audit = await context.GeneratedCredentialCreationAudits.AsNoTracking().SingleOrDefaultAsync(x => x.SecretId == row.SecretId, cancellationToken);
        if (secret is null || secret.EnvironmentRecordId != env || secret.ApplicationRecordId != app ||
            secret.ResourceLocalId != resource || secret.Owner != "Resource" || secret.Purpose != "ApplicationLogin" ||
            secret.Sensitivity != "Credential" || secret.ContentType != "application/octet-stream" ||
            secret.OwnershipPolicy != "Managed" || secret.StorageBackend != "PostgreSqlPlaintext" || secret.Name is not null ||
            secret.CurrentVersionId != row.VersionId || version is null || version.SecretId != row.SecretId ||
            version.Number != 1 || version.ProtectionFormat != PlaintextProtectionFormat.Name ||
            version.FormatVersion != PlaintextProtectionFormat.Version || audit is null ||
            audit.TechnicalActor != TechnicalActor || audit.CreatedAt != secret.CreatedAt || version.CreatedAt != secret.CreatedAt)
            return new(GeneratedCredentialOutcome.MaterialUnavailable);
        if (!validateMaterial)
            return await context.SecretPayloads.AsNoTracking().AnyAsync(x => x.SecretVersionId == row.VersionId && x.Bytes.Length == 43, cancellationToken)
                ? new(GeneratedCredentialOutcome.Existing, Reference(row.SecretId, row.VersionId)) : new(GeneratedCredentialOutcome.MaterialUnavailable);
        var payload = await context.SecretPayloads.AsNoTracking().SingleOrDefaultAsync(x => x.SecretVersionId == row.VersionId, cancellationToken);
        if (payload is null) return new(GeneratedCredentialOutcome.MaterialUnavailable);
        try
        {
            if (!ValidMaterial(payload.Bytes)) return new(GeneratedCredentialOutcome.MaterialUnavailable);
            try { return new(GeneratedCredentialOutcome.Existing, Reference(row.SecretId, row.VersionId)); }
            catch (ArgumentException) { return new(GeneratedCredentialOutcome.MaterialUnavailable); }
            catch (FormatException) { return new(GeneratedCredentialOutcome.MaterialUnavailable); }
        }
        finally { CryptographicOperations.ZeroMemory(payload.Bytes); }
    }

    private static SecretRef Reference(Guid secret, Guid version) =>
        new(SecretId.Parse(secret.ToString()), SecretVersionId.Parse(version.ToString()));

    private static byte[] Generate()
    {
        Span<byte> random = stackalloc byte[32];
        Span<char> encoded = stackalloc char[44];
        try
        {
            RandomNumberGenerator.Fill(random);
            Convert.TryToBase64Chars(random, encoded, out _);
            var bytes = new byte[43];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = encoded[i] switch
            {
                '+' => (byte)'-',
                '/' => (byte)'_',
                _ => (byte)encoded[i]
            };
            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
            encoded.Clear();
        }
    }

    private static bool ValidMaterial(byte[] bytes) => bytes.Length == 43 && bytes.All(b =>
        b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_') &&
        // 32 bytes leave two zero pad bits in the final base64url character.
        "AEIMQUYcgkosw048".Contains((char)bytes[^1], StringComparison.Ordinal);
}
