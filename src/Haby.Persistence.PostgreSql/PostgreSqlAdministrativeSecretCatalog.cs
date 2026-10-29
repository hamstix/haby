using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>EF owns the complete workflow transaction; ISecretStore is used only for exact-version reads.</summary>
public sealed class PostgreSqlAdministrativeSecretCatalog(
    IDbContextFactory<ManifestCatalogDbContext> contexts, ISecretStore store) : IAdministrativeSecretCatalog
{
    private const string Operation = "CreateAdministrativeSecret";

    public async ValueTask<SecretResult> CreateAsync(EnvironmentRecordId environment, string name, Guid requestId,
        SecretMaterial material, string actor, CancellationToken cancellationToken)
    {
        var bytes = material.CopyBytes();
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var env = Guid.Parse(environment.ToString());
            // EF cannot express row-lock modes. Lock the existing Environment to serialize absent-name
            // and absent-receipt races together, including one RequestId racing with different names.
            var owner = await context.Environments.FromSqlInterpolated($"SELECT * FROM environments WHERE id = {env} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (owner is null) return new(SecretOutcome.EnvironmentNotFound, RequestId: requestId);
            var receipt = await context.SecretCreationReceipts.SingleOrDefaultAsync(x =>
                x.EnvironmentRecordId == env && x.Operation == Operation && x.RequestId == requestId, cancellationToken);
            if (receipt is not null && (receipt.Name != name || receipt.Actor != actor))
                return new(SecretOutcome.RequestContentConflict, RequestId: requestId);
            var secret = receipt is null
                ? await context.Secrets.SingleOrDefaultAsync(x => x.EnvironmentRecordId == env && x.Name == name, cancellationToken)
                : await context.Secrets.SingleOrDefaultAsync(x => x.EnvironmentRecordId == env && x.Id == receipt.SecretId, cancellationToken);
            var created = secret is null && receipt is null;
            SecretVersionRow? version;
            var clock = DateTimeOffset.UtcNow;
            // PostgreSQL timestamps have microsecond precision; return exactly what retries will read.
            var now = new DateTimeOffset(clock.Ticks - clock.Ticks % 10, TimeSpan.Zero);
            if (created)
            {
                // The registered backend must explicitly support transactional PostgreSQL plaintext writes.
                // Never compose the independently committing ISecretStore.CreateAsync operation here.
                if (store is not PostgreSqlPlaintextSecretStore)
                    return new(SecretOutcome.SecretStorageUnavailable, RequestId: requestId);
                secret = new SecretRow
                {
                    Id = Guid.Parse(SecretId.New().ToString()),
                    EnvironmentRecordId = env,
                    Name = name,
                    CreatedAt = now,
                    Owner = "Environment",
                    Purpose = "ProviderAdministrator",
                    Sensitivity = "Credential",
                    ContentType = "application/octet-stream",
                    OwnershipPolicy = "Managed",
                    StorageBackend = "PostgreSqlPlaintext"
                };
                version = new SecretVersionRow
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
                context.SecretPayloads.Add(new SecretPayloadRow { SecretVersionId = version.Id, Bytes = bytes });
                await context.SaveChangesAsync(cancellationToken);
                secret.CurrentVersionId = version.Id;
            }
            else
            {
                if (secret is null) return new(SecretOutcome.SecretStorageUnavailable, RequestId: requestId);
                if (secret.Name != name || secret.Owner != "Environment" || secret.Purpose != "ProviderAdministrator" ||
                    secret.Sensitivity != "Credential" || secret.ContentType != "application/octet-stream" || secret.OwnershipPolicy != "Managed")
                    return new(SecretOutcome.RequestContentConflict, RequestId: requestId);
                version = await context.SecretVersions.SingleOrDefaultAsync(x => x.SecretId == secret.Id &&
                    (receipt == null ? x.Number == 1 : x.Id == receipt.VersionId), cancellationToken);
                if (version is null) return new(SecretOutcome.SecretStorageUnavailable, RequestId: requestId);
                var reference = new SecretRef(SecretId.Parse(secret.Id.ToString()), SecretVersionId.Parse(version.Id.ToString()));
                using var accepted = store is PostgreSqlPlaintextSecretStore postgres
                    ? await postgres.ResolveAsync(context, environment, reference, cancellationToken)
                    : await store.ResolveAsync(environment, reference, cancellationToken);
                if (accepted is null) return new(SecretOutcome.SecretStorageUnavailable, RequestId: requestId);
                var original = accepted.CopyBytes();
                try
                {
                    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bytes, original))
                        return new(SecretOutcome.RequestContentConflict, RequestId: requestId);
                }
                finally { Array.Clear(original); }
            }
            if (receipt is null) context.SecretCreationReceipts.Add(new SecretCreationReceiptRow
            {
                EnvironmentRecordId = env,
                Operation = Operation,
                RequestId = requestId,
                Name = name,
                SecretId = secret!.Id,
                VersionId = version.Id,
                Actor = actor,
                CreatedAt = now
            });
            await context.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Once commit begins the outcome may be uncertain. Durable RequestId resolves it on retry.
            await transaction.CommitAsync(CancellationToken.None);
            return new(created ? SecretOutcome.Created : SecretOutcome.ExistingEquivalent, Metadata(secret!, [version]), requestId);
        }
        catch (Exception error) when (error is DbUpdateException or NpgsqlException or InvalidDataException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(SecretOutcome.SecretStorageUnavailable, RequestId: requestId);
        }
        finally { Array.Clear(bytes); }
    }

    public async ValueTask<SecretMetadata?> GetAsync(EnvironmentRecordId environment, string? name, SecretId? id, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var env = Guid.Parse(environment.ToString());
        var identity = id is null ? (Guid?)null : Guid.Parse(id.ToString());
        var row = await context.Secrets.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == env &&
            (identity == null ? x.Name == name : x.Id == identity), cancellationToken);
        if (row is null) return null;
        // Discovery reports the initial numbered version only. Legacy versions remain explicitly unnumbered.
        var versions = await context.SecretVersions.AsNoTracking().Where(x => x.SecretId == row.Id &&
            (x.Number == 1 || (row.Name == null && x.Id == row.CurrentVersionId))).ToArrayAsync(cancellationToken);
        return Metadata(row, versions);
    }

    public async ValueTask<IReadOnlyList<SecretMetadata>> ListAsync(EnvironmentRecordId environment, string? after, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > SecretAdministration.MaxPageSize + 1) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var env = Guid.Parse(environment.ToString());
        var query = context.Secrets.AsNoTracking().Where(x => x.EnvironmentRecordId == env);
        if (after is not null) query = query.Where(x => string.Compare(EF.Functions.Collate(x.Name ?? "~" + x.Id.ToString(), "C"), after) > 0);
        var rows = await query.OrderBy(x => EF.Functions.Collate(x.Name ?? "~" + x.Id.ToString(), "C")).Take(limit).ToArrayAsync(cancellationToken);
        var ids = rows.Select(x => x.Id).ToArray();
        var current = rows.Where(x => x.Name == null).Select(x => x.CurrentVersionId).ToArray();
        var versions = await context.SecretVersions.AsNoTracking().Where(x => ids.Contains(x.SecretId) &&
            (x.Number == 1 || current.Contains(x.Id))).ToArrayAsync(cancellationToken);
        return rows.Select(x => Metadata(x, versions.Where(v => v.SecretId == x.Id).ToArray())).ToArray();
    }

    private static SecretMetadata Metadata(SecretRow row, IReadOnlyList<SecretVersionRow> versions) => new(
        EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString()), SecretId.Parse(row.Id.ToString()), row.Name,
        row.Purpose, row.CreatedAt, versions.Select(v => new SecretVersionMetadata(SecretVersionId.Parse(v.Id.ToString()), v.Number, v.CreatedAt)).ToArray());
}
