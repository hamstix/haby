using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Policy revisions are append-only. Selection synchronizes with Plan/Commit, including absence.</summary>
public sealed class PostgreSqlResourceNamingStore(IDbContextFactory<ManifestCatalogDbContext> contexts, TimeProvider clock) : IResourceNamingStore
{
    internal static async Task LockAsync(ManifestCatalogDbContext db, Guid environment, string alias, bool shared, CancellationToken token)
    {
        // A key lock also covers the absent selection. Alias row locks alone cannot do so.
        var key = $"haby-naming:{environment:N}:{alias}";
        if (shared) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({key}, 0))", token);
        else await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
    }

    internal static async Task<ResourceNamingState> CaptureAsync(ManifestCatalogDbContext db, Guid environment, string alias, CancellationToken token)
    {
        await LockAsync(db, environment, alias, true, token);
        var row = await db.ResourceNamingSelections.AsNoTracking().SingleOrDefaultAsync(x => x.Environment == environment && x.Alias == alias, token);
        if (row is null) return new(ResourceNamingSnapshot.Default, null, "", default);
        var policy = await db.ResourceNamingPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Environment == environment && x.Name == row.Name && x.Revision == row.Revision, token)
            ?? throw new InvalidDataException("Selected naming policy unavailable.");
        var snapshot = ResourceNamingCodec.Decode(policy.Snapshot);
        if (snapshot.Reference != new ResourceNamingReference(row.Name, row.Revision)) throw new InvalidDataException("Naming reference mismatch.");
        return new(snapshot with { SelectionToken = row.Token.ToString("D") }, row.Token.ToString("D"), row.Actor, row.ChangedAt);
    }

    public async ValueTask<ResourceNamingResult> CreateAsync(EnvironmentRecordId environment, ResourceNamingReference reference,
        ResourceNamingPolicy policy, string actor, CancellationToken token)
    {
        reference.Validate(); policy.Validate(); PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var id = Guid.Parse(environment.ToString());
        // Serializes preparatory imports, without changing any binding or planning lock.
        var key = $"haby-naming-policy:{id:N}:{reference.Name}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
        if (!await db.Environments.AnyAsync(x => x.Id == id, token)) return new(ResourceNamingOutcome.EnvironmentNotFound);
        var json = ResourceNamingCodec.Encode(new(1, reference, policy));
        var previous = await db.ResourceNamingPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Environment == id && x.Name == reference.Name && x.Revision == reference.Revision, token);
        if (previous is not null) return new(previous.Snapshot == json ? ResourceNamingOutcome.Unchanged : ResourceNamingOutcome.RevisionConflict,
            previous.Snapshot == json ? new(ResourceNamingCodec.Decode(previous.Snapshot), null, previous.Actor, previous.CreatedAt) : null);
        var now = clock.GetUtcNow();
        now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        db.ResourceNamingPolicies.Add(new() { Environment = id, Name = reference.Name, Revision = reference.Revision, Snapshot = json, Actor = actor, CreatedAt = now });
        await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None);
        return new(ResourceNamingOutcome.Created, new(new(1, reference, policy), null, actor, now));
    }

    public async ValueTask<ResourceNamingResult> GetAsync(EnvironmentRecordId environment, ResourceNamingReference reference, CancellationToken token)
    {
        reference.Validate();
        await using var db = await contexts.CreateDbContextAsync(token);
        var id = Guid.Parse(environment.ToString());
        var row = await db.ResourceNamingPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.Environment == id && x.Name == reference.Name && x.Revision == reference.Revision, token);
        if (row is null) return new(ResourceNamingOutcome.NotFound);
        var snapshot = ResourceNamingCodec.Decode(row.Snapshot);
        if (snapshot.Reference != reference) throw new InvalidDataException("Naming reference mismatch.");
        return new(ResourceNamingOutcome.Found, new(snapshot, null, row.Actor, row.CreatedAt));
    }

    public async ValueTask<ResourceNamingResult> GetSelectionAsync(EnvironmentRecordId environment, ProviderAlias alias, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var id = Guid.Parse(environment.ToString());
        if (!await db.ProviderAliases.AnyAsync(x => x.EnvironmentRecordId == id && x.Alias == alias.Value, token)) return new(ResourceNamingOutcome.BindingNotFound);
        return new(ResourceNamingOutcome.Found, await CaptureAsync(db, id, alias.Value, token));
    }

    public async ValueTask<ResourceNamingResult> SelectAsync(EnvironmentRecordId environment, ProviderAlias alias,
        ProviderAliasBinding expectedBinding, string? expectedToken, ResourceNamingReference reference, string actor, CancellationToken token)
    {
        reference.Validate(); PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        if (expectedToken is not null && (!Guid.TryParseExact(expectedToken, "D", out var parsed) || parsed == Guid.Empty))
            return new(ResourceNamingOutcome.StaleExpectation);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var id = Guid.Parse(environment.ToString());
        // Same lock order as Commit: exact alias, then naming selection. This API never updates the alias.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT environment_record_id FROM provider_aliases WHERE environment_record_id = {id} AND alias = {alias.Value} FOR SHARE", token);
        var binding = await db.ProviderAliases.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == id && x.Alias == alias.Value, token);
        if (binding is null) return new(ResourceNamingOutcome.BindingNotFound);
        if (binding.ProviderInstanceId.ToString() != expectedBinding.InstanceId.ToString() || binding.RevisionNumber != expectedBinding.RevisionNumber.Value)
            return new(ResourceNamingOutcome.StaleExpectation);
        await LockAsync(db, id, alias.Value, false, token);
        var row = await db.ResourceNamingSelections.SingleOrDefaultAsync(x => x.Environment == id && x.Alias == alias.Value, token);
        if (row?.Token.ToString("D") != expectedToken) return new(ResourceNamingOutcome.StaleExpectation);
        var policy = await db.ResourceNamingPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Environment == id && x.Name == reference.Name && x.Revision == reference.Revision, token);
        if (policy is null) return new(ResourceNamingOutcome.RevisionNotFound);
        var snapshot = ResourceNamingCodec.Decode(policy.Snapshot);
        if (snapshot.Reference != reference) throw new InvalidDataException("Naming reference mismatch.");
        if (row is not null && row.Name == reference.Name && row.Revision == reference.Revision)
            return new(ResourceNamingOutcome.Unchanged, new(snapshot with { SelectionToken = row.Token.ToString("D") }, row.Token.ToString("D"), row.Actor, row.ChangedAt));
        var now = clock.GetUtcNow();
        now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        if (row is null) { row = new() { Environment = id, Alias = alias.Value }; db.ResourceNamingSelections.Add(row); }
        row.Name = reference.Name; row.Revision = reference.Revision; row.Token = Guid.CreateVersion7(); row.Actor = actor; row.ChangedAt = now;
        await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None);
        return new(ResourceNamingOutcome.Changed, new(snapshot with { SelectionToken = row.Token.ToString("D") }, row.Token.ToString("D"), row.Actor, now));
    }
}
