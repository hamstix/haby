using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class ConfigurationSecretMigrationTests
{
    [Fact]
    public void Purpose_migration_preserves_shared_shape_and_refuses_old_frozen_histories()
    {
        using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        var migration = db.Database.GetMigrations().Single(x => x.EndsWith("_AddConfigurationSecretPurpose", StringComparison.Ordinal));
        var script = db.GetService<IMigrator>().GenerateScript("20261116130234_InitialRetirementCompletion", migration);
        Assert.Contains("purpose IN ('ProviderAdministrator', 'Configuration')", script);
        Assert.Contains("deployment_plans", script);
        Assert.Contains("application_deployment_generations", script);
        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
    }
}
