using System.Text;
using Haby.Domain;
using Haby.Manifest;
using Haby.Tests;
using Xunit;

namespace Haby.Application.Tests;

public sealed class DurablePlanningInputsTests
{
    private static DurablePlanningInputs Inputs(params string[] resources) => DurableInputSamples.Create(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), ProviderAlias.Create("sample.primary"),
        ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), "sample/v1", resources);

    internal static NormalizedApplicationManifest MixedManifest(string alias = "runtime", string component = "api") => ((ManifestCandidateResult.Valid)ManifestCandidatePipeline.Prepare(
        Encoding.UTF8.GetBytes("""
        {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{
        "resources":{"same":{"type":"sample.resource/v1","providerRef":"database","properties":{}}},
        "components":{"COMPONENT":{"workloads":{"same":{"type":"sample.workload/v1","providerRef":"RUNTIME","properties":{}}}},
        "worker":{"workloads":{"same":{"type":"sample.workload/v1","providerRef":"RUNTIME","properties":{}}}}}}}}
        """.Replace("RUNTIME", alias, StringComparison.Ordinal).Replace("COMPONENT", component, StringComparison.Ordinal)), new([new(ResourceTypeId.Create("sample.resource/v1"), "{}")],
            [new(WorkloadTypeId.Create("sample.workload/v1"), "{}")], [], []))).Candidate.Declaration;

    [Fact]
    public void Distinct_resource_and_component_workload_keys_preserve_every_exact_input_in_the_fingerprint()
    {
        var manifest = MixedManifest();
        var environment = EnvironmentRecordId.New();
        var instance = ProviderInstanceId.New();
        var configuration = ProviderConfigurationRevisionNumber.Create(3);
        var implementation = ProviderImplementationId.Create("sample/v1");
        var resource = new ExactProviderResolution(new ProviderInput.Resource(ManifestLocalId.Create("same")), environment,
            ProviderAlias.Create("database"), ProviderInstanceId.New(), configuration, implementation, ResourceNamingCodec.Exact(ResourceNamingSnapshot.Default));
        var api = new ExactProviderResolution(new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("same")), environment,
            ProviderAlias.Create("runtime"), instance, configuration, implementation);
        var worker = api with { Input = new ProviderInput.Workload(ManifestLocalId.Create("worker"), ManifestLocalId.Create("same")) };
        var source = new[] { resource, api, worker };
        var inputs = new DurablePlanningInputs(environment, EnvironmentId.Create("pilot"),
            ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), new(manifest, environment, source));
        var node = new DurableOperationNode(OperationId.Create("create"), ManifestLocalId.Create("same"),
            new(new(ResourceTypeId.Create("sample.resource/v1"), new("sample/v1", "sample.configuration/v1", "sample/v1")), OperationContractId.Create("sample.create/v1")),
            new(environment, resource.Instance, resource.Configuration, resource.Implementation.Value), new("sample/v1", "{}"), []);
        var graph = new DurableOperationGraph([node]);
        var fingerprint = DurableOperationGraphCodec.Fingerprint(inputs, graph);
        var encoded = ExactProviderResolutionSetCodec.Encode(inputs.Providers);
        source[0] = api;
        Assert.Equal(encoded, ExactProviderResolutionSetCodec.Encode(inputs.Providers));
        var read = ExactProviderResolutionSetCodec.Decode(encoded, manifest, environment);
        Assert.Equal(inputs.Providers, read);
        Assert.Equal(3, read.Entries.Count);
        Assert.Throws<InvalidDataException>(() => read.RequireUniformResourceProvider());
        Assert.Equal(fingerprint, DurableOperationGraphCodec.Fingerprint(inputs with { Providers = new(manifest, environment, [worker, resource, api]) }, graph));
        foreach (var variant in new[] { api with { Instance = ProviderInstanceId.New() }, api with { Configuration = ProviderConfigurationRevisionNumber.Create(4) },
            api with { Implementation = ProviderImplementationId.Create("other/v2") } })
        {
            var set = new ExactProviderResolutionSet(manifest, environment, [resource, variant, variant with { Input = worker.Input }]);
            Assert.NotEqual(fingerprint, DurableOperationGraphCodec.Fingerprint(inputs with { Providers = set }, graph));
        }
        Assert.NotEqual(fingerprint, DurableOperationGraphCodec.Fingerprint(inputs with
        {
            Providers = new(manifest, environment,
            [resource with { Naming = ResourceNamingCodec.Exact(new(1, new("names", 2), new("prefixed", "test_"))) }, api, worker])
        }, graph));
        var opaque = new ExactAdapterInput(AdapterInputContractId.Create("sample.naming/v1"),
            new ConfigurationObject([new("b", ConfigurationNumber.Parse("2")), new("a", new ConfigurationString("first"))]));
        var changed = new ExactProviderResolutionSet(manifest, environment, [resource, api with { Naming = opaque }, worker]);
        Assert.NotEqual(fingerprint, DurableOperationGraphCodec.Fingerprint(inputs with { Providers = changed }, graph));
        Assert.NotEqual(fingerprint, DurableOperationGraphCodec.Fingerprint(inputs with
        {
            Providers = new(manifest, environment,
            [resource, api with { Placement = opaque }, worker])
        }, graph));
        var reordered = opaque with { Value = new ConfigurationObject(opaque.Value.Members.Reverse()) };
        Assert.Equal(ExactProviderResolutionSetCodec.Encode(changed), ExactProviderResolutionSetCodec.Encode(new(manifest, environment,
            [worker, api with { Naming = reordered }, resource])));
        Assert.NotEqual(ExactProviderResolutionSetCodec.Encode(changed), ExactProviderResolutionSetCodec.Encode(new(manifest, environment,
            [resource, api with { Naming = opaque with { Contract = AdapterInputContractId.Create("sample.naming/v2") } }, worker])));
        Assert.NotEqual(DurableOperationGraphCodec.Fingerprint(inputs with { Providers = changed }, graph),
            DurableOperationGraphCodec.Fingerprint(inputs with
            {
                Providers = new(manifest, environment,
                [resource, api with { Naming = opaque with { Contract = AdapterInputContractId.Create("sample.naming/v2") } }, worker])
            }, graph));
        Assert.NotEqual(fingerprint, DurableOperationGraphCodec.Fingerprint(inputs with
        {
            Providers = new(MixedManifest(component: "renamed"), environment,
            [resource, api with { Input = new ProviderInput.Workload(ManifestLocalId.Create("renamed"), ManifestLocalId.Create("same")) }, worker])
        }, graph));
        var otherManifest = MixedManifest("other-runtime");
        Assert.NotEqual(fingerprint, DurableOperationGraphCodec.Fingerprint(inputs with
        {
            Providers = new(otherManifest, environment,
            [resource, api with { Alias = ProviderAlias.Create("other-runtime") }, worker with { Alias = ProviderAlias.Create("other-runtime") }])
        }, graph));
        Assert.Throws<InvalidDataException>(() => graph.ValidateReferences(inputs with
        {
            Providers = new(manifest, environment,
            [resource with { Instance = ProviderInstanceId.New() }, api, worker])
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Zero_one_and_multiple_inputs_roundtrip_detached_and_in_canonical_key_order(int count)
    {
        var inputs = Inputs(Enumerable.Range(0, count).Select(i => "resource-" + i).ToArray());
        var encoded = ExactProviderResolutionSetCodec.Encode(inputs.Providers);
        var read = ExactProviderResolutionSetCodec.Decode(encoded, inputs.Providers.Declaration, inputs.Environment);
        Assert.Equal(inputs.Providers, read);
        Assert.NotSame(inputs.Providers, read);
        Assert.Equal(count, read.Entries.Count);
        Assert.Equal(encoded, ExactProviderResolutionSetCodec.Encode(new(inputs.Providers.Declaration, inputs.Environment, inputs.Providers.Entries.Values.Reverse())));
        if (count == 0) Assert.Throws<InvalidDataException>(() => read.RequireUniformResourceProvider());
    }

    [Fact]
    public void Complete_set_equality_and_fingerprint_include_each_key_and_every_exact_field()
    {
        var inputs = Inputs("a", "b");
        var entries = inputs.Providers.Entries.Values.OrderBy(x => ((ProviderInput.Resource)x.Input).Id.Value).ToArray();
        // A distinct alias with its own exact Provider is storage/planning data, not admission.
        var original = entries[1];
        foreach (var changed in new[] { original with { Instance = ProviderInstanceId.New() },
            original with { Configuration = ProviderConfigurationRevisionNumber.Create(2) },
            original with { Implementation = ProviderImplementationId.Create("changed/v1") } })
        {
            // Use distinct aliases in the declaration to allow independently varying the second resolution.
            Assert.Throws<ArgumentException>(() => new ExactProviderResolutionSet(inputs.Providers.Declaration, inputs.Environment, [entries[0], changed]));
        }
        Assert.Equal(inputs, inputs with { Providers = new(inputs.Providers.Declaration, inputs.Environment, entries.Reverse()) });
        Assert.NotEqual(inputs, inputs with { EnvironmentKey = EnvironmentId.Create("other") });
        Assert.Throws<ArgumentException>(() => new ExactProviderResolutionSet(inputs.Providers.Declaration, inputs.Environment, entries.Skip(1)));
        Assert.Throws<ArgumentException>(() => new ExactProviderResolutionSet(inputs.Providers.Declaration, inputs.Environment, [entries[0], entries[0]]));
        Assert.Throws<ArgumentException>(() => new ExactProviderResolutionSet(inputs.Providers.Declaration, inputs.Environment,
            [entries[0], entries[1] with { Environment = EnvironmentRecordId.New() }]));
    }

    [Theory]
    [InlineData("{\"Version\":0,\"Entries\":[]}")]
    [InlineData("{\"Version\":1,\"Entries\":null}")]
    [InlineData("{\"Version\":1,\"Entries\":[],\"legacy\":true}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Entries\":[]}")]
    [InlineData("{}")]
    public void Unsupported_or_malformed_documents_have_no_legacy_or_default_fallback(string json)
    {
        var inputs = Inputs();
        Assert.Throws<InvalidDataException>(() => ExactProviderResolutionSetCodec.Decode(json, inputs.Providers.Declaration, inputs.Environment));
    }
}
