using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public class ImportApplicationManifestTests
{
    private const string Schema = """{"type":"object","required":["enabled"],"properties":{"enabled":{"type":"boolean"}}}""";
    private static readonly ManifestContractCatalog Catalog = new(
        [new ResourceTypeDefinition(ResourceTypeId.Create("example.resource/v1"), Schema)], [], [], []);
    private static readonly DateTimeOffset FirstTime = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Invalid_inputs_and_validation_leave_no_application()
    {
        var store = new InMemoryManifestImportStore();
        var import = Create(store);
        Assert.IsType<ImportApplicationManifestResult.InvalidRequest>(await import.ExecuteAsync(
            Encoding.UTF8.GetBytes("""{"applicationVersion":"bad value","manifest":{}}"""), "actor"));
        Assert.IsType<ImportApplicationManifestResult.InvalidManifest>(await import.ExecuteAsync(
            Envelope("1", "\"metadata\":{\"id\":\"Bad_Id\"}"), "actor"));
        Assert.IsType<ValidateApplicationManifestResult.Valid>(ValidateApplicationManifest.Execute(Envelope("1"), Catalog));
        Assert.Empty(store.Applications);
        Assert.Empty(store.Revisions);
    }

    [Fact]
    public async Task First_import_retry_conflict_and_new_version_preserve_history()
    {
        var store = new InMemoryManifestImportStore();
        var import = Create(store);
        var first = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await import.ExecuteAsync(Envelope("1", "\"displayName\":\"first\"", warning: true), "first-actor"));
        Assert.Equal(ImportApplicationManifestResult.ImportDisposition.Created, first.Disposition);
        Assert.Equal(1, first.ManifestRevisionRef.RevisionNumber.Value);
        Assert.Single(first.HistoricalWarnings.Diagnostics);
        Assert.Equal(FirstTime, first.AuditMetadata.ImportedAt);

        var repeat = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await import.ExecuteAsync(Envelope("1", "\"displayName\":\"first\"", warning: true), "second-actor"));
        Assert.Equal(ImportApplicationManifestResult.ImportDisposition.ExistingEquivalent, repeat.Disposition);
        Assert.Equal(first.ManifestRevisionRef, repeat.ManifestRevisionRef);
        Assert.Equal(first.HistoricalWarnings.Truncated, repeat.HistoricalWarnings.Truncated);
        var firstWarning = Assert.Single(first.HistoricalWarnings.Diagnostics);
        var historicalWarning = Assert.Single(repeat.HistoricalWarnings.Diagnostics);
        Assert.Equal(firstWarning.Code, historicalWarning.Code);
        Assert.Equal(firstWarning.Severity, historicalWarning.Severity);
        Assert.Equal(firstWarning.Path, historicalWarning.Path);
        Assert.Equal(firstWarning.Parameters.OrderBy(item => item.Key),
            historicalWarning.Parameters.OrderBy(item => item.Key));
        Assert.Equal(firstWarning.RelatedPaths, historicalWarning.RelatedPaths);
        Assert.Equal(first.AuditMetadata, repeat.AuditMetadata);
        Assert.Equal("first-actor", repeat.AuditMetadata.ActorId);

        var conflict = Assert.IsType<ImportApplicationManifestResult.ApplicationVersionContentConflict>(
            await import.ExecuteAsync(Envelope("1", "\"displayName\":\"secret-value\""), "third-actor"));
        Assert.Equal(first.ManifestRevisionRef, conflict.ExistingManifestRevisionRef);
        Assert.Equal(["/metadata/displayName", "/spec/resources/database"], conflict.ChangedPaths);
        Assert.DoesNotContain("secret-value", string.Join("", conflict.ChangedPaths));

        var next = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await import.ExecuteAsync(Envelope("2", "\"displayName\":\"first\"", warning: true), "actor"));
        Assert.Equal(2, next.ManifestRevisionRef.RevisionNumber.Value);
        Assert.Equal(first.ManifestRevisionRef.ApplicationRecordId, next.ManifestRevisionRef.ApplicationRecordId);
        Assert.Single(store.Applications);
        Assert.Equal(2, store.Revisions.Count);
    }

    [Fact]
    public async Task Equivalent_and_different_races_have_one_winner()
    {
        foreach (var different in new[] { false, true })
        {
            var inner = new InMemoryManifestImportStore();
            var barrier = new TwoCallerStore(inner);
            var import = Create(barrier);
            var one = import.ExecuteAsync(Envelope("1", "\"displayName\":\"one\""), "one").AsTask();
            var two = import.ExecuteAsync(Envelope("1", different ? "\"displayName\":\"two\"" : "\"displayName\":\"one\""), "two").AsTask();
            var results = await Task.WhenAll(one, two);
            Assert.Single(inner.Applications);
            var stored = Assert.Single(inner.Revisions);
            var created = Assert.Single(results.OfType<ImportApplicationManifestResult.Succeeded>(),
                result => result.Disposition == ImportApplicationManifestResult.ImportDisposition.Created);
            if (different)
            {
                var conflict = Assert.Single(results.OfType<ImportApplicationManifestResult.ApplicationVersionContentConflict>());
                Assert.Equal(["/metadata/displayName"], conflict.ChangedPaths);
                Assert.Equal(stored.Reference, conflict.ExistingManifestRevisionRef);
                Assert.Equal(created.AuditMetadata.ActorId, stored.Declaration.Metadata.DisplayName);
            }
            else
            {
                Assert.Single(results.OfType<ImportApplicationManifestResult.Succeeded>(),
                    result => result.Disposition == ImportApplicationManifestResult.ImportDisposition.ExistingEquivalent);
                Assert.Equal(((ImportApplicationManifestResult.Succeeded)results[0]).ManifestRevisionRef,
                    ((ImportApplicationManifestResult.Succeeded)results[1]).ManifestRevisionRef);
            }
        }
    }

    [Fact]
    public async Task Formatting_and_object_order_do_not_change_version_identity()
    {
        var store = new InMemoryManifestImportStore();
        var import = Create(store);
        var first = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await import.ExecuteAsync(Envelope("1", "\"displayName\":\"Example\""), "actor"));
        var reordered = Encoding.UTF8.GetBytes("""{"manifest":{"spec":{},"metadata":{"displayName":"Example","id":"example"},"kind":"Application","apiVersion":"haby.dev/v1alpha1"},"applicationVersion":"1"}""");
        var repeat = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await import.ExecuteAsync(reordered, "other-actor"));
        Assert.Equal(ImportApplicationManifestResult.ImportDisposition.ExistingEquivalent, repeat.Disposition);
        Assert.Equal(first.ManifestRevisionRef, repeat.ManifestRevisionRef);
        Assert.Single(store.Revisions);
    }

    [Fact]
    public async Task Cancellation_before_commit_creates_nothing_and_after_confirmed_commit_returns_success()
    {
        var inner = new InMemoryManifestImportStore();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Create(inner).ExecuteAsync(Envelope("1"), "actor", source.Token));
        Assert.Empty(inner.Applications);

        using var beforeCommit = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Create(new CancelBeforeCommitStore(inner, beforeCommit)).ExecuteAsync(
                Envelope("1"), "actor", beforeCommit.Token));
        Assert.Empty(inner.Applications);
        Assert.Empty(inner.Revisions);

        using var after = new CancellationTokenSource();
        var import = Create(new CancelAfterCommitStore(inner, after));
        var result = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await import.ExecuteAsync(Envelope("1"), "actor", after.Token));
        Assert.Equal(ImportApplicationManifestResult.ImportDisposition.Created, result.Disposition);
        Assert.Single(inner.Revisions);
    }

    [Fact]
    public async Task Conflict_paths_are_ordinal_and_bounded()
    {
        var store = new InMemoryManifestImportStore();
        var import = Create(store);
        var resources = string.Join(",", Enumerable.Range(0, 105).Select(i =>
            $"\"resource{i:D3}\":{{\"type\":\"example.resource/v1\",\"providerRef\":\"main\",\"properties\":{{\"enabled\":true}}}}"));
        var changed = string.Join(",", Enumerable.Range(0, 105).Select(i =>
            $"\"resource{i:D3}\":{{\"type\":\"example.resource/v1\",\"providerRef\":\"other\",\"properties\":{{\"enabled\":true}}}}"));
        var first = EnvelopeWithSpec("1", "\"resources\":{" + resources + "}");
        var second = EnvelopeWithSpec("1", "\"resources\":{" + changed + "}");
        Assert.IsType<ImportApplicationManifestResult.Succeeded>(await import.ExecuteAsync(first, "actor"));
        var conflict = Assert.IsType<ImportApplicationManifestResult.ApplicationVersionContentConflict>(
            await import.ExecuteAsync(second, "actor"));
        Assert.True(conflict.ChangedPathsTruncated);
        Assert.Equal(100, conflict.ChangedPaths.Count);
        Assert.Equal("/spec/resources/resource000/providerRef", conflict.ChangedPaths[0]);
        Assert.Equal("/spec/resources/resource099/providerRef", conflict.ChangedPaths[^1]);
        Assert.DoesNotContain("secret", string.Join("", conflict.ChangedPaths));
    }

    [Fact]
    public async Task Conflict_paths_escape_slash_and_tilde_without_collisions()
    {
        var store = new InMemoryManifestImportStore();
        var import = Create(store);
        static byte[] Payload(string properties) => EnvelopeWithSpec("1",
            "\"resources\":{\"db\":{\"type\":\"example.resource/v1\",\"providerRef\":\"main\",\"properties\":{\"enabled\":true," + properties + "}}}");

        Assert.IsType<ImportApplicationManifestResult.Succeeded>(await import.ExecuteAsync(
            Payload("\"a/b\":true"), "actor"));
        var conflict = Assert.IsType<ImportApplicationManifestResult.ApplicationVersionContentConflict>(
            await import.ExecuteAsync(Payload("\"a/b\":false,\"a~1b\":true"), "actor"));

        Assert.Equal([
            "/spec/resources/db/properties/a~01b",
            "/spec/resources/db/properties/a~1b"
        ], conflict.ChangedPaths);
        Assert.False(conflict.ChangedPathsTruncated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("actor\u0001")]
    public async Task Invalid_trusted_actor_identity_is_rejected_before_import(string actorId)
    {
        var store = new InMemoryManifestImportStore();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Create(store).ExecuteAsync(Envelope("1"), actorId));
        Assert.Empty(store.Applications);
    }

    [Fact]
    public async Task Oversized_trusted_actor_identity_is_rejected_before_import()
    {
        var store = new InMemoryManifestImportStore();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Create(store).ExecuteAsync(Envelope("1"), new string('a', 257)));
        Assert.Empty(store.Applications);
    }

    private static ImportApplicationManifest Create(IManifestImportStore store) => new(store, Catalog,
        new FixedTimeProvider(FirstTime));

    private static byte[] Envelope(string version, string metadata = "", bool warning = false) => Encoding.UTF8.GetBytes(
        "{\"applicationVersion\":\"" + version + "\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"example\"" +
        (metadata.Length == 0 ? "" : "," + metadata) + "},\"spec\":{" + (warning ?
            "\"resources\":{\"database\":{\"type\":\"example.resource/v1\",\"providerRef\":\"main\",\"properties\":{\"enabled\":true}}}" : "") + "}}}");

    private static byte[] EnvelopeWithSpec(string version, string spec) => Encoding.UTF8.GetBytes(
        "{\"applicationVersion\":\"" + version + "\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"example\"},\"spec\":{" + spec + "}}}");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TwoCallerStore(IManifestImportStore inner) : IManifestImportStore
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public async ValueTask<ManifestCompareAndCreateResult> CompareAndCreateAsync(
            ValidatedManifestCandidate candidate, ImportAuditMetadata audit, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _both.SetResult();
            await _both.Task.WaitAsync(cancellationToken);
            return await inner.CompareAndCreateAsync(candidate, audit, cancellationToken);
        }
    }

    private sealed class CancelAfterCommitStore(IManifestImportStore inner, CancellationTokenSource source) : IManifestImportStore
    {
        public async ValueTask<ManifestCompareAndCreateResult> CompareAndCreateAsync(
            ValidatedManifestCandidate candidate, ImportAuditMetadata audit, CancellationToken cancellationToken)
        {
            var result = await inner.CompareAndCreateAsync(candidate, audit, cancellationToken);
            source.Cancel();
            return result;
        }
    }

    private sealed class CancelBeforeCommitStore(IManifestImportStore inner, CancellationTokenSource source) : IManifestImportStore
    {
        public ValueTask<ManifestCompareAndCreateResult> CompareAndCreateAsync(
            ValidatedManifestCandidate candidate, ImportAuditMetadata audit, CancellationToken cancellationToken)
        {
            source.Cancel();
            return inner.CompareAndCreateAsync(candidate, audit, cancellationToken);
        }
    }
}
