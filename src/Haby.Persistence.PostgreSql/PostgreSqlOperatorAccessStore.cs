using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Provider-neutral authentication state. Each operation uses an independent context; no positive cache.</summary>
public sealed class PostgreSqlOperatorAccessStore(IDbContextFactory<ManifestCatalogDbContext> contexts, TimeProvider clock) : IOperatorAccessStore
{
    public async ValueTask<OperatorAccessCreated> CreateAsync(string? displayName, string actor, DateTimeOffset? expiresAt, CancellationToken token)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        if (displayName is not null && (displayName.Length is < 1 or > 128 || displayName.Any(char.IsControl))) throw new ArgumentException("Invalid display name.");
        var now = Precision(clock.GetUtcNow());
        if (expiresAt is { } expiry)
        {
            if (expiry.Offset != TimeSpan.Zero) throw new ArgumentException("Expiry must be a future UTC instant.");
            expiresAt = Precision(expiry);
            if (expiresAt <= now) throw new ArgumentException("Expiry must be a future UTC instant.");
        }
        var id = Guid.CreateVersion7();
        var credential = OperatorCredential.Create(id);
        var row = new OperatorAccessRow
        {
            Id = id,
            Purpose = "operator-admin/v1",
            DisplayName = displayName,
            CredentialHash = OperatorCredential.Hash(credential),
            Actor = actor,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };
        await using var db = await contexts.CreateDbContextAsync(token);
        db.OperatorAccess.Add(row);
        await db.SaveChangesAsync(token);
        return new(Metadata(row), credential);
    }
    public async ValueTask<OperatorAccessGrant?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.OperatorAccess.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return row is null ? null : Metadata(row);
    }
    public async ValueTask<IReadOnlyList<OperatorAccessGrant>> ListAsync(Guid? after, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("Invalid page size.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.OperatorAccess.AsNoTracking().Where(x => after == null || x.Id.CompareTo(after.Value) > 0)
            .OrderBy(x => x.Id).Take(limit).ToArrayAsync(token);
        return rows.Select(Metadata).ToArray();
    }
    public async ValueTask<OperatorAccessGrant?> RevokeAsync(Guid id, string actor, CancellationToken token)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = Precision(clock.GetUtcNow());
        await db.OperatorAccess.Where(x => x.Id == id && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedBy, actor), token);
        return await GetAsync(id, token);
    }
    public async ValueTask<OperatorAccessGrant?> AuthorizeAsync(string credential, CancellationToken token)
    {
        if (!OperatorCredential.TryIdentify(credential, out var id)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.OperatorAccess.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return row is null || row.Purpose != "operator-admin/v1" || !OperatorCredential.Verify(credential, row.CredentialHash) ||
            row.RevokedAt is not null || row.ExpiresAt <= clock.GetUtcNow() ? null : Metadata(row);
    }
    private static OperatorAccessGrant Metadata(OperatorAccessRow r) => new(r.Id, r.DisplayName, r.Actor, r.CreatedAt, r.ExpiresAt, r.RevokedBy, r.RevokedAt);
    private static DateTimeOffset Precision(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero);
}
