using System.Text;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class OperatorImportRequestTests
{
    [Fact]
    public async Task Binding_is_durable_before_private_input_and_linking_follows_verified_staging()
    {
        var calls = new List<string>();
        var binding = Binding();
        var store = new Store(calls);
        var intake = new Intake(calls, binding);
        using var material = new SecretMaterial(Encoding.UTF8.GetBytes(" exact input\n"));
        var result = await new OperatorImportRequests(store, intake).CreateAsync(binding, material, "operator:first", default);
        Assert.Equal(OperatorImportRequestStatus.Created, result.Status);
        Assert.Equal(new[] { "bind", "stage", "link" }, calls);
    }

    [Theory]
    [InlineData(ProtectedCredentialStageStatus.Conflict, OperatorImportRequestStatus.Conflict)]
    [InlineData(ProtectedCredentialStageStatus.StorageUnavailable, OperatorImportRequestStatus.StorageUnavailable)]
    public async Task Existing_link_never_bypasses_same_material_verification(ProtectedCredentialStageStatus stage,
        OperatorImportRequestStatus expected)
    {
        var calls = new List<string>();
        var binding = Binding();
        var store = new Store(calls) { Bound = OperatorImportRequestStatus.Created };
        var intake = new Intake(calls, binding) { Status = stage };
        using var material = new SecretMaterial("changed"u8.ToArray());
        var result = await new OperatorImportRequests(store, intake).CreateAsync(binding, material, "operator:replay", default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(new[] { "bind", "stage" }, calls);
    }

    [Fact]
    public async Task Binding_conflict_does_not_touch_material_and_cancellation_does_not_link()
    {
        var calls = new List<string>();
        var binding = Binding();
        var store = new Store(calls) { Bound = OperatorImportRequestStatus.Conflict };
        using var material = new SecretMaterial("private"u8.ToArray());
        var service = new OperatorImportRequests(store, new Intake(calls, binding));
        Assert.Equal(OperatorImportRequestStatus.Conflict, (await service.CreateAsync(binding, material, "operator", default)).Status);
        Assert.Equal(new[] { "bind" }, calls);
        calls.Clear();
        store.Bound = OperatorImportRequestStatus.Pending;
        using var canceled = new CancellationTokenSource();
        service = new(store, new Intake(calls, binding) { AfterStage = canceled.Cancel });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(binding, material, "operator", canceled.Token).AsTask());
        Assert.Equal(new[] { "bind", "stage" }, calls);
    }

    private static OperatorImportRequestBinding Binding() => new(Guid.NewGuid(), EnvironmentRecordId.New(),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), ManifestLocalId.Create("login"),
        new([new(ManifestLocalId.Create("database"), new("fixture.import/v1", "{}")),
            new(ManifestLocalId.Create("login"), new("fixture.import/v1", "{}"))]));

    private sealed class Store(List<string> calls) : IOperatorImportRequestStore
    {
        internal OperatorImportRequestStatus Bound = OperatorImportRequestStatus.Pending;
        public ValueTask<OperatorImportRequestResult> BindAsync(OperatorImportRequestBinding binding, string actor, CancellationToken token)
        { calls.Add("bind"); return ValueTask.FromResult(new OperatorImportRequestResult(Bound)); }
        public ValueTask<OperatorImportRequestResult> CompleteAsync(OperatorImportRequestBinding binding,
            ProtectedCredentialReference credential, string actor, CancellationToken token)
        { calls.Add("link"); return ValueTask.FromResult(new OperatorImportRequestResult(OperatorImportRequestStatus.Created)); }
        public ValueTask<OperatorImportRequestResult> GetAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Intake(List<string> calls, OperatorImportRequestBinding expected) : IProtectedApplicationCredentialIntake
    {
        internal ProtectedCredentialStageStatus Status = ProtectedCredentialStageStatus.Staged;
        internal Action? AfterStage;
        public ValueTask<ProtectedCredentialStageResult> StageAsync(Guid requestId, ManagedResourceKey owner,
            SecretMaterial material, CancellationToken cancellationToken)
        {
            Assert.Equal(new[] { "bind" }, calls);
            Assert.Equal(expected.RequestId, requestId);
            Assert.Equal(expected.CredentialOwner, owner);
            calls.Add("stage");
            AfterStage?.Invoke();
            return ValueTask.FromResult(new ProtectedCredentialStageResult(Status,
                Status is ProtectedCredentialStageStatus.Staged or ProtectedCredentialStageStatus.Existing
                    ? new(Guid.NewGuid(), new(SecretId.New(), SecretVersionId.New())) : null));
        }
        public ValueTask<SecretMaterial?> ResolveStagedAsync(ProtectedCredentialReference reference, ManagedResourceKey owner,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
