using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class PostgreSqlPairPlanningTests
{
    internal static readonly ManifestContractCatalog Catalog = new(
        [PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [], [], []);
    internal static readonly ProviderAdapterKey Provider = new("postgresql/v1", "haby.postgresql.connection/v1", PostgreSqlDatabaseCheckpointCapability.ImplementationId);
    internal static DurableActionAdmission Admission() => new(new([
        new PostgreSqlPairResourceAdmission(PostgreSqlDatabaseContract.Id),
        new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id)], []));
    internal static JsonObject Example()
    {
        using var stream = typeof(PostgreSqlPairPlanningTests).Assembly.GetManifestResourceStream("Haby.PostgreSql.IntegrationTests.pair.json")!;
        using var reader = new StreamReader(stream);
        return new JsonObject { ["applicationVersion"] = "v1", ["manifest"] = JsonNode.Parse(reader.ReadToEnd()) };
    }
    internal static byte[] Bytes(JsonObject envelope) => Encoding.UTF8.GetBytes(envelope.ToJsonString());
    internal static NormalizedManifestSpec Spec(JsonObject envelope) => Assert.IsType<ManifestCandidateResult.Valid>(
        ManifestCandidatePipeline.Prepare(Bytes(envelope), Catalog)).Candidate.Declaration.Spec;
    internal static PreparedProviderConfiguration Configuration() => new PostgreSqlProviderConfigurationCodec(new PostgreSqlDatabaseCheckpointCapability())
        .Encode(new PostgreSqlProviderConfiguration(1, "localhost", 5432, "postgres", "postgres",
            new(SecretId.New(), SecretVersionId.New()), Provider.ImplementationId));
    internal static DurablePlanningInputs Inputs() => new(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), ProviderAlias.Create("postgresql.primary"),
        ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), Provider.ImplementationId);

    [Fact]
    public async Task Pair_description_uses_operation_graph_instead_of_unsupported_single_action()
    {
        var candidate = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Bytes(Example()), Catalog)).Candidate;
        var inputs = Inputs();
        var store = new InMemoryManifestImportStore();
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(store, Catalog, TimeProvider.System).ExecuteAsync(Bytes(Example()), "actor"));
        var revision = store.Find(imported.ManifestRevisionRef)!;
        inputs = inputs with { Revision = revision.Reference };
        var description = new PostgreSqlPlanActionDescriber().Describe(revision, inputs, Admission().Plan(candidate.Declaration.Spec, Provider, inputs, Configuration()));
        Assert.Equal("OperationGraph", description.Status.ToString());
        Assert.Null(description.Action);
    }

    [Theory]
    [InlineData("payload", "InputCorrupt")]
    [InlineData("name", "PinnedInputMismatch")]
    [InlineData("references", "PinnedInputMismatch")]
    [InlineData("unsupported", "UnsupportedShape")]
    public async Task Pair_description_preserves_corrupt_inconsistent_and_unsupported_outcomes(string fault, string expected)
    {
        var store = new InMemoryManifestImportStore();
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(store, Catalog, TimeProvider.System).ExecuteAsync(Bytes(Example()), "actor"));
        var revision = store.Find(imported.ManifestRevisionRef)!;
        var inputs = Inputs() with { Revision = revision.Reference };
        var graph = Admission().Plan(revision.Declaration.Spec, Provider, inputs, Configuration());
        graph = new DurableOperationGraph(graph.Nodes.Select(n => n.Id.Value != "db-restrict" && fault != "references" ? n : fault switch
        {
            "payload" => n with { Input = n.Input with { Json = "private-invalid-json" } },
            "name" => n with { Input = n.Input with { Json = n.Input.Json.Replace("sample_app", "other_db", StringComparison.Ordinal) } },
            "references" => n with { References = n.References with { ConfigurationRevision = ProviderConfigurationRevisionNumber.Create(2) } },
            _ => n with { Input = n.Input with { Format = "unknown/v1" } }
        }));
        var result = new PostgreSqlPlanActionDescriber().Describe(revision, inputs, graph);
        Assert.Equal(expected, result.Status.ToString());
        Assert.Null(result.Action);
        Assert.DoesNotContain("private-invalid-json", result.ToString());
    }

    [Fact]
    public async Task Equivalent_import_and_read_preserve_preexisting_historical_dependency_warning()
    {
        var store = new InMemoryManifestImportStore();
        var oldCatalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition,
            PostgreSqlApplicationLoginContract.Definition with { Dependency = null }], [], [], []);
        var original = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(store, oldCatalog, TimeProvider.System).ExecuteAsync(Bytes(Example()), "original"));
        var saved = store.Find(original.ManifestRevisionRef)!;
        Assert.Equal("/spec/resources/db", Assert.Single(saved.HistoricalWarnings.Diagnostics).Path);
        var repeat = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(store, Catalog, TimeProvider.System).ExecuteAsync(Bytes(Example()), "repeat"));
        Assert.Equal(original.ManifestRevisionRef, repeat.ManifestRevisionRef);
        Assert.Equal(saved, store.Find(original.ManifestRevisionRef));
        Assert.Equal("/spec/resources/db", Assert.Single(store.Find(repeat.ManifestRevisionRef)!.HistoricalWarnings.Diagnostics).Path);
    }

    [Fact]
    public void Recognized_login_dependency_is_used_but_a_true_orphan_is_warned()
    {
        var envelope = Example();
        envelope["manifest"]!["spec"]!["resources"]!["orphan"] = envelope["manifest"]!["spec"]!["resources"]!["db"]!.DeepClone();
        var candidate = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Bytes(envelope), Catalog)).Candidate;
        Assert.Equal(new[] { "/spec/resources/orphan" }, candidate.Warnings.Diagnostics.Where(d => d.Code == "manifest.resource.unused").Select(d => d.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Competing_owner_policies_are_rejected_independently_of_declaration_order_and_bindings(bool reverse)
    {
        var envelope = Example(); var resources = envelope["manifest"]!["spec"]!["resources"]!.AsObject();
        var duplicate = resources["app-login"]!.DeepClone();
        if (reverse)
        {
            resources.Remove("app-login"); resources["other-login"] = duplicate; resources["app-login"] = duplicate.DeepClone();
        }
        else resources["other-login"] = duplicate;
        var spec = Spec(envelope); var inputs = Inputs(); var configuration = Configuration(); var admission = Admission();
        Assert.False(PostgreSqlPairResourceAdmission.HasSingleOwner(spec));
        Assert.Equal(DurablePlanStatus.UnsupportedShape, admission.Admit(spec, Provider, inputs, configuration));
        var original = admission.Plan(Spec(Example()), Provider, inputs, configuration);
        Assert.False(admission.ValidateGraph(spec, Provider, inputs, configuration, original));
        Assert.True(PostgreSqlPairResourceAdmission.HasSingleOwner(Spec(Example())));
    }

    [Fact]
    public void Example_has_exact_five_mandatory_nodes_owners_dependencies_and_future_selectors_without_executors()
    {
        var spec = Spec(Example()); var inputs = Inputs(); var admission = Admission(); var config = Configuration();
        Assert.Equal(2, spec.Components.Count);
        Assert.All(spec.Components.Values, c => Assert.Equal("app-login", Assert.IsType<NormalizedLocalResourceTarget>(Assert.Single(c.Bindings).Value.Target).ResourceRef.Value));
        var graph = admission.Plan(spec, Provider, inputs, config);
        Assert.False(admission.Supports(graph));
        AssertGraph(graph);
        Assert.Equal(graph, DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(graph)));
        var material = GeneratedCredentialInputCodec.Decode(graph.Nodes.Single(n => n.Id.Value == "app-login-material").Input)!;
        Assert.Equal(new ManagedResourceKey(inputs.Environment, inputs.Revision.ApplicationRecordId, ManifestLocalId.Create("app-login")), material.Owner);
        Assert.Equal(GeneratedCredentialSlot.ApplicationPassword, material.SlotId);
        Assert.DoesNotContain("SecretVersion", DurableOperationGraphCodec.Encode(graph));
    }

    internal static void AssertGraph(DurableOperationGraph graph)
    {
        var expected = new Dictionary<string, (string Owner, string Action, string Format, string[] Dependencies, string[] Outputs)>
        {
            ["db-create"] = ("db", "postgresql.database.create/v1alpha1", "haby.postgresql.database-create/v1", [], []),
            ["db-restrict"] = ("db", "postgresql.database.restrict/v1alpha1", "haby.postgresql.database-restrict/v1", ["db-create"], ["db-create/database"]),
            ["app-login-material"] = ("app-login", GeneratedCredentialContracts.Action, GeneratedCredentialContracts.InputFormat, ["db-restrict"], []),
            ["app-login-create"] = ("app-login", "postgresql.application-login.create/v1alpha1", "haby.postgresql.application-login-create/v1", ["app-login-material", "db-create", "db-restrict"], ["app-login-material/password", "db-create/database"]),
            ["app-login-access"] = ("app-login", "postgresql.application-login.configure-access/v1alpha1", "haby.postgresql.application-login-configure-access/v1", ["app-login-create", "app-login-material", "db-create", "db-restrict"], ["app-login-create/role", "app-login-material/password", "db-create/database"])
        };
        Assert.Equal(5, graph.Nodes.Count);
        foreach (var node in graph.Nodes)
        {
            var e = expected[node.Id.Value];
            Assert.Equal(e.Owner, node.ResourceId.Value); Assert.Equal(e.Action, node.Action.ActionContract.Value); Assert.Equal(e.Format, node.Input.Format);
            Assert.Equal(e.Dependencies, node.Dependencies.Select(d => d.Value));
            Assert.Equal(e.Outputs, node.OutputReferences.Select(o => o.ProducerOperationId.Value + "/" + o.OutputKey));
            foreach (var selector in node.OutputReferences)
                Assert.Equal(selector.OutputKey switch
                {
                    "database" => PostgreSqlApplicationLoginContract.DatabaseOutput,
                    "role" => PostgreSqlApplicationLoginContract.RoleOutput,
                    _ => GeneratedCredentialContracts.OutputContract
                }, selector.OutputContract);
        }
    }

    [Theory]
    [InlineData("databaseRef", "missing")]
    [InlineData("databaseRef", "app-login")]
    [InlineData("databaseRef", "other-app/db")]
    [InlineData("schema", "public")]
    [InlineData("schema", "information_schema")]
    [InlineData("schema", "pg_private")]
    [InlineData("schema", "Bad")]
    [InlineData("privilegePolicy", "other/v1")]
    [InlineData("userName", "forbidden")]
    [InlineData("password", "forbidden")]
    [InlineData("unknown", "forbidden")]
    [InlineData("databaseRef", null)]
    public void Invalid_properties_are_refused_by_metadata_or_adapter(string property, string? value)
    {
        var envelope = Example(); var props = envelope["manifest"]!["spec"]!["resources"]!["app-login"]!["properties"]!.AsObject();
        if (value is null) props.Remove(property); else props[property] = value;
        if (property != "databaseRef" || value is null || value.Contains('/', StringComparison.Ordinal))
            Assert.Null(PostgreSqlApplicationLoginContract.ParseJson(props.ToJsonString()));
        var result = ManifestCandidatePipeline.Prepare(Bytes(envelope), Catalog);
        if (result is ManifestCandidateResult.Valid valid)
            Assert.NotEqual(DurablePlanStatus.Planned, Admission().Admit(valid.Candidate.Declaration.Spec, Provider, Inputs(), Configuration()));
        else Assert.IsType<ManifestCandidateResult.InvalidManifest>(result);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("extra-resource")]
    [InlineData("binding-target")]
    [InlineData("binding-options")]
    [InlineData("implementation")]
    [InlineData("configuration")]
    public void Unsupported_pair_relationships_and_exact_configuration_fail_closed(string mode)
    {
        var e = Example(); var spec = e["manifest"]!["spec"]!; var config = Configuration();
        if (mode == "alias") spec["resources"]!["app-login"]!["providerRef"] = "postgresql.other";
        if (mode == "extra-resource") spec["resources"]!["extra"] = spec["resources"]!["db"]!.DeepClone();
        if (mode == "binding-target") spec["components"]!["api"]!["bindings"]!["storage"]!["target"]!["resourceRef"] = "db";
        if (mode == "binding-options") spec["components"]!["api"]!["bindings"]!["storage"]!["options"]!["widen"] = true;
        if (mode == "implementation") config = config with { ImplementationId = "other.impl/v1" };
        if (mode == "configuration") config = config with { ShapeId = "other.configuration/v1" };
        var result = ManifestCandidatePipeline.Prepare(Bytes(e), Catalog);
        if (result is ManifestCandidateResult.Valid valid)
            Assert.NotEqual(DurablePlanStatus.Planned, Admission().Admit(valid.Candidate.Declaration.Spec, Provider, Inputs(), config));
        else Assert.IsType<ManifestCandidateResult.InvalidManifest>(result);
    }

    internal static DurableOperationGraph Tamper(DurableOperationGraph graph, string mode)
    {
        var node = graph.Nodes.Single(n => n.Id.Value == (mode == "owner" ? "app-login-create" : "app-login-access"));
        var selector = node.OutputReferences.Single(o => o.OutputKey == "database");
        var altered = mode switch
        {
            "producer" => node with { OutputReferences = node.OutputReferences.Select(o => o == selector ? o with { ProducerOperationId = OperationId.Create("db-restrict") } : o).ToArray() },
            "key" => node with { OutputReferences = node.OutputReferences.Select(o => o == selector ? o with { OutputKey = "other" } : o).ToArray() },
            "contract" => node with { OutputReferences = node.OutputReferences.Select(o => o == selector ? o with { OutputContract = "other.output/v1" } : o).ToArray() },
            "resource" => node with { ResourceId = ManifestLocalId.Create("db") },
            "action" => node with { Action = node.Action with { ActionContract = OperationContractId.Create("other.action/v1") } },
            "input" => node with { Input = node.Input with { Format = "other.input/v1" } },
            "owner" => node with { Input = node.Input with { Json = node.Input.Json.Replace("\"resource\":\"app-login\"", "\"resource\":\"db\"", StringComparison.Ordinal) } },
            "dependency" => node with { Dependencies = node.Dependencies.Where(d => d.Value != "db-restrict").ToArray() },
            "provider" => node with { References = node.References with { ProviderInstance = ProviderInstanceId.New() } },
            _ => throw new ArgumentException("Unknown test mode.")
        };
        return new(graph.Nodes.Select(n => n.Id == altered.Id ? altered : n));
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("key")]
    [InlineData("contract")]
    [InlineData("resource")]
    [InlineData("action")]
    [InlineData("input")]
    [InlineData("owner")]
    [InlineData("dependency")]
    [InlineData("provider")]
    public void Frozen_graph_tampering_is_rejected_even_with_recomputed_fingerprint(string mode)
    {
        var spec = Spec(Example()); var inputs = Inputs(); var config = Configuration(); var admission = Admission();
        var graph = admission.Plan(spec, Provider, inputs, config);
        try
        {
            var tampered = Tamper(graph, mode);
            Assert.NotEqual(DurableOperationGraphCodec.Fingerprint(inputs, graph), DurableOperationGraphCodec.Fingerprint(inputs, tampered));
            Assert.False(admission.ValidateGraph(spec, Provider, inputs, config, tampered));
        }
        catch (InvalidDataException) { } // Structural/owner/reference guards can reject earlier.
    }

    [Fact]
    public void Canonical_owner_hash_and_semantic_graph_ignore_component_binding_and_resource_order()
    {
        var inputs = Inputs(); var e = Example(); var config = Configuration(); var admission = Admission();
        var a = admission.Plan(Spec(e), Provider, inputs, config);
        foreach (var obj in new[] { e["manifest"]!["spec"]!["resources"]!.AsObject(), e["manifest"]!["spec"]!["components"]!.AsObject() })
        {
            var pairs = obj.Reverse().Select(p => (p.Key, Value: p.Value!.DeepClone())).ToArray(); obj.Clear();
            foreach (var pair in pairs) obj.Add(pair.Key, pair.Value);
        }
        var b = admission.Plan(Spec(e), Provider, inputs, config);
        Assert.Equal(DurableOperationGraphCodec.Fingerprint(inputs, a), DurableOperationGraphCodec.Fingerprint(inputs, b));
        var owner = new ManagedResourceKey(EnvironmentRecordId.Parse("019b0000-0000-7000-8000-000000000001"),
            ApplicationRecordId.Parse("019b0000-0000-7000-8000-000000000002"), ManifestLocalId.Create("app-login"));
        Assert.Equal("haby_a_c2fdfb9f16cc051cf390b7f395e1f290b5020faf5bee3082", PostgreSqlPairResourceAdmission.RoleName(owner));
        Assert.NotEqual(PostgreSqlPairResourceAdmission.RoleName(owner), PostgreSqlPairResourceAdmission.RoleName(owner with { ResourceLocalId = ManifestLocalId.Create("other") }));
    }
}
