using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>EF-owned declaration transaction. No secret material or external Provider access.</summary>
public sealed class PostgreSqlProviderDeclarationCatalog(IDbContextFactory<ManifestCatalogDbContext> contexts,
    ProviderDeclarationRegistry registry, IEnumerable<IProviderConfigurationCodec> codecs) : IProviderDeclarationCatalog
{
    private readonly IProviderConfigurationCodec[] _codecs = codecs.ToArray();
    public async ValueTask<ProviderResult> ApplyAsync(EnvironmentRecordId environment, ProviderDeclaration declaration,
        object properties, IProviderDeclarationAdapter adapter, bool confirmIdentity, long? expectedPreviousRevision,
        string actor, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var env = Guid.Parse(environment.ToString());
            // EF cannot express row-lock modes. The Environment lock covers absent-name races;
            // the instance lock also serializes with the compatibility revision store and its DB guard.
            if (await context.Environments.FromSqlInterpolated($"SELECT * FROM environments WHERE id = {env} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken) is null) return new(ProviderOutcome.EnvironmentNotFound);
            var name = declaration.Metadata.Name;
            var instance = await context.ProviderInstances.FromSqlInterpolated(
                $"SELECT * FROM provider_instances WHERE environment_record_id = {env} AND name = {name} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            var spec = declaration.Spec;
            if (instance is not null && instance.ProviderKindId != spec.ProviderKind) return new(ProviderOutcome.ProviderKindConflict);
            var references = new Dictionary<string, SecretRef>(StringComparer.Ordinal);
            foreach (var entry in spec.SecretRefs)
            {
                var selected = await (from secret in context.Secrets.AsNoTracking()
                                      join version in context.SecretVersions.AsNoTracking() on secret.Id equals version.SecretId
                                      where secret.EnvironmentRecordId == env && secret.Name == entry.Value.SecretName &&
                                            version.Number == entry.Value.Version
                                      select new { secret.Id, VersionId = version.Id }).SingleOrDefaultAsync(cancellationToken);
                if (selected is null) return new(ProviderOutcome.SecretReferenceUnavailable);
                references.Add(entry.Key, new(SecretId.Parse(selected.Id.ToString()), SecretVersionId.Parse(selected.VersionId.ToString())));
            }
            var candidate = adapter.Bind(properties, references);
            var prepared = adapter.Codec.Encode(candidate);
            PostgreSqlProviderConfigurationStore.ValidatePrepared(adapter.Codec, prepared);
            if (prepared.ImplementationId != adapter.Key.ImplementationId || prepared.SecretReferences.Count != references.Count ||
                prepared.SecretReferences.Any(r => !references.TryGetValue(r.Slot, out var exact) || exact != r.Reference))
                return new(ProviderOutcome.ConfigurationUnavailable);
            ProviderConfigurationRevisionRow? previous = null;
            if (instance is not null)
            {
                var existing = await context.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ProviderInstanceId == instance.Id && x.RevisionNumber == spec.Revision, cancellationToken);
                if (existing is not null)
                {
                    var decoded = await DecodeAsync(context, instance, existing, cancellationToken);
                    if (decoded is null) return new(ProviderOutcome.ConfigurationUnavailable);
                    var storedReferences = await context.ProviderRevisionSecretRefs.AsNoTracking().Where(x =>
                        x.ProviderInstanceId == instance.Id && x.RevisionNumber == spec.Revision)
                        .Select(x => new { x.Slot, x.SecretId, x.SecretVersionId }).ToArrayAsync(cancellationToken);
                    // Exact reference identity is a generic retry invariant, independent of
                    // whether the typed adapter's settings comparison includes credentials.
                    var sameReferences = storedReferences.Length == references.Count && storedReferences.All(x =>
                        references.TryGetValue(x.Slot, out var exact) && x.SecretId == Guid.Parse(exact.SecretId.ToString()) &&
                        x.SecretVersionId == Guid.Parse(exact.SecretVersionId.ToString()));
                    var equivalent = existing.ImplementationId == prepared.ImplementationId &&
                        existing.ConfigurationShapeId == prepared.ShapeId && sameReferences && adapter.Equivalent(decoded, candidate);
                    return equivalent ? new(ProviderOutcome.ExistingEquivalent, await MetadataAsync(context, instance, existing, cancellationToken)) :
                        new(ProviderOutcome.RevisionContentConflict);
                }
                previous = await context.ProviderConfigurationRevisions.AsNoTracking().Where(x => x.ProviderInstanceId == instance.Id)
                    .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(cancellationToken);
            }
            // Explicit confirmation never silently moves to a concurrent baseline, even if that
            // concurrent revision happens to use the requested endpoint.
            if (confirmIdentity && expectedPreviousRevision != previous?.RevisionNumber) return new(ProviderOutcome.StaleBaseline);
            if (previous is null ? spec.Revision != 1 : spec.Revision <= previous.RevisionNumber)
                return new(ProviderOutcome.RevisionNotIncreasing);
            var changedEndpoint = false;
            if (previous is not null)
            {
                var key = new ProviderAdapterKey(instance!.ProviderKindId, previous.ConfigurationShapeId, previous.ImplementationId);
                if (!adapter.CanReplace(key)) return new(ProviderOutcome.UnsupportedConfiguration);
                var decoded = await DecodeAsync(context, instance, previous, cancellationToken);
                if (decoded is null) return new(ProviderOutcome.ConfigurationUnavailable);
                changedEndpoint = !adapter.SameEndpoint(decoded, candidate);
                if (changedEndpoint && !confirmIdentity)
                    return new(ProviderOutcome.IdentityConfirmationRequired, PreviousRevision: previous.RevisionNumber);
            }
            if (instance is null)
            {
                instance = new ProviderInstanceRow
                {
                    Id = Guid.Parse(ProviderInstanceId.New().ToString()),
                    EnvironmentRecordId = env,
                    Name = name,
                    ProviderKindId = prepared.ProviderKindId,
                    TargetFormatVersion = prepared.TargetIdentity.FormatVersion,
                    TargetIdentity = prepared.TargetIdentity.Value
                };
                context.ProviderInstances.Add(instance);
            }
            var revision = new ProviderConfigurationRevisionRow
            {
                ProviderInstanceId = instance.Id,
                EnvironmentRecordId = env,
                RevisionNumber = spec.Revision,
                ImplementationId = prepared.ImplementationId,
                ConfigurationShapeId = prepared.ShapeId,
                PayloadFormatVersion = prepared.PayloadFormatVersion,
                PayloadJson = prepared.PayloadJson,
                ConfirmedPreviousRevision = changedEndpoint ? previous!.RevisionNumber : null,
                ConfirmationActor = changedEndpoint ? actor : null,
                ConfirmedAt = changedEndpoint ? DateTimeOffset.UtcNow : null
            };
            context.ProviderConfigurationRevisions.Add(revision);
            foreach (var reference in prepared.SecretReferences)
                context.ProviderRevisionSecretRefs.Add(new ProviderRevisionSecretRefRow
                {
                    ProviderInstanceId = instance.Id,
                    RevisionNumber = spec.Revision,
                    EnvironmentRecordId = env,
                    Slot = reference.Slot,
                    SecretId = Guid.Parse(reference.Reference.SecretId.ToString()),
                    SecretVersionId = Guid.Parse(reference.Reference.SecretVersionId.ToString())
                });
            await context.SaveChangesAsync(cancellationToken);
            var sealedRows = await context.ProviderConfigurationRevisions.Where(x => x.ProviderInstanceId == instance.Id &&
                x.RevisionNumber == spec.Revision && !x.IsSealed).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsSealed, true), cancellationToken);
            if (sealedRows != 1) return new(ProviderOutcome.StorageUnavailable);
            var metadata = await MetadataAsync(context, instance, revision, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(ProviderOutcome.Created, metadata);
        }
        catch (Exception error) when (error is DbUpdateException or NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(ProviderOutcome.StorageUnavailable);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or FormatException)
        { return new(ProviderOutcome.ConfigurationUnavailable); }
    }

    private async Task<object?> DecodeAsync(ManifestCatalogDbContext context, ProviderInstanceRow instance,
        ProviderConfigurationRevisionRow row, CancellationToken cancellationToken)
    {
        var adapter = registry.Find(new(instance.ProviderKindId, row.ConfigurationShapeId, row.ImplementationId));
        if (adapter is null || !row.IsSealed) return null;
        var reader = _codecs.SingleOrDefault(codec => codec.ProviderKindId == instance.ProviderKindId &&
            codec.ShapeId == row.ConfigurationShapeId && codec.PayloadFormatVersion == row.PayloadFormatVersion);
        if (reader is null) return null;
        var decoded = reader.Decode(row.PayloadJson);
        var prepared = reader.Encode(decoded);
        PostgreSqlProviderConfigurationStore.ValidatePrepared(reader, prepared);
        var refs = await context.ProviderRevisionSecretRefs.AsNoTracking().Where(x => x.ProviderInstanceId == instance.Id &&
            x.RevisionNumber == row.RevisionNumber).ToArrayAsync(cancellationToken);
        return prepared.PayloadJson == row.PayloadJson && prepared.ImplementationId == row.ImplementationId &&
            prepared.SecretReferences.Count == refs.Length && prepared.SecretReferences.All(r => refs.Any(a =>
                a.Slot == r.Slot && a.SecretId.ToString() == r.Reference.SecretId.ToString() &&
                a.SecretVersionId.ToString() == r.Reference.SecretVersionId.ToString())) ? decoded : null;
    }

    public async ValueTask<ProviderMetadata?> GetAsync(EnvironmentRecordId environment, string name, long? revision, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var env = Guid.Parse(environment.ToString());
        var instance = await context.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == env && x.Name == name, cancellationToken);
        if (instance is null) return null;
        var query = context.ProviderConfigurationRevisions.AsNoTracking().Where(x => x.ProviderInstanceId == instance.Id && x.IsSealed);
        if (revision is not null) query = query.Where(x => x.RevisionNumber == revision);
        var row = await query.OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(cancellationToken);
        return revision is not null && row is null ? null : await MetadataAsync(context, instance, row, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ProviderMetadata>> ListAsync(EnvironmentRecordId environment, string? after, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 101) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var env = Guid.Parse(environment.ToString());
        var query = context.ProviderInstances.AsNoTracking().Where(x => x.EnvironmentRecordId == env);
        if (after is not null) query = query.Where(x => string.Compare(EF.Functions.Collate(x.Name ?? "~" + x.Id.ToString(), "C"), after) > 0);
        var rows = await query.OrderBy(x => EF.Functions.Collate(x.Name ?? "~" + x.Id.ToString(), "C")).Take(limit).ToArrayAsync(cancellationToken);
        var result = new List<ProviderMetadata>();
        foreach (var instance in rows)
        {
            var revision = await context.ProviderConfigurationRevisions.AsNoTracking().Where(x => x.ProviderInstanceId == instance.Id && x.IsSealed)
                .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(cancellationToken);
            result.Add(await MetadataAsync(context, instance, revision, cancellationToken));
        }
        return result;
    }

    private static async Task<ProviderMetadata> MetadataAsync(ManifestCatalogDbContext context, ProviderInstanceRow instance,
        ProviderConfigurationRevisionRow? revision, CancellationToken cancellationToken)
    {
        ProviderRevisionMetadata? metadata = null;
        if (revision is not null)
        {
            var references = await context.ProviderRevisionSecretRefs.AsNoTracking().Where(x => x.ProviderInstanceId == instance.Id &&
                x.RevisionNumber == revision.RevisionNumber).OrderBy(x => x.Slot).ToArrayAsync(cancellationToken);
            metadata = new(revision.RevisionNumber, revision.ConfigurationShapeId, revision.ImplementationId,
                references.Select(x => new ProviderConfigurationSecretReference(x.Slot,
                    new(SecretId.Parse(x.SecretId.ToString()), SecretVersionId.Parse(x.SecretVersionId.ToString())))).ToArray());
        }
        return new(EnvironmentRecordId.Parse(instance.EnvironmentRecordId.ToString()), ProviderInstanceId.Parse(instance.Id.ToString()),
            instance.Name, instance.ProviderKindId, metadata);
    }
}
