using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    private (Guid Transaction, Guid Environment, Guid Provider)? _sharedCredentialGate;
    private readonly Dictionary<SharedCredentialBindingRow, BindingPermit> _sharedBindingPermits = [];
    private readonly Dictionary<SharedCredentialRevocationRow, RevocationPermit> _sharedRevocationPermits = [];

    // Acquire, rather than merely assert, the lock that serializes all supported shared writers.
    // A new Provider has no row yet; the already owning Environment lock covers absent-name races.
    internal async Task<bool> AcquireSharedCredentialGateAsync(Guid environment, Guid provider, CancellationToken token)
    {
        var transaction = Database.CurrentTransaction ?? throw new InvalidOperationException("Shared credential writes require a transaction.");
        var added = ChangeTracker.Entries<ProviderInstanceRow>().Any(e => e.State == EntityState.Added &&
            e.Entity.Id == provider && e.Entity.EnvironmentRecordId == environment);
        var found = added
            ? await Environments.FromSqlInterpolated($"SELECT * FROM environments WHERE id = {environment} FOR UPDATE").SingleOrDefaultAsync(token) is not null
            : await ProviderInstances.FromSqlInterpolated($"SELECT * FROM provider_instances WHERE id = {provider} AND environment_record_id = {environment} FOR UPDATE").SingleOrDefaultAsync(token) is not null;
        if (found) _sharedCredentialGate = (transaction.TransactionId, environment, provider);
        return found;
    }
    private bool HoldsSharedCredentialGate(Guid environment, Guid provider) => Database.CurrentTransaction is { } transaction &&
        _sharedCredentialGate == (transaction.TransactionId, environment, provider);

    internal void AuthorizeSharedCredentialBinding(SharedCredentialBindingRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Actor) || row.Actor.Length > 256 ||
            row.Use is not ("SystemAccess" or "ApplicationDelivery") ||
            !HoldsSharedCredentialGate(row.EnvironmentRecordId, row.ProviderInstanceId))
            throw new InvalidOperationException("Shared credential issuance has no owning transaction.");
        _sharedBindingPermits.Add(row, BindingPermit.From(row, Database.CurrentTransaction!.TransactionId));
    }
    internal void AuthorizeSharedCredentialRevocation(SharedCredentialRevocationRow row, SharedCredentialScope scope)
    {
        var environment = Guid.Parse(scope.Environment.ToString());
        if (string.IsNullOrWhiteSpace(row.Actor) || row.Actor.Length > 256 ||
            row.ProviderInstanceId != Guid.Parse(scope.Provider.ToString()) || row.RevisionNumber != scope.Revision.Value || row.Slot != scope.Slot ||
            !HoldsSharedCredentialGate(environment, row.ProviderInstanceId))
            throw new InvalidOperationException("Shared credential revocation has no owning transaction.");
        _sharedRevocationPermits.Add(row, new(Database.CurrentTransaction!.TransactionId, row.ProviderInstanceId, row.RevisionNumber, row.Slot, row.Actor, row.CreatedAt,
            environment, Guid.Parse(scope.Reference.SecretId.ToString()), Guid.Parse(scope.Reference.SecretVersionId.ToString()), scope.Use.ToString()));
    }

    private async Task ValidateSharedCredentialWritesAsync(bool asynchronous, CancellationToken token)
    {
        if (ChangeTracker.Entries<SharedCredentialBindingRow>().Any(e => e.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<SharedCredentialRevocationRow>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Shared credential binding and revocation history is immutable.");
        foreach (var entry in ChangeTracker.Entries<SharedCredentialBindingRow>().Where(e => e.State == EntityState.Added))
        {
            var row = entry.Entity;
            if (!HoldsSharedCredentialGate(row.EnvironmentRecordId, row.ProviderInstanceId) ||
                !_sharedBindingPermits.TryGetValue(row, out var permit) || permit != BindingPermit.From(row, Database.CurrentTransaction!.TransactionId))
                throw new InvalidOperationException("Shared credential issuance has no exact owning acceptance scope.");
            var revision = ChangeTracker.Entries<ProviderConfigurationRevisionRow>().SingleOrDefault(e => e.State == EntityState.Added &&
                e.Entity.ProviderInstanceId == row.ProviderInstanceId && e.Entity.RevisionNumber == row.RevisionNumber &&
                e.Entity.EnvironmentRecordId == row.EnvironmentRecordId && !e.Entity.IsSealed);
            var reference = ChangeTracker.Entries<ProviderRevisionSecretRefRow>().SingleOrDefault(e => e.State == EntityState.Added &&
                e.Entity.ProviderInstanceId == row.ProviderInstanceId && e.Entity.RevisionNumber == row.RevisionNumber && e.Entity.Slot == row.Slot &&
                e.Entity.EnvironmentRecordId == row.EnvironmentRecordId && e.Entity.SecretId == row.SecretId && e.Entity.SecretVersionId == row.SecretVersionId);
            var existingQuery = ProviderConfigurationRevisions.AsNoTracking().Where(x => x.ProviderInstanceId == row.ProviderInstanceId && x.RevisionNumber == row.RevisionNumber);
            var existing = asynchronous ? await existingQuery.AnyAsync(token) : existingQuery.Any();
            if (revision is null || reference is null || existing)
                throw new InvalidOperationException("Shared credential issuance requires a new unsealed revision and exact reference.");
            var metadata = from secret in Secrets.AsNoTracking()
                           join version in SecretVersions.AsNoTracking() on secret.Id equals version.SecretId
                           where secret.Id == row.SecretId && secret.EnvironmentRecordId == row.EnvironmentRecordId &&
                               secret.Owner == "Environment" && secret.Purpose == "SharedApplicationCredential" && secret.Name != null &&
                               secret.ApplicationRecordId == null && secret.ResourceLocalId == null && version.Id == row.SecretVersionId
                           select version.Id;
            if (!(asynchronous ? await metadata.AnyAsync(token) : metadata.Any()))
                throw new InvalidOperationException("Shared credential issuance requires exact named shared-purpose metadata.");
        }
        foreach (var entry in ChangeTracker.Entries<SharedCredentialRevocationRow>().Where(e => e.State == EntityState.Added))
        {
            var row = entry.Entity;
            if (!_sharedRevocationPermits.TryGetValue(row, out var permit) ||
                !HoldsSharedCredentialGate(permit.Environment, row.ProviderInstanceId) || permit.Transaction != Database.CurrentTransaction!.TransactionId ||
                row.ProviderInstanceId != permit.Provider ||
                row.RevisionNumber != permit.Revision || row.Slot != permit.Slot || row.Actor != permit.Actor || row.CreatedAt != permit.Time)
                throw new InvalidOperationException("Shared credential revocation has no exact owning scope.");
            var bindingQuery = from binding in SharedCredentialBindings.AsNoTracking()
                               join revision in ProviderConfigurationRevisions.AsNoTracking() on
                                   new { binding.ProviderInstanceId, binding.RevisionNumber, binding.EnvironmentRecordId } equals
                                   new { revision.ProviderInstanceId, revision.RevisionNumber, revision.EnvironmentRecordId }
                               where binding.ProviderInstanceId == row.ProviderInstanceId && binding.RevisionNumber == row.RevisionNumber &&
                                   binding.Slot == row.Slot && binding.EnvironmentRecordId == permit.Environment && binding.SecretId == permit.Secret &&
                                   binding.SecretVersionId == permit.Version && binding.Use == permit.Use && revision.IsSealed
                               select binding.Slot;
            if (!(asynchronous ? await bindingQuery.AnyAsync(token) : bindingQuery.Any()))
                throw new InvalidOperationException("Shared credential revocation requires its exact sealed binding.");
        }
    }
    private sealed record BindingPermit(Guid Transaction, Guid Environment, Guid Provider, long Revision, string Slot, Guid Secret, Guid Version,
        string Use, string Actor, DateTimeOffset Time)
    {
        internal static BindingPermit From(SharedCredentialBindingRow row, Guid transaction) => new(transaction, row.EnvironmentRecordId, row.ProviderInstanceId,
            row.RevisionNumber, row.Slot, row.SecretId, row.SecretVersionId, row.Use, row.Actor, row.CreatedAt);
    }
    private sealed record RevocationPermit(Guid Transaction, Guid Provider, long Revision, string Slot, string Actor, DateTimeOffset Time,
        Guid Environment, Guid Secret, Guid Version, string Use);
}
