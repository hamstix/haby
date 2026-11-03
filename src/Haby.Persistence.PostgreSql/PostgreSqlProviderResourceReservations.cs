using Haby.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>PostgreSQL storage backend for Provider-neutral reservations. No adapter policy.</summary>
public sealed class PostgreSqlProviderResourceReservations(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IProviderResourceReservations
{
    public async ValueTask<bool> ContainsAsync(ProviderResourceReservation reservation, CancellationToken cancellationToken)
    {
        var provider = Guid.Parse(reservation.ProviderInstanceId.ToString());
        var environment = Guid.Parse(reservation.EnvironmentRecordId.ToString());
        var application = Guid.Parse(reservation.ApplicationRecordId.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.ProviderResourceReservations.AsNoTracking().AnyAsync(r => r.ProviderInstanceId == provider &&
            r.EnvironmentRecordId == environment && r.ApplicationRecordId == application && r.ResourceLocalId == reservation.ResourceLocalId.Value &&
            r.Namespace == reservation.Namespace && r.Value == reservation.Value, cancellationToken);
    }

    public async ValueTask<bool> TryReserveAsync(ProviderResourceReservation reservation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (string.IsNullOrEmpty(reservation.Namespace) || reservation.Namespace.Length > 128 ||
            string.IsNullOrEmpty(reservation.Value) || reservation.Value.Length > 512 ||
            reservation.Namespace.Contains('\0') || reservation.Value.Contains('\0'))
            throw new ArgumentException("Invalid reservation storage bounds.", nameof(reservation));
        var provider = Guid.Parse(reservation.ProviderInstanceId.ToString());
        var environment = Guid.Parse(reservation.EnvironmentRecordId.ToString());
        var application = Guid.Parse(reservation.ApplicationRecordId.ToString());
        var resource = reservation.ResourceLocalId.Value;
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var existing = await context.ProviderResourceReservations.AsNoTracking().SingleOrDefaultAsync(r =>
                r.ProviderInstanceId == provider && r.EnvironmentRecordId == environment && r.ApplicationRecordId == application &&
                r.ResourceLocalId == resource && r.Namespace == reservation.Namespace, cancellationToken);
            if (existing is not null) return existing.Value == reservation.Value;
            context.ProviderResourceReservations.Add(new()
            {
                ProviderInstanceId = provider,
                EnvironmentRecordId = environment,
                ApplicationRecordId = application,
                ResourceLocalId = resource,
                Namespace = reservation.Namespace,
                Value = reservation.Value
            });
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception e) when (e is DbUpdateException or NpgsqlException or IOException)
        {
            // Unique constraints arbitrate concurrent callers. Reread after disposal using an
            // independent context, including a possibly committed write with a lost response.
            await using var reread = await contexts.CreateDbContextAsync(cancellationToken);
            var existing = await reread.ProviderResourceReservations.AsNoTracking().SingleOrDefaultAsync(r =>
                r.ProviderInstanceId == provider && r.EnvironmentRecordId == environment && r.ApplicationRecordId == application &&
                r.ResourceLocalId == resource && r.Namespace == reservation.Namespace, cancellationToken);
            if (existing is not null) return existing.Value == reservation.Value;
            var winner = await reread.ProviderResourceReservations.AsNoTracking().SingleOrDefaultAsync(r =>
                r.ProviderInstanceId == provider && r.Namespace == reservation.Namespace && r.Value == reservation.Value, cancellationToken);
            if (winner is not null) return false;
            throw new IOException("Provider resource reservation unavailable.");
        }
    }
}

public static class ProviderResourceReservationServices
{
    public static IServiceCollection AddPostgreSqlProviderResourceReservations(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderResourceReservations, PostgreSqlProviderResourceReservations>();
        return services;
    }
}
