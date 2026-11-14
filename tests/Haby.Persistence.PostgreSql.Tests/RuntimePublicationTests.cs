using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class RuntimePublicationTests
{
    [Theory]
    [InlineData("Prepared", false)]
    [InlineData("Revoked", true)]
    [InlineData("Active", false)]
    [InlineData("Revoked", false)]
    [InlineData("Prepared", true)]
    public void Lifecycle_is_closed_and_independent_from_origin(string status, bool audit)
    {
        var binding = Binding(); binding.Status = status;
        if (audit) { binding.RevokedBy = "operator"; binding.RevokedAt = DateTimeOffset.UtcNow; }
        if (status == "Prepared" && !audit || status == "Revoked" && audit) Assert.Equal(status, InternalRuntimePublication.BindingState(binding).Status);
        else Assert.Throws<InvalidDataException>(() => InternalRuntimePublication.BindingState(binding));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Binding_origin_changes_and_deletion_remain_prohibited(bool delete)
    {
        await using var db = Context(); var binding = Binding(); db.Attach(binding);
        if (delete) db.Remove(binding); else binding.ValidationPayload = "changed";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("origin is immutable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoked_binding_cannot_be_reactivated_by_a_tracked_writer()
    {
        await using var db = Context(); var binding = Binding(); binding.Status = "Revoked";
        binding.RevokedBy = "operator"; binding.RevokedAt = DateTimeOffset.UtcNow; db.Attach(binding);
        binding.Status = "Prepared"; binding.RevokedAt = null; binding.RevokedBy = null; binding.ConcurrencyToken = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Original_receipt_round_trip_does_not_serialize_computed_authority_or_lose_precondition()
    {
        var origin = new RuntimePublicationOrigin(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "tester",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "api", "main", "settings", "fingerprint", Guid.NewGuid(), 1, "effect-runtime");
        var request = new RuntimePublicationRequest(InternalRuntimePublicationPreparation.Key(origin, Guid.NewGuid()),
            RuntimePublicationOperation.PublishPermission, Guid.NewGuid(), Guid.NewGuid(), null, "tester", origin);
        var receipt = new RuntimePublicationReceipt(Guid.NewGuid(), request, RuntimePublicationOutcome.Published, DateTimeOffset.UtcNow,
            null, null, new(request.Binding, "Prepared", Guid.NewGuid(), null, null));
        var json = RuntimePublicationCodec.Encode(receipt);
        Assert.DoesNotContain("Accepted", json, StringComparison.Ordinal);
        Assert.Equal(receipt, RuntimePublicationCodec.Receipt(json));
        Assert.Equal(RuntimePublicationCodec.Encode(origin), RuntimePublicationCodec.Encode(RuntimePublicationCodec.Origin(RuntimePublicationCodec.Encode(origin))));
        Assert.Throws<InvalidDataException>(() => RuntimePublicationCodec.Origin(RuntimePublicationCodec.Encode(origin with { Version = 2 })));
        Assert.Throws<System.Text.Json.JsonException>(() => RuntimePublicationCodec.Receipt(json[..^1] + ",\"extra\":true}"));
    }

    [Fact]
    public void Permission_and_request_constraints_remain_generic_and_exact()
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(typeof(RuntimeConfigurationPermissionRow))!;
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(["BindingId", "RevisionId"]));
        Assert.True(entity.FindProperty("ConcurrencyToken")!.IsConcurrencyToken);
        Assert.All(entity.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        Assert.DoesNotContain(entity.GetProperties(), x => new[] { "Namespace", "Uid", "TokenHash", "Password", "Endpoint" }.Contains(x.Name));
        var sql = db.GetService<IMigrator>().GenerateScript("20261114033757_StableWorkloadRuntimeIdentity");
        Assert.DoesNotContain("CREATE FUNCTION", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TRIGGER", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static ManifestCatalogDbContext Context() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql().Options);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_failure_is_translated_without_exposing_driver_diagnostics(bool update)
    {
        var publication = new InternalRuntimePublication(new UnavailablePublicationDatabase(update), null!, null!, TimeProvider.System);
        var request = new RuntimePublicationRequest("storage-failure", RuntimePublicationOperation.RevokeBinding, Guid.NewGuid(), null, Guid.NewGuid(), "operator");
        var error = await Assert.ThrowsAsync<IOException>(() => publication.ApplyAsync(request, default));
        Assert.Equal("Runtime publication storage unavailable.", error.Message);
        Assert.Null(error.InnerException); Assert.DoesNotContain("private-storage-diagnostic", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class UnavailablePublicationDatabase(bool update) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => throw Failure();
        public Task<ManifestCatalogDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<ManifestCatalogDbContext>(Failure());
        private Exception Failure() => update ? new DbUpdateException("private-storage-diagnostic") : new Npgsql.NpgsqlException("private-storage-diagnostic");
    }
    private static RuntimeIdentityBindingRow Binding() => new()
    {
        Id = Guid.NewGuid(),
        InstallationId = Guid.NewGuid(),
        ManagedWorkloadId = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(),
        ProviderId = Guid.NewGuid(),
        ConfigurationRevision = 1,
        Authenticator = "fixture",
        Audience = "fixture",
        Status = "Prepared",
        Role = "runtime",
        Contract = "fixture/v1",
        Format = 1,
        ValidationPayload = "{}"
    };
}
