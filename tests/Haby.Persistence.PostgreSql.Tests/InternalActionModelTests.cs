using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class InternalActionModelTests
{
    [Fact]
    public void Immutable_intent_has_one_shared_state_key_and_protects_exact_administrative_revisions()
    {
        using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        var intent = db.Model.FindEntityType(typeof(DurableActionIntentRow))!;
        Assert.Equal(["DeploymentId", "Generation", "OperationId"], intent.FindPrimaryKey()!.Properties.Select(x => x.Name).ToArray());
        Assert.Contains(intent.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(DurableOperationRow) && x.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(intent.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(ProviderConfigurationRevisionRow) && x.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(intent.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(PlacementRevisionRow) && x.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.DoesNotContain(intent.GetProperties(), x => new[] { "Uid", "Namespace", "Projection", "KubernetesGeneration", "Endpoint", "Password" }.Contains(x.Name));
        var observations = db.Model.FindEntityType(typeof(DurableActionObservationRow))!;
        Assert.Contains(observations.GetIndexes(), x => x.IsUnique && x.GetFilter() == "confirmed");
        Assert.Contains(observations.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(DurableOperationAttemptRow));
    }
    [Fact]
    public async Task Tracked_intent_and_observation_mutations_refuse_before_database_access()
    {
        await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        var intent = new DurableActionIntentRow { DeploymentId = Guid.NewGuid(), Generation = 2, OperationId = "effect-request" };
        db.Attach(intent); intent.Payload = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var observation = new DurableActionObservationRow { Id = Guid.NewGuid() };
        db.Attach(observation); db.Remove(observation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
