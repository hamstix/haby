using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Alias reads without composing Provider preflight or credential access.</summary>
public sealed class PostgreSqlProviderAliasResolver(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IProviderAliasResolver
{
    public async ValueTask<PersistedProviderAliasResolution?> ResolveAsync(EnvironmentRecordId environment,
        ProviderAlias alias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(alias);
        var environmentId = Guid.Parse(environment.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var result = await (from binding in context.ProviderAliases.AsNoTracking()
                            join revision in context.ProviderConfigurationRevisions.AsNoTracking()
                                on new { binding.ProviderInstanceId, binding.RevisionNumber, binding.EnvironmentRecordId }
                                equals new { revision.ProviderInstanceId, revision.RevisionNumber, revision.EnvironmentRecordId }
                            where binding.EnvironmentRecordId == environmentId && binding.Alias == alias.Value && revision.IsSealed
                            select new { binding.ProviderInstanceId, binding.RevisionNumber, revision.ImplementationId })
            .SingleOrDefaultAsync(cancellationToken);
        return result is null ? null : new PersistedProviderAliasResolution(alias,
            new ProviderAliasBinding(ProviderInstanceId.Parse(result.ProviderInstanceId.ToString()),
                ProviderConfigurationRevisionNumber.Create(result.RevisionNumber)), result.ImplementationId);
    }
}
