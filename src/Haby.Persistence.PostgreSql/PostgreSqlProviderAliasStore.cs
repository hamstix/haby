using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Exact alias selection with preflight outside its short CAS transaction.</summary>
public sealed class PostgreSqlProviderAliasStore(
    IDbContextFactory<ManifestCatalogDbContext> contexts,
    IProviderRevisionPreflight preflight, TimeProvider clock) : IProviderAliasStore
{
    public async ValueTask<ProviderAliasChangeResult> ChangeAsync(EnvironmentRecordId environment,
        ProviderAlias alias, ProviderAliasBinding? expected, ProviderAliasBinding proposed,
        string technicalActor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(proposed);
        if (string.IsNullOrWhiteSpace(technicalActor) || technicalActor.Length > 256)
            throw new ArgumentException("Invalid technical actor.", nameof(technicalActor));
        cancellationToken.ThrowIfCancellationRequested();
        ProviderPreflightResult check;
        try { check = await preflight.CheckAsync(environment, proposed, cancellationToken); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception)
        {
            return new(ProviderAliasChangeOutcome.PreflightFailed,
                new ProviderPreflightResult(ProviderPreflightOutcome.ConfigurationUnavailable));
        }
        if (check.Outcome == ProviderPreflightOutcome.RevisionUnavailable)
            return new(ProviderAliasChangeOutcome.TargetUnavailable, check);
        if (!check.IsReady) return new(ProviderAliasChangeOutcome.PreflightFailed, check);
        cancellationToken.ThrowIfCancellationRequested();

        var environmentId = Guid.Parse(environment.ToString());
        var newInstanceId = Guid.Parse(proposed.InstanceId.ToString());
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '10s'", cancellationToken);
            var available = await context.ProviderConfigurationRevisions.AsNoTracking().AnyAsync(row =>
                row.EnvironmentRecordId == environmentId && row.ProviderInstanceId == newInstanceId &&
                row.RevisionNumber == proposed.RevisionNumber.Value && row.IsSealed, cancellationToken);
            if (!available) return new(ProviderAliasChangeOutcome.TargetUnavailable);

            if (expected is null)
            {
                context.ProviderAliases.Add(new ProviderAliasRow
                {
                    EnvironmentRecordId = environmentId,
                    Alias = alias.Value,
                    ProviderInstanceId = newInstanceId,
                    RevisionNumber = proposed.RevisionNumber.Value
                });
            }
            else
            {
                var previousId = Guid.Parse(expected.InstanceId.ToString());
                var changed = await context.ProviderAliases
                    .Where(row => row.EnvironmentRecordId == environmentId && row.Alias == alias.Value &&
                        row.ProviderInstanceId == previousId && row.RevisionNumber == expected.RevisionNumber.Value)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.ProviderInstanceId, newInstanceId)
                        .SetProperty(row => row.RevisionNumber, proposed.RevisionNumber.Value), cancellationToken);
                if (changed != 1) return new(ProviderAliasChangeOutcome.StaleExpectation);
                if (expected == proposed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await transaction.CommitAsync(CancellationToken.None);
                    return new(ProviderAliasChangeOutcome.Unchanged);
                }
            }

            context.ProviderAliasAudits.Add(new ProviderAliasAuditRow
            {
                Id = Guid.CreateVersion7(),
                EnvironmentRecordId = environmentId,
                Alias = alias.Value,
                PreviousInstanceId = expected is null ? null : Guid.Parse(expected.InstanceId.ToString()),
                PreviousRevisionNumber = expected?.RevisionNumber.Value,
                NewInstanceId = newInstanceId,
                NewRevisionNumber = proposed.RevisionNumber.Value,
                TechnicalActor = technicalActor,
                ChangedAt = clock.GetUtcNow()
            });
            await context.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(ProviderAliasChangeOutcome.Changed);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception error) when (Postgres(error)?.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            if (expected is null)
            {
                try
                {
                    if (await AliasExistsAsync(environmentId, alias.Value, cancellationToken))
                        return new(ProviderAliasChangeOutcome.StaleExpectation);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                { throw new OperationCanceledException(cancellationToken); }
                catch (Exception) { return new(ProviderAliasChangeOutcome.StoreUnavailable); }
            }
            return new(ProviderAliasChangeOutcome.InstanceAlreadyBound);
        }
        catch (Exception error) when (Postgres(error)?.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        { return new(ProviderAliasChangeOutcome.TargetUnavailable); }
        catch (Exception error) when (Postgres(error)?.SqlState is "40P01" or "55P03" or "57014")
        { return new(ProviderAliasChangeOutcome.StoreUnavailable); }
        catch (Exception)
        { return new(ProviderAliasChangeOutcome.StoreUnavailable); }
    }

    private async Task<bool> AliasExistsAsync(Guid environmentId, string alias, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.ProviderAliases.AsNoTracking()
            .AnyAsync(row => row.EnvironmentRecordId == environmentId && row.Alias == alias, cancellationToken);
    }

    private static PostgresException? Postgres(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PostgresException postgres) return postgres;
        return null;
    }

    public ValueTask<PersistedProviderAliasResolution?> ResolveAsync(EnvironmentRecordId environment,
        ProviderAlias alias, CancellationToken cancellationToken) =>
        new PostgreSqlProviderAliasResolver(contexts).ResolveAsync(environment, alias, cancellationToken);

}

public static class ProviderAliasStoreServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlProviderAliasStore(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<PostgreSqlProviderAliasStore>();
        services.AddSingleton<IProviderAliasStore>(provider => provider.GetRequiredService<PostgreSqlProviderAliasStore>());
        services.AddSingleton<IProviderAliasResolver>(provider => provider.GetRequiredService<PostgreSqlProviderAliasStore>());
        return services;
    }
}
