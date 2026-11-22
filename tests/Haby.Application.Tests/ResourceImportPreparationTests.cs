using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ResourceImportPreparationTests
{
    [Fact]
    public async Task Invalid_uuid_is_refused_before_revision_or_adapter_access()
    {
        var result = await new PrepareResourceImport(new NoRead(), [new NoPrepare()])
            .ExecuteAsync(Request() with { RequestId = Guid.Empty }, default);
        Assert.Equal(ResourceImportPreparationStatus.InvalidInput, result.Status);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task Unknown_format_never_falls_back_to_an_adapter()
    {
        var result = await new PrepareResourceImport(new NoRead(), [new NoPrepare()])
            .ExecuteAsync(Request() with { Input = new("unsupported/v1", "{}") }, default);
        Assert.Equal(ResourceImportPreparationStatus.ImportNotSupported, result.Status);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task Cancellation_prevents_preparation_io()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PrepareResourceImport(new NoRead(), [new NoPrepare()])
            .ExecuteAsync(Request(), canceled.Token).AsTask());
    }

    [Fact]
    public async Task Missing_exact_revision_cannot_reach_adapter()
    {
        var request = Request();
        var query = new MissingRevision(request.Revision);
        var result = await new PrepareResourceImport(query, [new NoPrepare()]).ExecuteAsync(request, default);
        Assert.Equal(ResourceImportPreparationStatus.RevisionNotFound, result.Status);
        Assert.True(query.Read);
        Assert.Null(result.Binding);
    }

    private static ResourceImportPreparationRequest Request() => new(Guid.NewGuid(), EnvironmentRecordId.New(),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), new("fixture.names/v1", "{}"));

    private sealed class NoRead : IManifestRevisionQuery
    {
        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preparation must not read a revision on this path.");
    }
    private sealed class MissingRevision(ManifestRevisionRef expected) : IManifestRevisionQuery
    {
        public bool Read;
        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference, CancellationToken cancellationToken = default)
        { Assert.Equal(expected, reference); Read = true; return ValueTask.FromResult<ManifestRevision?>(null); }
    }
    private sealed class NoPrepare : IResourceImportPreparer
    {
        public string InputFormat => "fixture.names/v1";
        public ValueTask<ResourceImportPreparationResult> PrepareAsync(ResourceImportPreparationRequest request, NormalizedManifestSpec spec, CancellationToken token) =>
            throw new InvalidOperationException("Preparation must not reach an adapter on this path.");
    }
}
