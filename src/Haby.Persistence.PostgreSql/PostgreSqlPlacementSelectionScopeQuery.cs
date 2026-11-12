using Haby.Application;
using Haby.Domain;
using ApplicationId = Haby.Domain.ApplicationId;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlPlacementSelectionScopeQuery(IDbContextFactory<ManifestCatalogDbContext> contexts, IManifestRevisionQuery revisions)
    : IPlacementSelectionScopeQuery
{
    public async ValueTask<bool> ApplicationExistsAsync(ApplicationId application, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.Applications.AnyAsync(x => x.ApplicationId == application.Value, cancellationToken);
    }
    public async ValueTask<ManifestRevision?> FindVersionAsync(ApplicationId application, ApplicationVersion version, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.Revisions.AsNoTracking()
            .Where(x => x.ApplicationId == application.Value && x.ApplicationVersion == version.Value)
            .Select(x => new { x.ApplicationRecordId, x.LocalRevisionNumber }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : await revisions.FindAsync(ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
            ManifestRevisionNumber.Create(row.LocalRevisionNumber)), cancellationToken);
    }
}
