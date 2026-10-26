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
        var check = await preflight.CheckAsync(environment, proposed, cancellationToken);
        if (!check.IsReady) return new(ProviderAliasChangeOutcome.PreflightFailed, check);
        cancellationToken.ThrowIfCancellationRequested();

        var environmentId = Guid.Parse(environment.ToString());
        var newInstanceId = Guid.Parse(proposed.InstanceId.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
            int changed;
            try
            {
                changed = await context.ProviderAliases
                    .Where(row => row.EnvironmentRecordId == environmentId && row.Alias == alias.Value &&
                        row.ProviderInstanceId == previousId && row.RevisionNumber == expected.RevisionNumber.Value)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.ProviderInstanceId, newInstanceId)
                        .SetProperty(row => row.RevisionNumber, proposed.RevisionNumber.Value), cancellationToken);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            { return new(ProviderAliasChangeOutcome.InstanceAlreadyBound); }
            if (changed != 1) return new(ProviderAliasChangeOutcome.StaleExpectation);
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
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(ProviderAliasChangeOutcome.Changed);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException postgres &&
            postgres.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new(postgres.ConstraintName == "IX_provider_aliases_provider_instance_id" ?
                ProviderAliasChangeOutcome.InstanceAlreadyBound : ProviderAliasChangeOutcome.StaleExpectation);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new(ProviderAliasChangeOutcome.InstanceAlreadyBound);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException postgres &&
            postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return new(ProviderAliasChangeOutcome.TargetUnavailable);
        }
    }

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
