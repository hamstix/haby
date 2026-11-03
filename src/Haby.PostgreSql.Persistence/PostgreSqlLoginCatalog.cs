using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Haby.PostgreSql.Persistence;

/// <summary>Adapter-owned catalog model. Common catalog and secret models know no role names.</summary>
public sealed class PostgreSqlLoginCatalog(DbContextOptions<PostgreSqlLoginCatalog> options) : DbContext(options)
{
    internal DbSet<PostgreSqlRoleNameRow> Names => Set<PostgreSqlRoleNameRow>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PostgreSqlRoleNameRow>(e =>
        {
            e.ToTable("postgresql_role_names");
            e.HasKey(r => new { r.Provider, r.Environment, r.Application, r.Resource });
            e.HasIndex(r => new { r.Provider, r.Name }).IsUnique();
            e.Property(r => r.Resource).HasMaxLength(63);
            e.Property(r => r.Name).HasMaxLength(55);
        });
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Guard();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Guard();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    private void Guard()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<PostgreSqlRoleNameRow>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("PostgreSQL role reservations are immutable.");
    }
}

internal sealed class PostgreSqlRoleNameRow
{
    public Guid Provider { get; set; }
    public Guid Environment { get; set; }
    public Guid Application { get; set; }
    public required string Resource { get; set; }
    public required string Name { get; set; }
}

public sealed class PostgreSqlRoleNames(IDbContextFactory<PostgreSqlLoginCatalog> contexts) : IPostgreSqlRoleNames
{
    public async ValueTask<bool> ReserveAsync(ProviderInstanceId provider, ManagedResourceKey owner, CancellationToken token)
    {
        var p = Guid.Parse(provider.ToString());
        var env = Guid.Parse(owner.EnvironmentRecordId.ToString());
        var app = Guid.Parse(owner.ApplicationRecordId.ToString());
        var resource = owner.ResourceLocalId.Value;
        var name = PostgreSqlPairResourceAdmission.RoleName(owner);
        try
        {
            await using var context = await contexts.CreateDbContextAsync(token);
            var existing = await context.Names.AsNoTracking().SingleOrDefaultAsync(r => r.Provider == p &&
                r.Environment == env && r.Application == app && r.Resource == resource, token);
            if (existing is not null) return existing.Name == name;
            context.Names.Add(new() { Provider = p, Environment = env, Application = app, Resource = resource, Name = name });
            await context.SaveChangesAsync(token);
            return true;
        }
        catch (Exception e) when (e is DbUpdateException or NpgsqlException or IOException)
        {
            // Includes same-owner competitors and a lost commit response: independently read the
            // exact retained association. A name held by another owner is never reassigned.
            await using var reread = await contexts.CreateDbContextAsync(token);
            var winner = await reread.Names.AsNoTracking().SingleOrDefaultAsync(r => r.Provider == p && r.Name == name, token);
            if (winner is not null) return winner.Environment == env && winner.Application == app && winner.Resource == resource;
            throw new IOException("PostgreSQL role reservation unavailable.");
        }
    }
}

public sealed class PostgreSqlLoginCatalogInitializer(IDbContextFactory<PostgreSqlLoginCatalog> contexts)
{
    public async Task MigrateAsync(CancellationToken token = default)
    {
        await using var context = await contexts.CreateDbContextAsync(token);
        var applied = (await context.Database.GetAppliedMigrationsAsync(token)).ToArray();
        if (applied.Length != 0 && !applied.SequenceEqual(context.Database.GetMigrations()))
            throw new InvalidOperationException("Unsupported PostgreSQL adapter catalog. Explicit initialization is required.");
        await context.Database.MigrateAsync(token);
    }
}

public static class PostgreSqlLoginCatalogServices
{
    /// <summary>Explicit adapter opt-in. Initialize the adapter catalog before execution.</summary>
    public static IServiceCollection AddPostgreSqlLoginCatalog(this IServiceCollection services, string connection)
    {
        services.AddDbContextFactory<PostgreSqlLoginCatalog>(o => o.UseNpgsql(connection,
            p => p.MigrationsHistoryTable("__PostgreSqlLoginMigrations")));
        services.AddSingleton<IPostgreSqlRoleNames, PostgreSqlRoleNames>();
        services.AddSingleton<PostgreSqlLoginCatalogInitializer>();
        return services;
    }
}

public sealed class PostgreSqlLoginDesignTimeFactory : IDesignTimeDbContextFactory<PostgreSqlLoginCatalog>
{
    public PostgreSqlLoginCatalog CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PostgreSqlLoginCatalog>()
        .UseNpgsql("Host=localhost;Database=haby_design", p => p.MigrationsHistoryTable("__PostgreSqlLoginMigrations")).Options);
}
