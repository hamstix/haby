using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class ResourceImportMigrationTests
{
    [Fact]
    public void Operator_request_migration_is_additive_and_preserves_existing_import_history()
    {
        using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, x => x.EndsWith("_PersistOperatorImportRequests", StringComparison.Ordinal));
        Assert.True(index > 0);
        var script = db.GetService<IMigrator>().GenerateScript(migrations[index - 1], migrations[index]);
        Assert.Contains("CREATE TABLE operator_resource_import_requests", script);
        Assert.Contains("CK_operator_import_request_completion", script);
        Assert.DoesNotContain("RAISE EXCEPTION", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Current_model_matches_snapshot_and_migration_refuses_frozen_history_before_schema_changes()
    {
        using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, x => x.EndsWith("_PersistResourceImportAuthority", StringComparison.Ordinal));
        var script = db.GetService<IMigrator>().GenerateScript(migrations[index - 1], migrations[index]);
        var guard = script.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
        Assert.True(guard >= 0, "Direct migration must refuse superseded frozen history.");
        Assert.Contains("deployment_plans", script);
        Assert.Contains("application_deployment_generations", script);
        Assert.True(guard < script.IndexOf("ALTER TABLE", StringComparison.Ordinal));
        Assert.DoesNotContain("DEFAULT", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE resource_management", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Protected_import_migration_refuses_previous_imports_before_creating_receipt_or_private_input_tables()
    {
        using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, x => x.EndsWith("_ConfirmResourceImportCredentials", StringComparison.Ordinal));
        var script = db.GetService<IMigrator>().GenerateScript(migrations[index - 1], migrations[index]);
        var guard = script.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < script.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        Assert.Contains("import_intent_json IS NOT NULL", script);
        Assert.Contains("origin = 'Imported'", script);
        Assert.DoesNotContain("UPDATE resource_management", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", script, StringComparison.OrdinalIgnoreCase);
    }
}
