using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>One SQL statement captures each instance and its current alias-bound revision.</summary>
public sealed class PostgreSqlProviderConnectionSelectionQuery(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IProviderConnectionSelectionQuery
{
    public async ValueTask<IReadOnlyList<ProviderConnectionSelection>> SelectAsync(EnvironmentRecordId environment,
        string? name, int maximum, CancellationToken token)
    {
        if (maximum is < 1 or > 1025) throw new ArgumentOutOfRangeException(nameof(maximum));
        await using var context = await contexts.CreateDbContextAsync(token);
        var env = Guid.Parse(environment.ToString());
        var rows = await (from instance in context.ProviderInstances.AsNoTracking()
                          join alias in context.ProviderAliases.AsNoTracking() on
                              new { ProviderInstanceId = instance.Id, instance.EnvironmentRecordId } equals
                              new { alias.ProviderInstanceId, alias.EnvironmentRecordId } into bindings
                          from alias in bindings.DefaultIfEmpty()
                          where instance.EnvironmentRecordId == env && (name == null || instance.Name == name)
                          orderby instance.Name, instance.Id
                          select new
                          {
                              instance.Id,
                              instance.Name,
                              Alias = alias == null ? null : alias.Alias,
                              Revision = alias == null ? (long?)null : alias.RevisionNumber
                          }).Take(maximum).ToArrayAsync(token);
        return rows.Select(r => new ProviderConnectionSelection(r.Name, ProviderInstanceId.Parse(r.Id.ToString()), r.Alias,
            r.Revision is { } revision ? ProviderConfigurationRevisionNumber.Create(revision) : null)).ToArray();
    }
}
