using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Exact, uncached authorization and append-only per-use revocation.</summary>
public sealed class PostgreSqlSharedCredentialAuthority(IDbContextFactory<ManifestCatalogDbContext> contexts,
    ISecretStore secrets) : ISharedCredentialAdministration, ISharedCredentialSystemAccess
{
    public async ValueTask<IReadOnlyList<SharedCredentialBinding>> InspectAsync(EnvironmentRecordId environment,
        ProviderInstanceId provider, ProviderConfigurationRevisionNumber revision, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var env = Guid.Parse(environment.ToString());
        var id = Guid.Parse(provider.ToString());
        var rows = await db.SharedCredentialBindings.AsNoTracking().Where(x => x.EnvironmentRecordId == env &&
            x.ProviderInstanceId == id && x.RevisionNumber == revision.Value).OrderBy(x => x.Slot).ToArrayAsync(token);
        var revocations = await db.SharedCredentialRevocations.AsNoTracking().Where(x => x.ProviderInstanceId == id &&
            x.RevisionNumber == revision.Value).ToArrayAsync(token);
        return rows.Select(x =>
        {
            var revoked = revocations.SingleOrDefault(r => r.Slot == x.Slot);
            var use = x.Use switch
            {
                "SystemAccess" => SharedCredentialUse.SystemAccess,
                "ApplicationDelivery" => SharedCredentialUse.ApplicationDelivery,
                _ => throw new InvalidDataException("Shared credential use is unavailable.")
            };
            return new SharedCredentialBinding(new(environment, provider, revision, x.Slot, use,
                new(SecretId.Parse(x.SecretId.ToString()), SecretVersionId.Parse(x.SecretVersionId.ToString()))),
                x.Actor, x.CreatedAt, revoked?.Actor, revoked?.CreatedAt);
        }).ToArray();
    }

    public async ValueTask<SharedCredentialRevocationOutcome> RevokeAsync(SharedCredentialScope scope, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Valid(scope) || string.IsNullOrWhiteSpace(actor) || actor.Length > 256)
            return SharedCredentialRevocationOutcome.InvalidRequest;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var env = Guid.Parse(scope.Environment.ToString());
        var provider = Guid.Parse(scope.Provider.ToString());
        // The same provider lock serializes issuance and every per-use revocation.
        if (await db.ProviderInstances.FromSqlInterpolated($"SELECT * FROM provider_instances WHERE id = {provider} AND environment_record_id = {env} FOR UPDATE")
                .SingleOrDefaultAsync(token) is null) return SharedCredentialRevocationOutcome.NotFound;
        var row = await ExactAsync(db, scope, token);
        if (row is null) return SharedCredentialRevocationOutcome.NotFound;
        if (await RevokedAsync(db, row, token)) return SharedCredentialRevocationOutcome.AlreadyRevoked;
        db.SharedCredentialRevocations.Add(new()
        {
            ProviderInstanceId = provider,
            RevisionNumber = scope.Revision.Value,
            Slot = scope.Slot,
            Actor = actor,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(token);
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return SharedCredentialRevocationOutcome.Revoked;
    }

    public async ValueTask<SecretMaterial?> ResolveAsync(SharedCredentialScope scope, int maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Valid(scope) || scope.Use != SharedCredentialUse.SystemAccess || maximumBytes is < 1 or > SecretAdministration.MaxBytes) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await ExactAsync(db, scope, token);
        if (row is null || await RevokedAsync(db, row, token)) return null;
        // Authorization precedes material access. An already authorized in-flight operation may complete.
        return await secrets.ResolveBoundedAsync(new(new SecretOwner.Environment(scope.Environment), SecretPurpose.SharedApplicationCredential),
            scope.Reference, maximumBytes, token);
    }
    private static bool Valid(SharedCredentialScope scope) => scope is
    {
        Environment: not null, Provider: not null,
        Revision: not null, Reference: not null
    } && !string.IsNullOrWhiteSpace(scope.Slot) && scope.Slot.Length <= 128 &&
        scope.Use is SharedCredentialUse.SystemAccess or SharedCredentialUse.ApplicationDelivery;
    private static Task<bool> RevokedAsync(ManifestCatalogDbContext db, SharedCredentialBindingRow row, CancellationToken token) =>
        db.SharedCredentialRevocations.AsNoTracking().AnyAsync(x => x.ProviderInstanceId == row.ProviderInstanceId &&
            x.RevisionNumber == row.RevisionNumber && x.Slot == row.Slot, token);
    private static Task<SharedCredentialBindingRow?> ExactAsync(ManifestCatalogDbContext db, SharedCredentialScope scope, CancellationToken token)
    {
        var env = Guid.Parse(scope.Environment.ToString());
        var provider = Guid.Parse(scope.Provider.ToString());
        var secret = Guid.Parse(scope.Reference.SecretId.ToString());
        var version = Guid.Parse(scope.Reference.SecretVersionId.ToString());
        var use = scope.Use.ToString();
        return (from binding in db.SharedCredentialBindings.AsNoTracking()
                join revision in db.ProviderConfigurationRevisions.AsNoTracking() on
                    new { binding.ProviderInstanceId, binding.RevisionNumber, binding.EnvironmentRecordId } equals
                    new { revision.ProviderInstanceId, revision.RevisionNumber, revision.EnvironmentRecordId }
                where binding.EnvironmentRecordId == env && binding.ProviderInstanceId == provider &&
                    binding.RevisionNumber == scope.Revision.Value && revision.IsSealed && binding.Slot == scope.Slot &&
                    binding.Use == use && binding.SecretId == secret && binding.SecretVersionId == version
                select binding).SingleOrDefaultAsync(token);
    }
}
