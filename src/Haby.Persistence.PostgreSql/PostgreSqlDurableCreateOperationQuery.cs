using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.Persistence.PostgreSql;

/// <summary>Reads exact operation identity and pinned generation inputs without consulting aliases.</summary>
public sealed class PostgreSqlDurableCreateOperationQuery(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IDurableCreateOperationQuery
{
    public async ValueTask<DurableCreateOperation?> FindAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var deploymentId = Guid.Parse(key.DeploymentId.ToString());
        var rows = await (from operation in context.DurableCreateOperations.AsNoTracking()
                          join generation in context.DeploymentGenerations.AsNoTracking()
                              on new { operation.DeploymentId, operation.Generation }
                              equals new { generation.DeploymentId, generation.Generation }
                          where operation.DeploymentId == deploymentId && operation.Generation == key.Generation.Value &&
                  operation.ResourceId == key.ResourceId.Value && operation.Action == key.Action.ToString()
                          select new { operation, generation }).SingleOrDefaultAsync(cancellationToken);
        if (rows is null) return null;
        if (rows.operation.State != nameof(DurableCreateOperationState.Pending) ||
            rows.generation.Presence != nameof(DesiredPresence.Present))
            throw new InvalidDataException("Stored create operation is inconsistent.");
        return new DurableCreateOperation(key, DurableCreateOperationState.Pending,
            ProviderInstanceId.Parse(rows.generation.ProviderInstanceId.ToString()),
            ProviderConfigurationRevisionNumber.Create(rows.generation.ConfigurationRevisionNumber),
            rows.generation.ImplementationId, rows.operation.CreatedAt);
    }
}

public static class DurableCreateOperationQueryServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDurableCreateOperationQuery(this IServiceCollection services)
    {
        services.TryAddSingleton<IDurableCreateOperationQuery, PostgreSqlDurableCreateOperationQuery>();
        return services;
    }
}
