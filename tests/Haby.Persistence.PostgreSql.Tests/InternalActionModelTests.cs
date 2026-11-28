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
    [Theory]
    [InlineData("delete")]
    [InlineData("deployment")]
    [InlineData("role")]
    [InlineData("contract")]
    [InlineData("format")]
    [InlineData("generation")]
    [InlineData("origin-generation")]
    [InlineData("origin-operation")]
    [InlineData("missing-receipt")]
    public async Task Latest_projection_requires_exact_forward_origin_immutable_target_and_atomic_receipt(string fault)
    {
        await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);
        var row = new ManagedObjectStateRow
        {
            ManagedWorkloadId = Guid.CreateVersion7(),
            DeploymentId = Guid.CreateVersion7(),
            Role = "deployment",
            Contract = "confirmed-deployment/v1",
            Format = 1,
            Payload = "old",
            OriginGeneration = 1,
            OriginOperation = "effect-initial",
            Generation = 2,
            OperationId = "effect-previous"
        };
        db.Attach(row);
        row.OriginGeneration = 2; row.OriginOperation = "effect-previous"; row.Generation = 3; row.OperationId = "effect-next"; row.Payload = "new";
        switch (fault)
        {
            case "delete": db.Remove(row); break;
            case "deployment": row.DeploymentId = Guid.CreateVersion7(); break;
            case "role": row.Role = "other"; break;
            case "contract": row.Contract = "different/v1"; break;
            case "format": row.Format = 2; break;
            case "generation": row.Generation = 2; break;
            case "origin-generation": row.OriginGeneration = 1; break;
            case "origin-operation": row.OriginOperation = "effect-foreign"; break;
        }
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        if (fault == "role")
        { Assert.StartsWith("The property 'ManagedObjectStateRow.Role' is part of a key", failure.Message); return; }
        Assert.Equal(fault == "missing-receipt" ? "Current object projection must accompany its confirmed receipt transaction." :
            "Current object projection requires an exact forward transition.", failure.Message);
    }

}
