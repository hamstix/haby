using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Xunit;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Application.Tests;

public sealed class KubernetesPlanningTests
{
    private static readonly ManifestContractCatalog Catalog = new([PostgreSqlDatabaseContract.Definition], [KubernetesContract.Definition], [], []);
    private static readonly EnvironmentRecordId Environment = EnvironmentRecordId.New();
    private static readonly ProviderInstanceId Provider = ProviderInstanceId.New();
    private static readonly ProviderConfigurationRevisionNumber Revision = ProviderConfigurationRevisionNumber.Create(3);
    private static readonly ProviderImplementationId Implementation = ProviderImplementationId.Create("kubernetes.test");
    private static readonly ProviderAlias Alias = ProviderAlias.Create("kubernetes.primary");
    private static ManifestLocalId Id(string id) => ManifestLocalId.Create(id);
    private const string Properties = """
        {"image":{"name":"orders","tagFrom":"applicationVersion"},"containerName":"orders-service",
         "ports":{"http":{"containerPort":8080,"protocol":"TCP"},"metrics":{"containerPort":9090}},
         "env":{"MODE":{"value":"safe"},"SETTING":{"configMapKey":{"name":"settings","key":"mode","optional":true}},"PASSWORD":{"secretKey":{"name":"database","key":"password"}}},
         "probes":{"startup":{"tcpSocket":{"port":"http"},"failureThreshold":20},"liveness":{"httpGet":{"port":"http","path":"/live","scheme":"HTTPS"},"initialDelaySeconds":2,"periodSeconds":4,"timeoutSeconds":3,"successThreshold":1,"failureThreshold":5},"readiness":{"httpGet":{"port":"http","path":"/ready"},"successThreshold":2}},
         "volumes":{"settings":{"configMap":{"name":"settings","optional":true}},"secret":{"secret":{"name":"database"}},"data":{"persistentVolumeClaim":{"name":"orders-data","readOnly":false}}},
         "mounts":[{"volume":"settings","mountPath":"/etc/app","readOnly":true},{"volume":"secret","mountPath":"/etc/password","subPath":"password"},{"volume":"data","mountPath":"/data","readOnly":false}],
         "routes":[{"hostLabel":"orders","path":"/","port":"http","pathType":"Prefix"}],
         "configuration":{"documents":["settings"]},
         "overrides":{"replicas":2,"minReadySeconds":5,"rolloutSeconds":1200,"resources":{"requests":{"cpu":"500m","memory":"128Mi"},"limits":{"cpu":"2","memory":"1Gi"}},"service":{"enabled":true},"headlessService":{"enabled":true},"ingress":{"enabled":true,"className":"nginx","domain":"example.test","tlsSecret":"tls"}}}
        """;
    private const string Minimal = """{"image":{"name":"orders","tagFrom":"applicationVersion"}}""";
    private static JsonObject Envelope(string properties = Minimal, string? selector = null)
    {
        var workload = new JsonObject { ["type"] = "kubernetes.workload/v1alpha1", ["providerRef"] = Alias.Value, ["properties"] = JsonNode.Parse(properties) };
        if (selector is not null) workload["placementRef"] = selector;
        return new JsonObject
        {
            ["applicationVersion"] = "1.4.2",
            ["manifest"] = new JsonObject
            {
                ["apiVersion"] = "haby.dev/v1alpha1",
                ["kind"] = "Application",
                ["metadata"] = new JsonObject { ["id"] = "orders" },
                ["spec"] = new JsonObject
                {
                    ["components"] = new JsonObject
                    {
                        ["api"] = new JsonObject
                        {
                            ["documents"] = new JsonObject { ["settings"] = new JsonObject { ["fileName"] = "settings.json", ["format"] = "json", ["template"] = new JsonObject() } },
                            ["workloads"] = new JsonObject { ["main"] = workload }
                        }
                    }
                }
            }
        };
    }
    private static JsonObject Workload(JsonObject envelope) => envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!.AsObject();
    private static NormalizedApplicationManifest Parse(JsonObject envelope)
    {
        var parsed = Assert.IsType<ManifestParseResult.Valid>(ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(envelope.ToJsonString()), Catalog));
        var normalized = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes(envelope.ToJsonString()), Catalog)).Candidate.Declaration;
        var stored = NormalizedSnapshotCodec.Encode(normalized);
        var reloaded = NormalizedSnapshotCodec.Decode(stored, normalized.InputApiVersion, normalized.NormalizedModelVersion, normalized.ApplicationId.Value, NormalizedSnapshotCodec.FormatVersion);
        Assert.Equal(normalized, reloaded);
        return reloaded;
    }
    private static KubernetesPlacement Placement(string name = "standard", string settings = "", string? registry = "registry.customer.local")
    {
        var root = JsonNode.Parse("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard","revision":1},"spec":{"namespace":"production"}}"""u8)!;
        root["metadata"]!["name"] = name;
        if (registry is not null) { root["spec"]!["registry"] = registry; root["spec"]!["repositoryPrefix"] = "product"; }
        if (settings.Length > 0) foreach (var (key, value) in JsonNode.Parse(settings)!.AsObject()) root["spec"]![key] = value?.DeepClone();
        return KubernetesContract.DecodePlacement(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }
    private static PlacementRevisionInput Bind(KubernetesPlacement placement) => new(Environment, Alias, Provider, Revision, Implementation, placement);
    private static PlacementInputs Inputs(KubernetesPlacement? placement = null) => new([Bind(placement ?? Placement())], [new(new(Environment, Alias), (placement ?? Placement()).Reference)]);
    private static ExactProviderResolutionSet Providers(NormalizedApplicationManifest manifest) => new(manifest, Environment,
        manifest.Spec.Components.SelectMany(c => c.Value.Workloads.Select(w => new ExactProviderResolution(new ProviderInput.Workload(c.Key, w.Key), Environment, w.Value.ProviderRef, Provider, Revision, Implementation)))
            .Concat(manifest.Spec.Resources.Select(r => new ExactProviderResolution(new ProviderInput.Resource(r.Key), Environment, r.Value.ProviderRef,
                ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(7), ProviderImplementationId.Create("postgresql.test")))));
    private static EffectiveWorkload Resolve(JsonObject? envelope = null, PlacementInputs? placements = null, string version = "1.4.2")
    {
        var manifest = Parse(envelope ?? Envelope());
        return Assert.Single(KubernetesWorkloadPlanner.Resolve(manifest, ApplicationVersion.Create(version), Providers(manifest), placements ?? Inputs()));
    }

    [Fact]
    public void Full_real_pipeline_retains_every_property_and_freezes_expected_values()
    {
        var envelope = Envelope(Properties, "standard");
        var manifest = Parse(envelope);
        Assert.Equal(Id("standard"), manifest.Spec.Components[Id("api")].Workloads[Id("main")].PlacementRef);
        var inputs = new PlacementInputs([Bind(Placement())], [new(new(Environment, Alias, Selector: Id("standard")), Placement().Reference)]);
        var result = Resolve(envelope, inputs);
        Assert.Equal("registry.customer.local/product/orders:1.4.2", result.ImageReference);
        Assert.Equal("orders-service", result.Declaration.ContainerName);
        Assert.Equal(2, result.Settings.Replicas);
        Assert.Equal(1200, result.Settings.RolloutSeconds);
        Assert.Equal(5, result.Settings.MinReadySeconds);
        Assert.Equal("500m", result.Settings.Resources!.Requests!.Cpu);
        Assert.Equal("1Gi", result.Settings.Resources.Limits!.Memory);
        Assert.Equal(5, result.Objects.Length);
        Assert.Contains(result.Objects, x => x.Kind == "Service" && x.Name == "orders-api-main-headless");
        Assert.Contains(result.Objects, x => x.Kind == "ServiceAccount" && x.Name == "orders-api-main-runtime");
        Assert.Equal(3, result.Declaration.Volumes.Count);
        Assert.Equal(3, result.Declaration.Mounts.Length);
        Assert.Equal("password", result.Declaration.Mounts[1].SubPath);
        Assert.Equal(2, result.Declaration.Probes.Readiness!.SuccessThreshold);
        Assert.Equal("HTTPS", result.Declaration.Probes.Liveness!.Scheme);
        Assert.Equal("nginx", result.Settings.Ingress!.ClassName);
        Assert.Equal("tls", result.Settings.Ingress.TlsSecret);
        Assert.Single(result.Declaration.Documents);
        Assert.IsType<EnvironmentValue.Secret>(result.Declaration.Environment["PASSWORD"]);
        Assert.IsType<EnvironmentValue.ConfigMap>(result.Declaration.Environment["SETTING"]);
    }

    [Fact]
    public void Defaults_do_not_materialize_in_normalized_extension_properties()
    {
        var manifest = Parse(Envelope());
        Assert.Single(manifest.Spec.Components[Id("api")].Workloads[Id("main")].Properties.Members);
        var result = Resolve();
        Assert.Equal("app", result.Declaration.ContainerName);
        Assert.Equal(1, result.Settings.Replicas);
        Assert.Equal("RollingUpdate", result.Settings.Strategy!.Type);
        Assert.Equal(new RolloutBound(25, true), result.Settings.Strategy.MaxSurge);
        Assert.Equal(600, result.Settings.RolloutSeconds);
        Assert.Equal(0, result.Settings.MinReadySeconds);
        Assert.Null(result.Settings.Resources);
        Assert.Empty(result.Declaration.Documents);
        Assert.Single(result.Objects);
        Assert.False(result.Settings.Ingress!.Enabled);
    }

    [Theory]
    [InlineData("{\"name\":\"orders\",\"tag\":\"v2\"}", "registry.customer.local/product/orders:v2")]
    [InlineData("{\"reference\":\"other.test/team/orders:v2\"}", "other.test/team/orders:v2")]
    [InlineData("{\"reference\":\"other.test/team/orders@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}", "other.test/team/orders@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Images_are_explicit_and_complete_forms_bypass_prefix(string image, string expected)
    {
        var envelope = Envelope(); Workload(envelope)["properties"]!["image"] = JsonNode.Parse(image);
        Assert.Equal(expected, Resolve(envelope).ImageReference);
        if (image.Contains("reference", StringComparison.Ordinal)) Assert.Equal(expected, Resolve(envelope, Inputs(Placement(registry: null))).ImageReference);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"repository\":\"orders\",\"tag\":\"v1\"}")]
    [InlineData("{\"name\":\"orders\",\"tag\":\"v1\",\"tagFrom\":\"applicationVersion\"}")]
    [InlineData("{\"name\":\"orders\",\"tag\":null}")]
    [InlineData("{\"name\":\"{{ app }}\",\"tag\":\"v1\"}")]
    [InlineData("{\"name\":\"orders\",\"tag\":\"1.0+build\"}")]
    [InlineData("{\"reference\":\"host.test/orders\"}")]
    [InlineData("{\"reference\":\"orders:v1\"}")]
    [InlineData("{\"reference\":\"https://host.test/orders:v1\"}")]
    [InlineData("{\"reference\":\"host.test/orders@sha256:aaa\"}")]
    public void Invalid_image_forms_refuse_at_schema_or_typed_boundary(string image)
    {
        var envelope = Envelope(); Workload(envelope)["properties"]!["image"] = JsonNode.Parse(image);
        var parsed = ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(envelope.ToJsonString()), Catalog);
        if (parsed is ManifestParseResult.Valid valid)
            Assert.Throws<WorkloadContractException>(() => KubernetesContract.Decode(Parse(envelope).Spec.Components[Id("api")].Workloads[Id("main")]));
        else Assert.IsType<ManifestParseResult.InvalidManifest>(parsed);
    }

    [Fact]
    public void Actual_application_version_and_static_image_name_are_independent_of_identity()
    {
        Assert.EndsWith(":2.3.4", Resolve(version: "2.3.4").ImageReference);
        Assert.Throws<WorkloadContractException>(() => Resolve(version: "1.0+build"));
        Assert.Throws<WorkloadContractException>(() => Resolve(placements: Inputs(Placement(registry: null))));
        var envelope = Envelope(); envelope["manifest"]!["metadata"]!["id"] = "different";
        Workload(envelope)["properties"]!["containerName"] = "existing-container";
        var result = Resolve(envelope);
        Assert.Equal("registry.customer.local/product/orders:1.4.2", result.ImageReference);
        Assert.Equal("existing-container", result.Declaration.ContainerName);
        Assert.Equal("different-api-main", result.Objects[0].Name);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"documents\":[]}")]
    [InlineData("{\"documents\":[\"settings\",\"settings\"]}")]
    [InlineData("{\"documents\":[\"settings\"],\"extra\":true}")]
    public void Configuration_must_select_explicit_nonempty_unique_documents(string config)
    {
        var envelope = Envelope(); Workload(envelope)["properties"]!["configuration"] = JsonNode.Parse(config);
        Assert.IsType<ManifestParseResult.InvalidManifest>(ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(envelope.ToJsonString()), Catalog));
    }

    [Fact]
    public void Missing_document_and_named_port_and_volume_references_refuse()
    {
        var envelope = Envelope(Properties); Workload(envelope)["properties"]!["configuration"]!["documents"]![0] = "missing";
        Assert.Throws<WorkloadContractException>(() => Resolve(envelope));
        foreach (var member in new[] { "routes", "mounts", "probes" })
        {
            envelope = Envelope(Properties); var properties = Workload(envelope)["properties"]!;
            if (member == "routes") properties[member]![0]!["port"] = "missing";
            if (member == "mounts") properties[member]![0]!["volume"] = "missing";
            if (member == "probes") properties[member]!["startup"]!["tcpSocket"]!["port"] = "missing";
            Assert.Throws<WorkloadContractException>(() => Resolve(envelope));
        }
    }

    [Theory]
    [InlineData(0, "environment")]
    [InlineData(1, "application")]
    [InlineData(2, "component")]
    [InlineData(3, "explicit")]
    public void Scope_precedence_selects_complete_exact_revision(int level, string expected)
    {
        var declarations = new[] { "environment", "application", "component", "explicit" }.Select(n => Bind(Placement(n))).ToArray();
        var scopes = new[] { new PlacementScope(Environment, Alias), new(Environment, Alias, ApplicationId.Create("orders")),
            new(Environment, Alias, ApplicationId.Create("orders"), Id("api")), new(Environment, Alias, Selector: Id("selected")) };
        var assignments = scopes.Take(level + 1).Select((s, i) => new PlacementAssignment(s, declarations[i].Declaration.Reference)).ToList();
        var inputs = new PlacementInputs(declarations, assignments);
        var result = Resolve(Envelope(selector: level == 3 ? "selected" : null), inputs);
        assignments.Clear(); Array.Clear(declarations);
        Assert.Equal(expected, result.Placement.Declaration.Reference.Name.Value);
        Assert.Equal(expected, Resolve(Envelope(selector: level == 3 ? "selected" : null), inputs).Placement.Declaration.Reference.Name.Value);
    }

    [Fact]
    public void Unknown_explicit_selection_never_falls_back_and_foreign_inputs_refuse()
    {
        Assert.Throws<WorkloadContractException>(() => Resolve(Envelope(selector: "missing")));
        Assert.Throws<WorkloadContractException>(() => Resolve(placements: new([], [])));
        var placement = Placement(); var assignment = new PlacementAssignment(new(Environment, Alias), placement.Reference);
        foreach (var wrong in new[] { Bind(placement) with { Environment = EnvironmentRecordId.New() }, Bind(placement) with { Provider = ProviderInstanceId.New() },
            Bind(placement) with { Configuration = ProviderConfigurationRevisionNumber.Create(4) }, Bind(placement) with { Implementation = ProviderImplementationId.Create("other.test") },
            Bind(placement) with { Alias = ProviderAlias.Create("other.primary") } })
            Assert.Throws<WorkloadContractException>(() => Resolve(placements: new([wrong], [assignment])));
        var manifest = Parse(Envelope());
        Assert.Throws<WorkloadContractException>(() => KubernetesWorkloadPlanner.Resolve(manifest, ApplicationVersion.Create("1"), Providers(manifest), Inputs(), "foreign"));
    }

    [Fact]
    public void Whole_strategy_and_resource_override_do_not_merge()
    {
        var placement = Placement(settings: """{"strategy":{"type":"RollingUpdate","maxSurge":9,"maxUnavailable":0},"resources":{"requests":{"cpu":"1","memory":"1Gi"}},"nodeSelector":{"kubernetes.io/os":"linux"},"imagePullSecrets":["registry"]}""");
        var envelope = Envelope(); Workload(envelope)["properties"]!["overrides"] = JsonNode.Parse("""{"strategy":{"type":"Recreate"},"resources":{"limits":{"cpu":"2"}}}"""u8);
        var result = Resolve(envelope, Inputs(placement));
        Assert.Equal("Recreate", result.Settings.Strategy!.Type); Assert.Null(result.Settings.Strategy.MaxSurge);
        Assert.Null(result.Settings.Resources!.Requests); Assert.Equal("2", result.Settings.Resources.Limits!.Cpu);
        Assert.Equal("linux", result.Placement.Declaration.NodeSelector["kubernetes.io/os"]);
        Assert.Equal("registry", Assert.Single(result.Placement.Declaration.ImagePullSecrets));
        Assert.Equal("Recreate", Resolve(placements: Inputs(Placement(settings: """{"strategy":{"type":"Recreate"}}"""))).Settings.Strategy!.Type);
    }

    [Theory]
    [InlineData("{\"strategy\":{\"type\":\"Recreate\",\"maxSurge\":1}}")]
    [InlineData("{\"strategy\":{\"type\":\"RollingUpdate\",\"maxSurge\":0,\"maxUnavailable\":\"0%\"}}")]
    [InlineData("{\"replicas\":0}")]
    [InlineData("{\"replicas\":1.01}")]
    [InlineData("{\"namespace\":\"other\"}")]
    [InlineData("{\"replicas\":null}")]
    [InlineData("{\"resources\":{\"requests\":{\"cpu\":\"2\"},\"limits\":{\"cpu\":\"1\"}}}")]
    public void Invalid_operational_shapes_refuse(string settings)
    {
        Assert.Throws<WorkloadContractException>(() => KubernetesContract.DecodeWorkload(Encoding.UTF8.GetBytes("{\"image\":{\"name\":\"orders\",\"tag\":\"v1\"},\"overrides\":" + settings + "}")));
    }

    [Fact]
    public void Exact_integer_lexical_forms_do_not_use_double_coercion()
    {
        foreach (var number in new[] { "2", "2.0", "2e0" })
        {
            var envelope = Envelope(); Workload(envelope)["properties"]!["overrides"] = JsonNode.Parse("{\"replicas\":" + number + "}");
            Assert.Equal(2, Resolve(envelope).Settings.Replicas);
        }
        Assert.Throws<WorkloadContractException>(() => KubernetesContract.DecodeWorkload(Encoding.UTF8.GetBytes("""{"image":{"name":"orders","tag":"v1"},"overrides":{"replicas":2.0000000000000000000000000000000001}}""")));
    }

    [Fact]
    public void Final_names_and_collisions_are_checked_without_truncation()
    {
        var envelope = Envelope(Properties); Workload(envelope)["properties"]!["nameOverride"] = new string('a', 55);
        Assert.Throws<WorkloadContractException>(() => Resolve(envelope));
        Workload(envelope)["properties"]!["nameOverride"] = new string('a', 54);
        Assert.Equal(63, Resolve(envelope).Objects.Single(x => x.Name.EndsWith("-headless", StringComparison.Ordinal)).Name.Length);
        envelope = Envelope(); Workload(envelope)["properties"]!["nameOverride"] = "same";
        var workloads = envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!;
        workloads["second"] = Workload(envelope).DeepClone();
        var manifest = Parse(envelope);
        Assert.Throws<WorkloadContractException>(() => KubernetesWorkloadPlanner.Resolve(manifest, ApplicationVersion.Create("1"), Providers(manifest), Inputs()));
    }

    [Fact]
    public void Resource_and_workload_provider_entries_remain_distinct()
    {
        var envelope = Envelope(); envelope["manifest"]!["spec"]!["resources"] = JsonNode.Parse("""{"database":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"orders"}}}"""u8);
        var manifest = Parse(envelope); var providers = Providers(manifest);
        Assert.Equal(2, providers.Entries.Count);
        var resource = providers.Entries[new ProviderInput.Resource(Id("database"))];
        var workload = providers.Entries[new ProviderInput.Workload(Id("api"), Id("main"))];
        Assert.NotEqual(resource.Instance, workload.Instance);
        Assert.NotEqual(resource.Implementation, workload.Implementation);
        var reversed = new ExactProviderResolutionSet(manifest, Environment, providers.Entries.Values.Reverse());
        Assert.Equal(workload, Assert.Single(KubernetesWorkloadPlanner.Resolve(manifest, ApplicationVersion.Create("1"), reversed, Inputs())).Provider);
        Assert.Throws<ArgumentException>(() => new ExactProviderResolutionSet(manifest, Environment, [workload]));
    }

    [Fact]
    public void Obsolete_snapshot_version_or_profile_cannot_silently_disappear()
    {
        var manifest = Parse(Envelope()); var snapshot = NormalizedSnapshotCodec.Encode(manifest);
        Assert.Throws<InvalidDataException>(() => NormalizedSnapshotCodec.Decode(snapshot, manifest.InputApiVersion, 2, "orders", 2));
        var root = JsonNode.Parse(snapshot)!; root["spec"]!["components"]!["api"]!["workloads"]!["main"]!["profile"] = "old";
        Assert.Throws<InvalidDataException>(() => NormalizedSnapshotCodec.Decode(root.ToJsonString(), manifest.InputApiVersion, 3, "orders", 3));
    }
    [Fact]
    public void Every_top_level_property_participates_in_snapshot_equality_and_diff()
    {
        var complete = Envelope(Properties, "standard");
        Workload(complete)["properties"]!["nameOverride"] = "explicit";
        var baseline = Parse(complete);
        foreach (var name in Workload(complete)["properties"]!.AsObject().Select(x => x.Key).ToArray())
        {
            var changed = (JsonObject)complete.DeepClone();
            if (name == "image") Workload(changed)["properties"]!["image"] = JsonNode.Parse("""{"name":"another","tag":"v2"}"""u8);
            else Workload(changed)["properties"]!.AsObject().Remove(name);
            var normalized = Parse(changed);
            Assert.NotEqual(baseline, normalized);
        }
        var other = (JsonObject)complete.DeepClone(); Workload(other)["placementRef"] = "another";
        Assert.NotEqual(baseline, Parse(other));
    }

    [Fact]
    public void Reordered_input_maps_and_mutated_source_json_do_not_change_frozen_results()
    {
        var envelope = Envelope(Properties);
        var original = Resolve(envelope);
        var properties = Workload(envelope)["properties"]!.AsObject();
        var reversed = new JsonObject();
        foreach (var (name, value) in properties.Reverse()) reversed[name] = value?.DeepClone();
        Workload(envelope)["properties"] = reversed;
        var reordered = Resolve(envelope);
        Assert.Equal(original.ImageReference, reordered.ImageReference);
        Assert.Equal(original.Objects.ToArray(), reordered.Objects.ToArray());
        Assert.Equal(original.Settings, reordered.Settings);
        reversed["containerName"] = "changed";
        Assert.Equal("orders-service", original.Declaration.ContainerName);
        Assert.Equal("orders-service", reordered.Declaration.ContainerName);
    }

    [Fact]
    public void Common_exact_set_preserves_opaque_typed_inputs_without_adapter_interpretation()
    {
        var manifest = Parse(Envelope());
        var resolution = Providers(manifest).Entries.Values.Single();
        var opaque = new ExactAdapterInput(AdapterInputContractId.Create("example.placement/v1"),
            new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["opaque"] = new ConfigurationString("exact-revision") }));
        var set = new ExactProviderResolutionSet(manifest, Environment, [resolution with { Placement = opaque, Naming = opaque }]);
        Assert.Equal(opaque, Assert.Single(set.Entries).Value.Placement);
        Assert.Equal("exact-revision", Assert.IsType<ConfigurationString>(Assert.Single(set.Entries).Value.Naming!.Value.Members["opaque"]).Value);
    }

    [Theory]
    [InlineData("ports")]
    [InlineData("env")]
    [InlineData("probes")]
    [InlineData("volumes")]
    [InlineData("mounts")]
    [InlineData("routes")]
    [InlineData("overrides")]
    [InlineData("containerName")]
    public void Explicit_null_does_not_mean_inherit(string member)
    {
        var envelope = Envelope(Properties); Workload(envelope)["properties"]![member] = null;
        Assert.IsType<ManifestParseResult.InvalidManifest>(ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(envelope.ToJsonString()), Catalog));
    }

    [Fact]
    public void Deployment_and_runtime_suffix_use_their_own_final_length_bounds()
    {
        var envelope = Envelope(); Workload(envelope)["properties"]!["nameOverride"] = new string('a', 253);
        Assert.Equal(253, Resolve(envelope).Objects[0].Name.Length);
        Workload(envelope)["properties"]!["configuration"] = JsonNode.Parse("""{"documents":["settings"]}"""u8);
        Assert.Throws<WorkloadContractException>(() => Resolve(envelope));
        Workload(envelope)["properties"]!["nameOverride"] = new string('a', 245);
        Assert.Equal(253, Resolve(envelope).Objects.Single(x => x.Kind == "ServiceAccount").Name.Length);
    }

    private static void RefusePipeline(JsonObject envelope)
    {
        var parsed = ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(envelope.ToJsonString()), Catalog);
        Assert.IsType<ManifestParseResult.InvalidManifest>(parsed);
        Assert.Throws<WorkloadContractException>(() => KubernetesContract.DecodeWorkload(
            Encoding.UTF8.GetBytes(Workload(envelope)["properties"]!.ToJsonString())));
    }

    [Fact]
    public void Ambiguous_registry_tokens_never_resolve_to_implicit_Docker_Hub()
    {
        Assert.Throws<WorkloadContractException>(() => Resolve(placements: Inputs(Placement(registry: "registry"))));
        var envelope = Envelope(); Workload(envelope)["properties"]!["image"] = JsonNode.Parse("""{"reference":"team/orders:v1"}"""u8);
        RefusePipeline(envelope);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("registry:5000")]
    [InlineData("registry.example")]
    public void Unambiguous_registry_tokens_are_preserved(string registry)
    {
        Assert.Equal(registry + "/product/orders:1.4.2", Resolve(placements: Inputs(Placement(registry: registry))).ImageReference);
        var envelope = Envelope(); Workload(envelope)["properties"]!["image"] = new JsonObject { ["reference"] = registry + "/orders:v1" };
        Assert.Equal(registry + "/orders:v1", Resolve(envelope).ImageReference);
    }

    [Theory]
    [InlineData("http--api", false)]
    [InlineData("http-api", true)]
    public void Port_names_prohibit_adjacent_hyphens(string name, bool valid)
    {
        var envelope = Envelope(); Workload(envelope)["properties"]!["ports"] = new JsonObject { [name] = new JsonObject { ["containerPort"] = 8080 } };
        if (valid) Assert.Equal(8080, Resolve(envelope).Declaration.Ports[name].Number);
        else RefusePipeline(envelope);
    }

    [Theory]
    [InlineData("configMapKey", ".", false)]
    [InlineData("configMapKey", "..", false)]
    [InlineData("configMapKey", "..data", false)]
    [InlineData("secretKey", ".", false)]
    [InlineData("secretKey", "..", false)]
    [InlineData("secretKey", "..data", false)]
    [InlineData("configMapKey", ".config", true)]
    [InlineData("secretKey", "a.b", true)]
    public void Selector_keys_prohibit_directory_traversal_prefixes(string source, string key, bool valid)
    {
        var envelope = Envelope(); Workload(envelope)["properties"]!["env"] = new JsonObject
        { ["SETTING"] = new JsonObject { [source] = new JsonObject { ["name"] = "settings", ["key"] = key, ["optional"] = true } } };
        if (valid) Assert.Single(Resolve(envelope).Declaration.Environment); else RefusePipeline(envelope);
    }

    [Theory]
    [InlineData("/api//v1")]
    [InlineData("/api/../admin")]
    [InlineData("/api/./v1")]
    [InlineData("/api%2Fv1")]
    [InlineData("/api%2fv1")]
    [InlineData("/api/.")]
    [InlineData("/api/..")]
    public void Ingress_paths_reject_invalid_sequences_for_both_supported_path_types(string path)
    {
        foreach (var type in new[] { "Exact", "Prefix" })
        {
            var envelope = Envelope(Properties); var route = Workload(envelope)["properties"]!["routes"]![0]!;
            route["path"] = path; route["pathType"] = type; RefusePipeline(envelope);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_image_repository_paths_have_a_255_character_boundary(bool digest)
    {
        foreach (var length in new[] { 255, 256 })
        {
            var reference = "registry.example/" + new string('a', length) + (digest ? "@sha256:" + new string('a', 64) : ":v1");
            var envelope = Envelope(); Workload(envelope)["properties"]!["image"] = new JsonObject { ["reference"] = reference };
            if (length == 255) Assert.Equal(reference, Resolve(envelope).ImageReference); else RefusePipeline(envelope);
        }
    }

    [Theory]
    [InlineData("/api//v1")]
    [InlineData("/api/../admin")]
    [InlineData("/api%2Fv1")]
    public void Ingress_specific_path_rules_do_not_restrict_HTTP_probes(string path)
    {
        var envelope = Envelope(Properties);
        Workload(envelope)["properties"]!["probes"]!["liveness"]!["httpGet"]!["path"] = path;
        Assert.Equal(path, Resolve(envelope).Declaration.Probes.Liveness!.Path);
    }

    [Theory]
    [InlineData("Exact")]
    [InlineData("Prefix")]
    public void Ordinary_ingress_paths_survive_schema_snapshot_and_resolution(string type)
    {
        var envelope = Envelope(Properties); var route = Workload(envelope)["properties"]!["routes"]![0]!;
        route["path"] = "/api/v1/"; route["pathType"] = type;
        var result = Resolve(envelope);
        Assert.Equal("/api/v1/", Assert.Single(result.Declaration.Routes).Path);
        Assert.True(result.Settings.Ingress!.Enabled);
    }

    [Fact]
    public void Composed_repository_path_is_checked_after_prefix_and_name_are_combined()
    {
        var placement = Placement(settings: new JsonObject { ["repositoryPrefix"] = new string('p', 128) }.ToJsonString());
        var envelope = Envelope();
        Workload(envelope)["properties"]!["image"] = new JsonObject { ["name"] = new string('a', 126), ["tag"] = "v1" };
        Assert.Contains(new string('p', 128) + "/" + new string('a', 126), Resolve(envelope, Inputs(placement)).ImageReference, StringComparison.Ordinal);
        Workload(envelope)["properties"]!["image"]!["name"] = new string('a', 127);
        Assert.Throws<WorkloadContractException>(() => Resolve(envelope, Inputs(placement)));
    }

}
