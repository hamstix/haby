using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Haby.Persistence.PostgreSql;

/// <summary>Creates the catalog context for migration tooling without opening a connection.</summary>
public sealed class ManifestCatalogDesignTimeFactory : IDesignTimeDbContextFactory<ManifestCatalogDbContext>
{
    public ManifestCatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql()
            .Options;

        return new ManifestCatalogDbContext(options);
    }
}
