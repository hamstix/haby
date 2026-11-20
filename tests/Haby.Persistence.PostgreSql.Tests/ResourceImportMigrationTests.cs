using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class ResourceImportMigrationTests
{
    [Fact]
    public void Current_model_matches_snapshot_and_migration_refuses_frozen_history_before_schema_changes()
    {
        using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var script = db.GetService<IMigrator>().GenerateScript(migrations[^2], migrations[^1]);
        var guard = script.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
        Assert.True(guard >= 0, "Direct migration must refuse superseded frozen history.");
        Assert.Contains("deployment_plans", script);
        Assert.Contains("application_deployment_generations", script);
        Assert.True(guard < script.IndexOf("ALTER TABLE", StringComparison.Ordinal));
        Assert.DoesNotContain("DEFAULT", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE resource_management", script, StringComparison.OrdinalIgnoreCase);
    }
}
