using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Haby.Persistence.PostgreSql;

/// <summary>Design-time schema generation; no connection is opened.</summary>
public sealed class ManifestCatalogDesignTimeFactory : IDesignTimeDbContextFactory<ManifestCatalogDbContext>
{
    public ManifestCatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=haby_catalog;Username=haby")
            .Options;
        return new ManifestCatalogDbContext(options);
    }
}
