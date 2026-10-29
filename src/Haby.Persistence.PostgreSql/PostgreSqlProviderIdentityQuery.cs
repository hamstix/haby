using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Environment-scoped operator selectors resolve to sealed exact revisions without decoding payloads.</summary>
public sealed class PostgreSqlProviderIdentityQuery(IDbContextFactory<ManifestCatalogDbContext> contexts) : IProviderIdentityQuery
{
    public async ValueTask<ProviderAliasBinding?> ResolveAsync(EnvironmentRecordId environment, ProviderSelector selector,
        CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var environmentId = Guid.Parse(environment.ToString());
        var instances = context.ProviderInstances.AsNoTracking().Where(x => x.EnvironmentRecordId == environmentId);
        if (selector.Name is not null) instances = instances.Where(x => x.Name == selector.Name);
        else
        {
            var instanceId = Guid.Parse(selector.InstanceId!);
            instances = instances.Where(x => x.Id == instanceId);
        }
        var id = await (from instance in instances
                        join revision in context.ProviderConfigurationRevisions.AsNoTracking() on instance.Id equals revision.ProviderInstanceId
                        where revision.RevisionNumber == selector.Revision && revision.IsSealed && revision.EnvironmentRecordId == environmentId
                        select (Guid?)instance.Id).SingleOrDefaultAsync(cancellationToken);
        return id is null ? null : new(ProviderInstanceId.Parse(id.Value.ToString()), ProviderConfigurationRevisionNumber.Create(selector.Revision));
    }
}
