using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PreviewImportedDatabaseTests
{
    private static readonly ManifestContractCatalog Catalog = new([PostgreSqlDatabaseContract.Definition], [], [], []);
    private static readonly PreviewProviderSelection Selected = new("main", "local-postgresql", 7,
        "haby.postgresql.database/v1alpha1");

    [Fact]
    public async Task Accepted_revision_has_deterministic_hypothetical_preview_without_state_changes()
    {
        var (preview, revision, store) = await Setup();
        var first = await preview.ExecuteAsync(revision, "test", [Selected]);
        Assert.Equal(first, await preview.ExecuteAsync(revision, "test", [Selected]));
        var ready = Assert.IsType<PreviewDatabaseResult.WouldCreate>(first);
        Assert.Equal("database", ready.ResourceId);
        Assert.Equal("sample_db", ready.DatabaseName);
        Assert.Equal("main", ready.ProviderAlias);
        Assert.Equal(7, ready.ConfigurationRevision);
        Assert.Single(store.Revisions);
    }

    [Fact]
    public async Task Missing_revision_alias_wrong_implementation_and_invalid_context_are_typed()
    {
        var (preview, revision, _) = await Setup();
        Assert.IsType<PreviewDatabaseResult.RevisionNotFound>(await preview.ExecuteAsync(
            ManifestRevisionRef.Create(revision.ApplicationRecordId, ManifestRevisionNumber.Create(999)), "test", [Selected]));
        Assert.IsType<PreviewDatabaseResult.AliasNotResolved>(await preview.ExecuteAsync(revision, "test",
            [Selected with { Alias = "other" }]));
        Assert.IsType<PreviewDatabaseResult.CapabilityMismatch>(await preview.ExecuteAsync(revision, "test",
            [Selected with { ImplementationId = "wrong-secret-value" }]));
        Assert.IsType<PreviewDatabaseResult.InvalidContext>(await preview.ExecuteAsync(revision, "Bad_Context", [Selected]));
        Assert.IsType<PreviewDatabaseResult.InvalidContext>(await preview.ExecuteAsync(revision, "test",
            [Selected, Selected]));
        Assert.IsType<PreviewDatabaseResult.InvalidContext>(await preview.ExecuteAsync(revision, "test",
            [Selected, Selected with { Alias = "unused" }]));
        Assert.IsType<PreviewDatabaseResult.InvalidContext>(await preview.ExecuteAsync(revision, "test",
            [Selected with { InstanceId = "secret=value" }]));
    }

    [Fact]
    public async Task Second_resource_fails_closed()
    {
        var (preview, revision, _) = await Setup(
            ",\"second\":{\"type\":\"postgresql.database/v1alpha1\",\"providerRef\":\"main\",\"properties\":{\"name\":\"second_db\"}}");
        Assert.IsType<PreviewDatabaseResult.UnsupportedShape>(await preview.ExecuteAsync(revision, "test", [Selected]));
    }

    private static async Task<(PreviewImportedDatabase Preview, ManifestRevisionRef Reference, InMemoryManifestImportStore Store)> Setup(
        string extraResource = "")
    {
        var store = new InMemoryManifestImportStore();
        var importer = new ImportApplicationManifest(store, Catalog, TimeProvider.System);
        var payload = Encoding.UTF8.GetBytes(
            "{\"applicationVersion\":\"1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"preview-test\"},\"spec\":{\"resources\":{\"database\":{\"type\":\"postgresql.database/v1alpha1\",\"providerRef\":\"main\",\"properties\":{\"name\":\"sample_db\"}}" + extraResource + "}}}}");
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await importer.ExecuteAsync(payload, "actor"));
        return (new PreviewImportedDatabase(new Query(store), new Capability()), imported.ManifestRevisionRef, store);
    }

    private sealed class Query(InMemoryManifestImportStore store) : IManifestRevisionQuery
    {
        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(store.Find(reference));
    }

    private sealed class Capability : ICheckpointProviderCapability
    {
        public bool Supports(ProviderResolution selection, ResourceTypeId contract) =>
            selection.ImplementationId == Selected.ImplementationId && contract == PostgreSqlDatabaseContract.Id;
    }
}
