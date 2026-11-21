using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ResourceImportIntentTests
{
    private static readonly DurablePlanningInputs Inputs = Haby.Tests.DurableInputSamples.Create(
        EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)),
        ProviderAlias.Create("sample.primary"), ProviderInstanceId.New(),
        ProviderConfigurationRevisionNumber.Create(1), "sample.impl/v1", "one-resource");
    private static readonly ProviderAdapterKey Provider = new("sample/v1", "sample.configuration/v1", "sample.impl/v1");
    private static ResourceImportIntent Intent(string identity = "existing") => new([
        new(ManifestLocalId.Create("one-resource"), new("sample.import/v1", "{\"identity\":\"" + identity + "\"}"))]);

    [Fact]
    public void Intent_copies_requests_and_closed_codec_refuses_ambiguous_or_unknown_inputs()
    {
        var requests = Intent().Resources.ToArray();
        var intent = new ResourceImportIntent(requests);
        var encoded = ResourceImportIntentCodec.Encode(intent)!;
        requests[0] = requests[0] with { Input = new("sample.import/v1", "{}") };
        Assert.Equal(Intent(), intent);
        Assert.Equal(intent, ResourceImportIntentCodec.Decode(encoded));
        Assert.Throws<InvalidDataException>(() => ResourceImportIntentCodec.Decode(encoded.Replace("\"Version\":2", "\"Version\":99", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ResourceImportIntentCodec.Decode(encoded.Replace("\"Version\":2", "\"Version\":2,\"Version\":2", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ResourceImportIntentCodec.Decode(encoded.Replace("\"Version\":2", "\"Unknown\":true,\"Version\":2", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => new ResourceImportIntent([requests[0], requests[0]]));
        Assert.Throws<InvalidDataException>(() => new ResourceImportIntent([
            new(ManifestLocalId.Create("other"), new("sample.import/v1", "{}"))]).Validate(Inputs.Providers.Declaration.Spec));
    }

    [Fact]
    public void Protected_input_reference_freezes_exact_stage_and_secret_version_without_material()
    {
        var credential = new ProtectedCredentialReference(Guid.NewGuid(), new(SecretId.New(), SecretVersionId.New()));
        var intent = new ResourceImportIntent([Intent().Resources.Single() with { Credential = credential }]);
        var encoded = ResourceImportIntentCodec.Encode(intent)!;
        Assert.Equal(intent, ResourceImportIntentCodec.Decode(encoded));
        Assert.Equal(credential, ResourceImportIntentCodec.Decode(encoded)!.Resources.Single().Credential);
        Assert.Throws<InvalidDataException>(() => ResourceImportIntentCodec.Decode(encoded.Replace(credential.Reference.SecretVersionId.ToString(), "latest", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ResourceImportIntentCodec.Decode(encoded.Replace("\"Secret\":", "\"material\":\"forbidden\",\"Secret\":", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ResourceImportIntentCodec.Decode(encoded.Replace(credential.Reference.SecretVersionId.ToString(), "", StringComparison.Ordinal)));
    }

    [Fact]
    public void Intent_encoding_cannot_persist_a_document_its_reader_refuses()
    {
        var requests = Enumerable.Range(0, 20).Select(i => new ResourceImportRequest(ManifestLocalId.Create("resource-" + i),
            new("sample.import/v1", "{\"identity\":\"" + new string('a', 60000) + "\"}")));
        Assert.Throws<InvalidDataException>(() => ResourceImportIntentCodec.Encode(new(requests)));
    }

    [Fact]
    public void Provision_registration_never_admits_import_or_a_different_exact_resource_type()
    {
        var provision = new Admission(ResourceAcquisitionMode.Provision);
        var admission = new DurableActionAdmission(new([provision], []));
        var imported = Inputs with { Import = Intent() };
        Assert.Equal(DurablePlanStatus.Planned, admission.Admit(Inputs.Providers.Declaration.Spec, Provider, Inputs));
        Assert.Equal(DurablePlanStatus.ImportNotSupported, admission.Admit(Inputs.Providers.Declaration.Spec, Provider, imported));
        Assert.False(admission.HasPlanningSupport(Inputs.Providers.Declaration.Spec, Provider, imported));
        var different = new Admission(ResourceAcquisitionMode.ImportUseAndRetain, "sample.other/v1");
        admission = new(new([different], []));
        Assert.Equal(DurablePlanStatus.ImportNotSupported, admission.Admit(Inputs.Providers.Declaration.Spec, Provider, imported));
    }

    [Fact]
    public void Frozen_import_identity_mode_and_output_obligation_survive_reload_and_reject_tampering()
    {
        var imported = Inputs with { Import = Intent() };
        var handler = new Admission(ResourceAcquisitionMode.ImportUseAndRetain);
        var node = Assert.Single(handler.Plan(ManifestLocalId.Create("one-resource"), Inputs.Providers.Declaration.Spec.Resources.Values.Single(), imported));
        var graph = new DurableOperationGraph([node]) { Import = imported.Import };
        graph.ValidateReferences(imported);
        var frozen = DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(graph));
        frozen.ValidateReferences(imported);
        Assert.Equal(graph, frozen);
        Assert.Throws<InvalidDataException>(() => frozen.ValidateReferences(imported with { Import = Intent("different") }));
        Assert.Throws<InvalidDataException>(() => frozen.ValidateReferences(Inputs));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([node with {
            Action = node.Action with { Admission = node.Action.Admission with { Mode = ResourceAcquisitionMode.Provision } }
        }])
        { Import = imported.Import }.ValidateReferences(imported));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([node with {
            RequiresExternalOutput = false, RequiredOutput = null
        }])
        { Import = imported.Import }.ValidateReferences(imported));
    }

    [Fact]
    public void Import_cannot_be_combined_with_an_update_baseline_even_without_reused_resources()
    {
        var imported = Inputs with { Import = Intent() };
        var handler = new Admission(ResourceAcquisitionMode.ImportUseAndRetain);
        var node = Assert.Single(handler.Plan(ManifestLocalId.Create("one-resource"), Inputs.Providers.Declaration.Spec.Resources.Values.Single(), imported));
        var graph = new DurableOperationGraph([node], baseline: new(ApplicationDeploymentId.New(), 1, "v6:" + new string('A', 64))) { Import = imported.Import };
        Assert.Throws<InvalidDataException>(() => graph.ValidateReferences(imported));
    }

    private sealed class Admission(ResourceAcquisitionMode mode, string type = "sample.resource/v1") : IDurableActionAdmissionHandler
    {
        public DurableActionExecutionKey Key { get; } = new(new(ResourceTypeId.Create(type), Provider) { Mode = mode }, OperationContractId.Create("sample.verify/v1"));
        public bool Validate(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) => true;
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
        {
            var provider = inputs.Providers.Resource(resourceId);
            return [new(OperationId.Create("verify"), resourceId, Key,
                new(provider.Environment, provider.Instance, provider.Configuration, provider.Implementation.Value),
                inputs.Import?.Resources.Single().Input ?? new("sample.provision/v1", "{}"), [])
                { RequiresExternalOutput = true, RequiredOutput = new("sample.receipt/v1", "verified") }];
        }
    }
}
