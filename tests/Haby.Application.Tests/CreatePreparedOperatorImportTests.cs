using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class CreatePreparedOperatorImportTests
{
    [Fact]
    public async Task First_create_observes_server_identities_then_binds_before_material_and_linkage()
    {
        var f = await Fixture.Create();
        using var material = new SecretMaterial("private"u8.ToArray());
        Assert.Equal(OperatorImportRequestStatus.Created, (await f.Service.ExecuteAsync(f.Request, null, material, "operator", default)).Status);
        Assert.Equal(new[] { "lookup", "revision", "prepare", "bind", "stage", "link" }, f.Calls);
    }

    [Theory]
    [InlineData(OperatorImportRequestStatus.Pending)]
    [InlineData(OperatorImportRequestStatus.Created)]
    [InlineData(OperatorImportRequestStatus.Superseded)]
    public async Task Bound_replay_never_reprepares_or_retargets_when_provider_or_revision_reads_are_unavailable(OperatorImportRequestStatus status)
    {
        var f = await Fixture.Create();
        f.Store.Saved = f.Binding; f.Store.Status = status; f.Query.Unavailable = true; f.Adapter.Unavailable = true;
        using var material = new SecretMaterial("private"u8.ToArray());
        var result = await f.Service.ExecuteAsync(f.Request, null, material, "replay", default);
        Assert.Equal(status == OperatorImportRequestStatus.Superseded ? status : OperatorImportRequestStatus.Created, result.Status);
        Assert.Equal(status == OperatorImportRequestStatus.Superseded ? new[] { "lookup", "bind" } : new[] { "lookup", "bind", "stage", "link" }, f.Calls);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("revision")]
    [InlineData("environment")]
    [InlineData("correction")]
    public async Task Changed_replay_refuses_before_material_or_external_reads(string change)
    {
        var f = await Fixture.Create(); f.Store.Saved = f.Binding; f.Query.Unavailable = true; f.Adapter.Unavailable = true;
        var request = change switch
        {
            "names" => f.Request with { Input = new("fixture.names/v1", "{\"changed\":true}") },
            "revision" => f.Request with { Revision = ManifestRevisionRef.Create(f.Request.Revision.ApplicationRecordId, ManifestRevisionNumber.Create(2)) },
            "environment" => f.Request with { Environment = EnvironmentRecordId.New() },
            _ => f.Request
        };
        var correction = change == "correction" ? new OperatorImportCorrectionTarget(Guid.NewGuid(), null, null, OperatorImportCorrectionReason.ExplicitRetry) : null;
        using var material = new SecretMaterial("private"u8.ToArray()); material.Dispose();
        Assert.Equal(OperatorImportRequestStatus.Conflict, (await f.Service.ExecuteAsync(request, correction, material, "replay", default)).Status);
        Assert.Equal(new[] { "lookup" }, f.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_uuid_or_client_prepared_payload_never_reaches_catalog_or_material(bool clientPrepared)
    {
        var f = await Fixture.Create();
        var request = clientPrepared ? f.Request with { Input = new("fixture.prepared/v1", "{}") } : f.Request with { RequestId = Guid.Empty };
        using var material = new SecretMaterial("private"u8.ToArray()); material.Dispose();
        Assert.Equal(clientPrepared ? OperatorImportRequestStatus.ImportNotSupported : OperatorImportRequestStatus.InvalidInput,
            (await f.Service.ExecuteAsync(request, null, material, "operator", default)).Status);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task Unavailable_binding_lookup_does_not_prepare_a_new_target()
    {
        var f = await Fixture.Create(); f.Store.Status = OperatorImportRequestStatus.StorageUnavailable;
        using var material = new SecretMaterial("private"u8.ToArray()); material.Dispose();
        Assert.Equal(OperatorImportRequestStatus.StorageUnavailable, (await f.Service.ExecuteAsync(f.Request, null, material, "operator", default)).Status);
        Assert.Equal(new[] { "lookup" }, f.Calls);
    }

    [Fact]
    public async Task Protected_preparation_can_bind_one_new_group_without_resubmitting_retained_resources()
    {
        var f = await Fixture.Create(true);
        var result = await new PrepareResourceImport(f.Query, [f.Adapter]).ExecuteAsync(f.Request, default);
        Assert.Equal(ResourceImportPreparationStatus.Prepared, result.Status);
        Assert.Equal(f.Binding, result.Binding);
        Assert.Equal(new[] { "revision", "prepare" }, f.Calls);
    }

    [Fact]
    public async Task Protected_staging_freezes_binding_before_material_without_accepting_a_ChangeSet()
    {
        var f = await Fixture.Create(true);
        using var material = new SecretMaterial("private"u8.ToArray());
        var request = f.Request with { CredentialSlot = "application-password" };
        Assert.Equal(OperatorImportRequestStatus.Pending, (await f.Service.ExecuteAsync(request, null, material, "operator", default, stageOnly: true)).Status);
        Assert.Equal(new[] { "lookup", "revision", "prepare", "bind", "stage" }, f.Calls);
        f.Calls.Clear(); f.Query.Unavailable = true; f.Adapter.Unavailable = true;
        Assert.Equal(OperatorImportRequestStatus.Pending, (await f.Service.ExecuteAsync(request, null, material, "operator", default, stageOnly: true)).Status);
        Assert.Equal(new[] { "lookup", "bind", "stage" }, f.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("provider-administrator")]
    public async Task Missing_or_wrong_import_slot_cannot_read_catalog_or_material(string? slot)
    {
        var f = await Fixture.Create(true);
        using var material = new SecretMaterial("private"u8.ToArray()); material.Dispose();
        Assert.Equal(OperatorImportRequestStatus.ImportNotSupported,
            (await f.Service.ExecuteAsync(f.Request with { CredentialSlot = slot }, null, material, "operator", default, stageOnly: true)).Status);
        Assert.Empty(f.Calls);
    }

    private sealed record Fixture(CreatePreparedOperatorImport Service, ResourceImportPreparationRequest Request,
        OperatorImportRequestBinding Binding, List<string> Calls, Store Store, Query Query, Adapter Adapter)
    {
        internal static async Task<Fixture> Create(bool retained = false)
        {
            var revisions = new InMemoryManifestImportStore();
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(revisions,
                new([new(ResourceTypeId.Create("sample.resource/v1"), "{}")], [], [], []), TimeProvider.System).ExecuteAsync("""
                {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},
                  "spec":{"resources":{"db":{"type":"sample.resource/v1","providerRef":"main","properties":{}},
                  "login":{"type":"sample.resource/v1","providerRef":"main","properties":{}}}}}}
                """u8.ToArray(), "operator"));
            if (retained)
            {
                var next = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(revisions,
                    new([new(ResourceTypeId.Create("sample.resource/v1"), "{}")], [], [], []), TimeProvider.System).ExecuteAsync("""
                    {"applicationVersion":"2","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},
                      "spec":{"resources":{"db":{"type":"sample.resource/v1","providerRef":"main","properties":{}},
                      "login":{"type":"sample.resource/v1","providerRef":"main","properties":{}},
                      "retained":{"type":"sample.resource/v1","providerRef":"main","properties":{}}}}}}
                    """u8.ToArray(), "operator"));
                imported = next;
            }
            var calls = new List<string>();
            var request = new ResourceImportPreparationRequest(Guid.NewGuid(), EnvironmentRecordId.New(), imported.ManifestRevisionRef, new("fixture.names/v1", "{}"));
            var binding = new OperatorImportRequestBinding(request.RequestId, request.Environment, request.Revision, ManifestLocalId.Create("login"),
                new([new(ManifestLocalId.Create("db"), new("fixture.prepared/v1", "{}")), new(ManifestLocalId.Create("login"), new("fixture.prepared/v1", "{}"))]));
            var store = new Store(calls); var query = new Query(calls, revisions); var adapter = new Adapter(calls, binding);
            return new(new(new(query, [adapter]), store, new(store, new Intake(calls))), request, binding, calls, store, query, adapter);
        }
    }

    private sealed class Query(List<string> calls, InMemoryManifestImportStore revisions) : IManifestRevisionQuery
    {
        internal bool Unavailable;
        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference, CancellationToken cancellationToken = default)
        { if (Unavailable) throw new IOException("Revision reads are unavailable."); calls.Add("revision"); return ValueTask.FromResult(revisions.Find(reference)); }
    }
    private sealed class Adapter(List<string> calls, OperatorImportRequestBinding binding) : IResourceImportPreparer
    {
        internal bool Unavailable;
        public string InputFormat => "fixture.names/v1";
        public ImportCredentialSlot CredentialSlot => new("application-password", SecretPurpose.ApplicationLogin);
        public bool MatchesPreparedRequest(ResourceImportPreparationRequest request, OperatorImportRequestBinding saved) => request.Input.Json == "{}" && saved.Input == binding.Input;
        public ValueTask<ResourceImportPreparationResult> PrepareAsync(ResourceImportPreparationRequest request, NormalizedManifestSpec spec, CancellationToken token)
        { if (Unavailable) throw new IOException("Provider is unavailable."); calls.Add("prepare"); return ValueTask.FromResult(new ResourceImportPreparationResult(ResourceImportPreparationStatus.Prepared, binding)); }
    }
    private sealed class Store(List<string> calls) : IOperatorImportRequestStore, IOperatorImportRequestBindings
    {
        internal OperatorImportRequestBinding? Saved;
        internal OperatorImportRequestStatus Status = OperatorImportRequestStatus.Pending;
        public ValueTask<OperatorImportBindingResult> FindBindingAsync(Guid requestId, CancellationToken token)
        { calls.Add("lookup"); return ValueTask.FromResult(new OperatorImportBindingResult(Status == OperatorImportRequestStatus.StorageUnavailable ? Status : Saved is null ? OperatorImportRequestStatus.NotFound : Status, Saved)); }
        public ValueTask<OperatorImportRequestResult> BindAsync(OperatorImportRequestBinding binding, string actor, CancellationToken token)
        { calls.Add("bind"); Saved = binding; return ValueTask.FromResult(new OperatorImportRequestResult(Status)); }
        public ValueTask<OperatorImportRequestResult> BindGroupAsync(OperatorImportRequestBinding binding, string actor, CancellationToken token) => BindAsync(binding, actor, token);
        public ValueTask<OperatorImportRequestResult> CompleteAsync(OperatorImportRequestBinding binding, ProtectedCredentialReference credential, string actor, CancellationToken token)
        { calls.Add("link"); return ValueTask.FromResult(new OperatorImportRequestResult(OperatorImportRequestStatus.Created)); }
        public ValueTask<OperatorImportRequestResult> GetAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Intake(List<string> calls) : IProtectedApplicationCredentialIntake
    {
        public ValueTask<ProtectedCredentialStageResult> StageAsync(Guid requestId, ManagedResourceKey owner, SecretMaterial material, CancellationToken cancellationToken)
        { Assert.Equal("bind", calls[^1]); calls.Add("stage"); _ = material.CopyBytes(); return ValueTask.FromResult(new ProtectedCredentialStageResult(ProtectedCredentialStageStatus.Staged, new(Guid.NewGuid(), new(SecretId.New(), SecretVersionId.New())))); }
        public ValueTask<SecretMaterial?> ResolveStagedAsync(ProtectedCredentialReference reference, ManagedResourceKey owner, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
