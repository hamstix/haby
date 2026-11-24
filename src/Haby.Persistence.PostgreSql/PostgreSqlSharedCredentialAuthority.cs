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
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        var env = Guid.Parse(environment.ToString());
        var id = Guid.Parse(provider.ToString());
        var rows = await db.SharedCredentialBindings.AsNoTracking().Where(x => x.EnvironmentRecordId == env &&
            x.ProviderInstanceId == id && x.RevisionNumber == revision.Value).OrderBy(x => x.Slot).ToArrayAsync(token);
        var revocations = await db.SharedCredentialRevocations.AsNoTracking().Where(x => x.ProviderInstanceId == id &&
            x.RevisionNumber == revision.Value).ToArrayAsync(token);
        var result = new List<SharedCredentialBinding>();
        var dependencyCount = 0;
        long projectionCharacters = 0;
        foreach (var x in rows)
        {
            var revoked = revocations.SingleOrDefault(r => r.Slot == x.Slot);
            var use = x.Use switch
            {
                "SystemAccess" => SharedCredentialUse.SystemAccess,
                "ApplicationDelivery" => SharedCredentialUse.ApplicationDelivery,
                _ => throw new InvalidDataException("Shared credential use is unavailable.")
            };
            var dependencies = await DependenciesAsync(db, x, 512 - dependencyCount, 1048576 - projectionCharacters, token);
            dependencyCount += dependencies.Count;
            projectionCharacters += dependencies.Sum(d => (long)d.Projection.Length);
            result.Add(new SharedCredentialBinding(new(environment, provider, revision, x.Slot, use,
                new(SecretId.Parse(x.SecretId.ToString()), SecretVersionId.Parse(x.SecretVersionId.ToString()))),
                x.Actor, x.CreatedAt, revoked?.Actor, revoked?.CreatedAt)
            { Dependencies = dependencies });
        }
        return result;
    }

    private static async Task<IReadOnlyList<SharedCredentialDependency>> DependenciesAsync(ManifestCatalogDbContext db,
        SharedCredentialBindingRow binding, int maximum, long characterBudget, CancellationToken token)
    {
        var query = from use in db.ConfigurationSharedUses.AsNoTracking()
                    join document in db.ConfigurationDocuments.AsNoTracking() on use.Document equals document.Id
                    where use.Provider == binding.ProviderInstanceId && use.Revision == binding.RevisionNumber && use.Slot == binding.Slot &&
                        use.Environment == binding.EnvironmentRecordId && use.Secret == binding.SecretId && use.Version == binding.SecretVersionId && use.Use == binding.Use
                    select new { Use = use, document.DeploymentId, document.Generation, document.Component, Name = document.Document };
        if (await query.LongCountAsync(token) > maximum || (await query.SumAsync(x => (long?)x.Use.Projection.Length, token) ?? 0) > characterBudget)
            throw new ConfigurationDocumentLimitException();
        var rows = await query.OrderBy(x => x.Use.Document).ThenBy(x => x.Use.ProjectionHash).Select(x => new
        {
            x.Use.Document,
            x.Use.Application,
            x.Use.Resource,
            x.Use.Association,
            x.Use.Projection,
            x.Use.Formatter,
            x.Use.Implementation,
            x.DeploymentId,
            x.Generation,
            x.Component,
            x.Name,
            Active = db.ResourceManagementHeads.Any(h => h.Management == x.Use.Association && h.Environment == x.Use.Environment &&
                h.Application == x.Use.Application && h.Resource == x.Use.Resource)
        }).ToArrayAsync(token);
        return rows.Select(x => new SharedCredentialDependency(ConfigurationDocumentRevisionId.Parse(x.Document.ToString()),
            new(ApplicationDeploymentId.Parse(x.DeploymentId.ToString()), DeploymentGeneration.Create(x.Generation), ManifestLocalId.Create(x.Component), ManifestLocalId.Create(x.Name)),
            ApplicationRecordId.Parse(x.Application.ToString()), ManifestLocalId.Create(x.Resource), x.Association, x.Projection,
            ConfigurationFormatterId.Create(x.Formatter), ManifestLocalId.Create(x.Implementation), x.Active)).ToArray();
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
        if (!await db.AcquireSharedCredentialGateAsync(env, provider, token)) return SharedCredentialRevocationOutcome.NotFound;
        var row = await ExactAsync(db, scope, token);
        if (row is null) return SharedCredentialRevocationOutcome.NotFound;
        if (await RevokedAsync(db, row, token)) return SharedCredentialRevocationOutcome.AlreadyRevoked;
        var revocation = new SharedCredentialRevocationRow
        {
            ProviderInstanceId = provider,
            RevisionNumber = scope.Revision.Value,
            Slot = scope.Slot,
            Actor = actor,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AuthorizeSharedCredentialRevocation(revocation, scope);
        db.SharedCredentialRevocations.Add(revocation);
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
