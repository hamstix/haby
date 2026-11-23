using Haby.Domain;
using Haby.Tests;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ResourceAcquisitionTests
{
    private static readonly ManifestLocalId First = ManifestLocalId.Create("first");
    private static readonly ManifestLocalId Second = ManifestLocalId.Create("second");
    private static readonly DurablePlanningInputs Inputs = DurableInputSamples.Create(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), ProviderAlias.Create("sample.primary"), ProviderInstanceId.New(),
        ProviderConfigurationRevisionNumber.Create(3), "sample.impl/v1", "first", "second");
    private static readonly DurableActionPayload Input = new("test.reference/v1", "{\"Identity\":\"service-1\"}");
    private static ResourceAcquisitionIntent Intent() => new([new(First, ResourceAcquisitionMode.Reference, Input), new(Second, ResourceAcquisitionMode.ImportUseAndRetain, Input)]);
    private static DurableOperationNode Node(ManifestLocalId resource, ResourceAcquisitionMode mode, string id)
    {
        var p = Inputs.Providers.Resource(resource);
        return new(OperationId.Create(id), resource,
            new(new(ResourceTypeId.Create("sample.resource/v1"), new ProviderAdapterKey("sample/v1", "sample.configuration/v1", "sample.impl/v1")) { Mode = mode }, OperationContractId.Create("sample.observe/v1")),
            new(p.Environment, p.Instance, p.Configuration, p.Implementation.Value), Input, [])
        { RequiresExternalOutput = true, RequiredOutput = new("sample.capability/v1", "access") };
    }
    [Fact]
    public void Acquisition_set_rejects_missing_foreign_duplicate_unknown_and_credential_escalation()
    {
        Assert.Throws<InvalidDataException>(() => new ResourceAcquisitionIntent([]).Validate(Inputs.Providers.Declaration.Spec));
        Assert.Throws<InvalidDataException>(() => new ResourceAcquisitionIntent([new(First, ResourceAcquisitionMode.Reference, Input)]).Validate(Inputs.Providers.Declaration.Spec));
        Assert.Throws<InvalidDataException>(() => new ResourceAcquisitionIntent([new(First, ResourceAcquisitionMode.Reference, Input), new(ManifestLocalId.Create("foreign"), ResourceAcquisitionMode.Provision)]).Validate(Inputs.Providers.Declaration.Spec));
        Assert.Throws<ArgumentException>(() => new ResourceAcquisitionIntent([new(First, ResourceAcquisitionMode.Provision), new(First, ResourceAcquisitionMode.Provision)]));
        Assert.Throws<ArgumentException>(() => new ResourceAcquisitionIntent([new(First, (ResourceAcquisitionMode)99, Input)]));
        Assert.Throws<ArgumentException>(() => new ResourceAcquisitionIntent([new(First, ResourceAcquisitionMode.Reference, Input, new(Guid.NewGuid(), new(SecretId.New(), SecretVersionId.New())))]));
    }
    [Fact]
    public void Closed_acquisition_codec_preserves_exact_input_mode_and_rejects_unknown_or_ambiguous_documents()
    {
        var intent = Intent();
        var json = ResourceAcquisitionIntentCodec.Encode(intent);
        Assert.Equal(intent, ResourceAcquisitionIntentCodec.Decode(json));
        foreach (var malformed in new[] { json.Replace("\"Mode\":2", "\"Mode\":99", StringComparison.Ordinal), json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal), json.Replace("\"Version\":1", "\"Unknown\":true,\"Version\":1", StringComparison.Ordinal) })
            Assert.Throws<InvalidDataException>(() => ResourceAcquisitionIntentCodec.Decode(malformed));
    }
    [Fact]
    public void Frozen_groups_partition_observations_by_mode_and_reject_missing_duplicate_foreign_or_provision_members()
    {
        var a = Node(First, ResourceAcquisitionMode.Reference, "reference");
        var b = Node(Second, ResourceAcquisitionMode.ImportUseAndRetain, "import");
        var graph = new DurableOperationGraph([a, b]) { Acquisitions = Intent() };
        var inputs = Inputs with { Acquisitions = Intent() };
        graph.ValidateReferences(inputs);
        var decoded = DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(graph));
        decoded.ValidateReferences(inputs);
        Assert.Equal(graph, decoded);
        Assert.Equal(2, decoded.ObservationGroups.Count);
        foreach (var groups in new IReadOnlyList<ResourceObservationGroup>[] {
            [], [new("one", ResourceAcquisitionMode.Reference, [a.Id]), new("two", ResourceAcquisitionMode.Reference, [a.Id])],
            [new("one", ResourceAcquisitionMode.Reference, [a.Id, b.Id])],
            [new("one", ResourceAcquisitionMode.Reference, [a.Id, OperationId.Create("foreign")])],
            [new("one", ResourceAcquisitionMode.Provision, [a.Id, b.Id])] })
            Assert.Throws<InvalidDataException>(() => DurableOperationGraphCodec.Encode(new([a, b]) { Acquisitions = Intent(), ObservationGroups = groups }));
        Assert.Throws<InvalidDataException>(() => decoded.ValidateReferences(inputs with { Acquisitions = ResourceAcquisitionIntent.Provision([First, Second]) }));
    }
    [Fact]
    public void Intragroup_dependencies_refuse_but_frozen_crossgroup_prerequisites_remain_explicit()
    {
        var a = Node(First, ResourceAcquisitionMode.Reference, "first-observation");
        var b = a with { Id = OperationId.Create("second-observation"), Dependencies = [a.Id] };
        var group = new ResourceObservationGroup("together", ResourceAcquisitionMode.Reference, [a.Id, b.Id]);
        Assert.Throws<InvalidDataException>(() => DurableOperationGraphCodec.Encode(new([a, b]) { ObservationGroups = [group] }));
        var separate = new DurableOperationGraph([a, b with { ResourceId = Second }]);
        var decoded = DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(separate));
        Assert.Equal(a.Id, Assert.Single(decoded.Nodes.Single(x => x.Id == b.Id).Dependencies));
    }
}
