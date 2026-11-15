using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class GenerationObservationModelTests
{
    [Fact]
    public void Observation_schema_is_neutral_and_additive_with_declarative_origins_and_no_server_workflow()
    {
        using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        var model = db.Model.FindEntityType(typeof(GenerationObservationAttemptRow))!;
        Assert.Contains(model.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(DurableActionIntentRow));
        Assert.Contains(model.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "DeploymentId", "Generation", "Subject", "Ordinal" }));
        Assert.DoesNotContain(model.GetProperties(), p => new[] { "Uid", "KubernetesGeneration", "Namespace", "DeploymentProjection" }.Contains(p.Name));
        var migration = db.Database.GetMigrations().Single(x => x.EndsWith("_PersistRolloutObservations", StringComparison.Ordinal));
        var script = db.GetService<IMigrator>().GenerateScript("20261114051041_RuntimeConfigurationPublication", migration);
        Assert.Contains("generation_observation_attempts", script); Assert.Contains("generation_observation_records", script);
        Assert.Contains("WorkloadsSubmitted", script); Assert.Contains("RolloutReady", script);
        Assert.DoesNotContain("CREATE FUNCTION", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TRIGGER", script, StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_observation_authority_and_completed_records_cannot_be_tracked_overwrites(bool attempt)
    {
        await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        if (attempt)
        {
            var row = new GenerationObservationAttemptRow { Id = Guid.NewGuid() }; db.Attach(row); row.Deadline = DateTimeOffset.UtcNow;
        }
        else
        {
            var row = new GenerationObservationRecordRow { Id = Guid.NewGuid() }; db.Attach(row); row.Payload = "changed";
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
