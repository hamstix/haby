using System.Text;
using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ConfigurationProfileExpansionTests
{
    private static ManifestLocalId Id(string value) => ManifestLocalId.Create(value);
    private static ProfileRevisionRef Ref(string id, int revision = 1) => new(ProfileId.Create(id), revision);
    private static ProfileDecision Use(string id, int revision = 1) => new ProfileDecision.Use(Ref(id, revision));
    private static ProfileDecision Disable(string id) => new ProfileDecision.Disable(ProfileId.Create(id));
    private static ConfigurationString Text(string value) => new(value);
    private static ConfigurationObject Obj(params (string Key, ConfigurationValue Value)[] members) => new(members.Select(x => KeyValuePair.Create(x.Key, x.Value)));
    private static ProfileDocument Document(string id = "settings", string file = "appsettings.json", ConfigurationValue? template = null) => new(Id(id), file, StructuredDocumentFormat.Json, template ?? Obj());
    private static ProfileStaticContribution Static(string path, ConfigurationValue? value = null, string id = "static", string document = "settings") => new(Id(id), Id(document), path, value ?? Text("opaque"));
    private static ProfileBindingRule Rule(string slot = "main", string path = "/Connections/Main", string renderer = "sample.connection/v1", ConfigurationObject? defaults = null,
        IEnumerable<ProfileProjection>? projections = null) => new(Id(slot), Id(slot), ConfigurationRendererId.Create(renderer), defaults ?? Obj(), projections ?? [new(Id("settings"), path)]);
    private static ConfigurationProfileRevision Profile(string id, int revision = 1, IEnumerable<ProfileDocument>? documents = null,
        IEnumerable<ProfileStaticContribution>? contributions = null, IEnumerable<ProfileBindingRule>? rules = null) => new(Ref(id, revision), documents ?? [], contributions ?? [], rules ?? []);
    private static ConfigurationProfileRevision Layout() => Profile("layout", documents: [Document()]);
    private static ProfileExpansionResult Expand(IEnumerable<ConfigurationProfileRevision> profiles, IEnumerable<ProfileDecision> environment,
        IEnumerable<ProfileComponentInput>? components = null, IEnumerable<ProfileDecision>? application = null) =>
        new ConfigurationProfileExpander(new(profiles)).Expand(environment, application ?? [], components ?? [Component()]);
    private static EffectiveComponentLayout Only(ProfileExpansionResult result)
    {
        Assert.True(result.IsSuccess, string.Join(";", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        return Assert.Single(result.Layouts!);
    }
    private static void Fails(ProfileExpansionResult result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Layouts);
        Assert.Contains(result.Diagnostics, x => x.Code == code);
    }

    // These are existing published manifest fields. Slots remain separate typed inputs.
    private static ProfileComponentInput Component(string id = "api", string bindings = "{}", string documents = "{}",
        IEnumerable<ProfileDecision>? decisions = null, IEnumerable<BindingSlotAssociation>? slots = null,
        IEnumerable<ProfileStaticContribution>? contributions = null)
    {
        var json = """
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{
            "resources":{"one":{"type":"sample.resource/v1","providerRef":"available","properties":{}},
                         "two":{"type":"sample.resource/v1","providerRef":"available","properties":{}}},
            "components":{"COMPONENT":{"bindings":BINDINGS,"documents":DOCUMENTS}}}}}
            """.Replace("COMPONENT", id, StringComparison.Ordinal).Replace("BINDINGS", bindings, StringComparison.Ordinal).Replace("DOCUMENTS", documents, StringComparison.Ordinal);
        var catalog = new ManifestContractCatalog([new(ResourceTypeId.Create("sample.resource/v1"), "{}", "{}")], [], [],
            [new(ConfigurationRendererId.Create("sample.connection/v1"))]);
        var candidate = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes(json), catalog)).Candidate;
        return new(Id(id), candidate.Declaration.Spec.Components[Id(id)], decisions ?? [], slots ?? [], contributions);
    }
    private static string Binding(string resource = "one", string options = "{}", string projections = "[]") =>
        "{\"consume\":{\"target\":{\"kind\":\"local-resource\",\"resourceRef\":\"" + resource + "\"},\"options\":" + options + ",\"documentProjections\":" + projections + "}}";
    private static BindingSlotAssociation Slot(string slot = "main") => new(Id("consume"), Id(slot));

    [Fact]
    public void Environment_only_defaults_apply_per_component_and_only_explicit_slots_create_requests()
    {
        var profiles = new[] { Layout(), Profile("logging", contributions: [Static("/Serilog", Obj(("MinimumLevel", Text("Information"))))]),
            Profile("telemetry", contributions: [Static("/OpenTelemetry", Obj())]),
            Profile("connections", rules: [Rule(), Rule("events", "/Events", "sample.events/v1"), Rule("unused", "/Unused")]) };
        var result = Expand(profiles, profiles.Select(x => new ProfileDecision.Use(x.Reference)),
            [Component(bindings: Binding(), slots: [Slot()]), Component("worker", Binding("two"), slots: [Slot("events")]), Component("unbound")]);
        Assert.True(result.IsSuccess);
        var api = result.Layouts!.Single(x => x.Component == Id("api"));
        var worker = result.Layouts!.Single(x => x.Component == Id("worker"));
        Assert.All(result.Layouts!, x => Assert.Equal(2, x.Contributions.Count));
        Assert.Equal(Id("one"), Assert.IsType<NormalizedLocalResourceTarget>(Assert.Single(api.BindingRequests).Binding.Target).ResourceRef);
        Assert.Equal(Id("two"), Assert.IsType<NormalizedLocalResourceTarget>(Assert.Single(worker.BindingRequests).Binding.Target).ResourceRef);
        Assert.Equal(ConfigurationRendererId.Create("sample.events/v1"), Assert.Single(worker.BindingRequests).Renderer);
        Assert.Empty(result.Layouts!.Single(x => x.Component == Id("unbound")).BindingRequests);
        Assert.All(api.Decisions, x => Assert.Equal(ProfileSelectionScope.Environment, x.Scope));
        Assert.DoesNotContain(result.Layouts!.SelectMany(x => x.BindingRequests), x => x.Slot == Id("unused"));
    }

    [Fact]
    public void Whole_revision_replacement_precedes_expansion_and_preserves_independent_profiles()
    {
        var old = Profile("serilog", 3, contributions: [Static("/Serilog", Obj(("MinimumLevel", Text("Information"))))]);
        var app = Profile("serilog", 4, contributions: [Static("/Serilog", Obj(("MinimumLevel", Obj(("Default", Text("Warning")), ("Override", Obj(("Framework", Text("Error"))))))))]);
        var nearest = Profile("serilog", 5, contributions: [Static("/Serilog", Obj(("MinimumLevel", Text("Debug"))))]);
        var otel = Profile("otel", contributions: [Static("/OpenTelemetry")]);
        var profiles = new[] { Layout(), old, app, nearest, otel };
        var environment = new[] { Use("layout"), Use("serilog", 3), Use("otel") };
        var result = Expand(profiles, environment, [Component(decisions: [Use("serilog", 5)]), Component("worker")], [Use("serilog", 4)]);
        Assert.True(result.IsSuccess);
        foreach (var layout in result.Layouts!)
        {
            var revision = layout.Component == Id("api") ? nearest : app;
            Assert.Equal(revision.Contributions[0], layout.Contributions.Single(x => x.Contribution.Path == "/Serilog").Contribution);
            var selected = layout.Decisions.Single(x => x.Decision.ProfileId == ProfileId.Create("serilog"));
            Assert.Equal(revision.Reference, Assert.IsType<ProfileDecision.Use>(selected.Decision).Reference);
            Assert.Equal(layout.Component == Id("api") ? ProfileSelectionScope.Component : ProfileSelectionScope.Application, selected.Scope);
            Assert.Equal(otel.Reference, layout.Contributions.Single(x => x.Contribution.Path == "/OpenTelemetry").Origin.Profile);
        }
        // An absent inherited revision is harmless when a nearer decision replaces it before lookup.
        Only(Expand([Layout(), nearest], [Use("layout"), Use("serilog", 99)], [Component(decisions: [Use("serilog", 5)])]));
    }

    [Fact]
    public void Disable_add_reenable_and_duplicate_scope_decisions_have_no_enumeration_priority()
    {
        var profiles = new[] { Layout(), Profile("first", contributions: [Static("/Serilog")]), Profile("alternative", contributions: [Static("/Serilog")]) };
        Fails(Expand(profiles, [Use("layout"), Use("first")], application: [Use("alternative")]), "render.projection.path-conflict");
        var disabled = Only(Expand(profiles, [Use("layout"), Use("first")], application: [Disable("first"), Use("alternative")]));
        Assert.Equal(ProfileId.Create("alternative"), Assert.Single(disabled.Contributions).Origin.Profile!.ProfileId);
        var reenabled = Only(Expand(profiles, [Use("layout"), Use("first")], [Component(decisions: [Use("first"), Disable("alternative")])], [Disable("first"), Use("alternative")]));
        Assert.Equal(ProfileId.Create("first"), Assert.Single(reenabled.Contributions).Origin.Profile!.ProfileId);
        foreach (var decisions in new[] { new[] { Use("first"), Use("first") }, new[] { Use("first"), Disable("first") } })
        {
            var forward = Expand(profiles, decisions);
            Fails(forward, "profile.selection.duplicate");
            Assert.Equal(forward.Diagnostics, Expand(profiles.Reverse(), decisions.Reverse()).Diagnostics);
            Fails(Expand(profiles, [], application: decisions), "profile.selection.duplicate");
            Fails(Expand(profiles, [], [Component(decisions: decisions)]), "profile.selection.duplicate");
        }
    }

    [Fact]
    public void Missing_exact_revision_documents_rules_duplicate_definitions_file_names_and_slots_refuse()
    {
        Fails(Expand([Layout()], [Use("layout", 2)]), "profile.revision.not-found");
        Fails(Expand([Profile("missing", contributions: [Static("/x")])], [Use("missing")]), "profile.document.not-found");
        Fails(Expand([Layout(), Profile("other", documents: [Document()])], [Use("layout"), Use("other")]), "profile.document.duplicate");
        Fails(Expand([Profile("layout", documents: [Document(), Document("other")])], [Use("layout")]), "profile.document.file-conflict");
        var requested = Component(bindings: Binding(), slots: [Slot()]);
        Fails(Expand([Layout()], [Use("layout")], [requested]), "profile.slot.not-found");
        Fails(Expand([Layout(), Profile("a", rules: [Rule()]), Profile("b", rules: [Rule()])], [Use("layout"), Use("a"), Use("b")], [requested]), "profile.slot.ambiguous");
        Fails(Expand([Layout()], [Use("layout")], [Component(bindings: Binding(), slots: [Slot(), Slot()])]), "profile.slot.duplicate");
        Fails(Expand([Layout()], [Use("layout")], [Component(slots: [Slot()])]), "profile.binding.not-found");
        var missing = Profile("missing", rules: [Rule(projections: [new(Id("absent"), "/x")])]);
        Fails(Expand([Layout(), missing], [Use("layout"), Use("missing")], [requested]), "profile.document.not-found");
        Only(Expand([Layout(), missing], [Use("layout"), Use("missing")])); // Unused rule emits nothing.
    }

    [Fact]
    public void Several_projections_resolve_one_actual_binding_and_top_level_options_retain_exact_provenance()
    {
        var nested = Obj(("kept-only-at-profile", Text("no-deep-merge")));
        var defaults = Obj(("nested", nested), ("lower-only", Text("inherited")), ("nullable", Text("default")));
        var rule = Rule(defaults: defaults, projections: [new(Id("settings"), "/First"), new(Id("settings"), "/Second")]);
        var layout = Only(Expand([Layout(), Profile("client", rules: [rule])], [Use("layout"), Use("client")],
            [Component(bindings: Binding(options: """{"nested":{"replacement":[1,2]},"nullable":null}"""), slots: [Slot()])]));
        var request = Assert.Single(layout.BindingRequests);
        Assert.Equal(2, request.Projections.Count);
        Assert.Same(request.Binding.Options.Members["nested"], request.Options.Members["nested"]);
        Assert.NotEqual(nested, request.Options.Members["nested"]);
        Assert.Same(defaults.Members["lower-only"], request.Options.Members["lower-only"]);
        Assert.IsType<ConfigurationNull>(request.Options.Members["nullable"]);
        Assert.Equal(ProfileOptionScope.Profile, request.OptionOrigins["lower-only"]);
        Assert.Equal(ProfileOptionScope.Binding, request.OptionOrigins["nested"]);
        Assert.Equal(ProfileOptionScope.Binding, request.OptionOrigins["nullable"]);
        Assert.Equal(Id("main"), request.Slot);
        Assert.Equal(Ref("client"), request.Origin.Profile);
        Assert.Same(request.Binding.Options.Members["nested"], request.Options.Members["nested"]);
    }

    [Theory]
    [InlineData("/x", "/x")]
    [InlineData("/x", "/x/y")]
    [InlineData("/x/y", "/x")]
    [InlineData("/", "/")]
    [InlineData("/a~1b/~0/0", "/a~1b/~0")]
    public void Static_and_unresolved_binding_paths_share_complete_collision_validation(string first, string second)
    {
        var component = Component(bindings: Binding(), slots: [Slot()]);
        foreach (var mode in new[] { "static-static", "static-binding", "binding-binding" })
        {
            var contributions = mode == "static-static" ? new[] { Static(first, id: "one"), Static(second, id: "two") }
                : mode == "static-binding" ? new[] { Static(first) } : [];
            var rules = mode == "static-static" ? Array.Empty<ProfileBindingRule>() : new[] { Rule(projections: mode == "binding-binding"
                ? [new(Id("settings"), first), new(Id("settings"), second)] : [new(Id("settings"), second)]) };
            Fails(Expand([Layout(), Profile("values", contributions: contributions, rules: rules)], [Use("layout"), Use("values")],
                mode == "static-static" ? [Component()] : [component]), "render.projection.path-conflict");
        }
    }

    [Theory]
    [InlineData("leaf", "/leaf")]
    [InlineData("leaf", "/leaf/child")]
    [InlineData("array", "/array/0")]
    public void Existing_leaves_and_non_object_parents_refuse_for_static_and_requested_paths(string key, string path)
    {
        var template = Obj((key, key == "array" ? new ConfigurationArray([]) : Text("never-echo-value")));
        foreach (var useBinding in new[] { false, true })
            Fails(Expand([Profile("layout", documents: [Document(template: template)], contributions: useBinding ? [] : [Static(path)], rules: useBinding ? [Rule(path: path)] : [])],
                [Use("layout")], [Component(bindings: Binding(), slots: useBinding ? [Slot()] : [])]), "render.projection.template-collision");
    }

    [Fact]
    public void Root_and_invalid_paths_refuse_but_empty_escaped_numeric_and_dash_object_members_work()
    {
        foreach (var root in new ConfigurationValue[] { Text("private-value"), ConfigurationNull.Instance, new ConfigurationArray([]) })
            Fails(Expand([Profile("layout", documents: [Document(template: root)], rules: [Rule()])], [Use("layout")],
                [Component(bindings: Binding(), slots: [Slot()])]), "render.projection.template-collision");
        foreach (var path in new[] { "", "private-value", "/~2" })
        {
            var result = Expand([Layout(), Profile("bad", contributions: [Static(path)])], [Use("layout"), Use("bad")]);
            Fails(result, "render.projection.invalid-path");
            Assert.All(result.Diagnostics, x => Assert.Equal("", x.Path));
            Assert.DoesNotContain("private-value", string.Join(";", result.Diagnostics));
        }
        var good = Only(Expand([Layout(), Profile("good", contributions: [Static("/"), Static("/a~1b/~0/0", id: "numeric"), Static("/a~1b/~0/-", id: "dash")])], [Use("layout"), Use("good")]));
        var composed = new ConfigurationComposer(new([], [])).Compose(good.Documents[0].Definition.Template,
            good.Contributions.Select(x => new ConfigurationProjection(x.Contribution.Path, new(x.Contribution.Value), x.Origin.Source)));
        Assert.True(composed.IsSuccess);
    }

    [Fact]
    public void Application_documents_static_contributions_and_direct_projections_share_uniqueness_and_collision_checks()
    {
        const string docs = """{"settings":{"fileName":"appsettings.json","format":"json","template":{}}}""";
        var application = Component(documents: docs, contributions: [Static("/from-app")]);
        Fails(Expand([Layout()], [Use("layout")], [application]), "profile.document.duplicate");
        Only(Expand([Profile("other", contributions: [Static("/from-profile")])], [Use("other")], [application]));
        const string projections = """[{"documentRef":"settings","path":"/same","rendererRef":"sample.connection/v1"}]""";
        var direct = Component(bindings: Binding(projections: projections), documents: docs);
        var successful = Only(Expand([], [], [direct]));
        Assert.Null(Assert.Single(successful.BindingRequests).Slot);
        Fails(Expand([Profile("other", contributions: [Static("/same/child")])], [Use("other")], [direct]), "render.projection.path-conflict");
    }

    [Fact]
    public void Catalog_structural_equality_includes_all_behavior_fields_and_ignores_collection_order()
    {
        var docs = new[] { Document(), Document("second", "second.json") };
        var contributions = new[] { Static("/one"), Static("/two", id: "two") };
        var projections = new[] { new ProfileProjection(Id("settings"), "/first"), new ProfileProjection(Id("settings"), "/second") };
        var original = Profile("test", documents: docs, contributions: contributions, rules: [Rule(projections: projections)]);
        var same = Profile("test", documents: docs.Reverse(), contributions: contributions.Reverse(), rules: [Rule(projections: projections.Reverse())]);
        Assert.Equal(original, same);
        Assert.Equal(original.GetHashCode(), same.GetHashCode());
        var catalog = new ConfigurationProfileCatalog([original, same]);
        Assert.Equal(original, catalog.Find(original.Reference));
        Assert.Null(catalog.Find(Ref("test", 2)));
        var variants = new[] {
            Profile("test", documents: [Document(file: "different.json")], contributions: contributions, rules: original.Rules),
            Profile("test", documents: [Document(template: Text("different"))], contributions: contributions, rules: original.Rules),
            Profile("test", documents: [new(Id("settings"), "appsettings.json", StructuredDocumentFormat.Yaml, Obj())], contributions: contributions, rules: original.Rules),
            Profile("test", documents: docs, contributions: [Static("/different")], rules: original.Rules),
            Profile("test", documents: docs, contributions: [Static("/one", Text("different")), contributions[1]], rules: original.Rules),
            Profile("test", documents: docs, contributions: contributions, rules: [Rule(renderer: "sample.other/v1")]),
            Profile("test", documents: docs, contributions: contributions, rules: [Rule("other")]),
            Profile("test", documents: docs, contributions: contributions, rules: [Rule(defaults: Obj(("x", Text("different"))))]),
            Profile("test", documents: docs, contributions: contributions, rules: [Rule(projections: [new(Id("second"), "/different")])]),
            Profile("test", documents: docs, contributions: contributions.Concat(contributions), rules: original.Rules)
        };
        foreach (var variant in variants)
        {
            Assert.NotEqual(original, variant);
            var error = Assert.Throws<ArgumentException>(() => new ConfigurationProfileCatalog([original, variant]));
            Assert.DoesNotContain("different", error.Message);
        }
        Assert.Throws<ArgumentException>(() => ProfileId.Create("Bad_Id"));
        Assert.Equal("short-name", ProfileId.Create("short-name").Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => Ref("test", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ref("test", -1));
    }

    [Fact]
    public void Reordering_and_caller_mutation_cannot_change_catalog_inputs_layouts_or_provenance()
    {
        var documents = new List<ProfileDocument> { Document() };
        var projections = new List<ProfileProjection> { new(Id("settings"), "/z"), new(Id("settings"), "/a") };
        var rules = new List<ProfileBindingRule> { Rule(projections: projections) };
        var contributions = new List<ProfileStaticContribution> { Static("/s"), Static("/t", id: "two") };
        var profile = Profile("all", documents: documents, rules: rules, contributions: contributions);
        var profiles = new List<ConfigurationProfileRevision> { profile };
        var catalog = new ConfigurationProfileCatalog(profiles);
        var decisions = new List<ProfileDecision> { Use("all") };
        var slots = new List<BindingSlotAssociation> { Slot() };
        var component = Component(bindings: Binding(), decisions: decisions, slots: slots);
        var expander = new ConfigurationProfileExpander(catalog);
        var forward = Only(expander.Expand([], [], [component]));
        var reversedProfile = Profile("all", documents: documents.AsEnumerable().Reverse(), rules: rules.AsEnumerable().Reverse(), contributions: contributions.AsEnumerable().Reverse());
        var reverse = Only(Expand([reversedProfile], [], [Component(bindings: Binding(), decisions: decisions.AsEnumerable().Reverse(), slots: slots.AsEnumerable().Reverse())]));
        Assert.Equal(forward.Documents, reverse.Documents);
        Assert.Equal(forward.Contributions, reverse.Contributions);
        Assert.Equal(forward.Decisions, reverse.Decisions);
        Assert.Equal(forward.BindingRequests[0].Projections, reverse.BindingRequests[0].Projections);
        documents.Clear(); projections.Clear(); rules.Clear(); contributions.Clear(); profiles.Clear(); decisions.Clear(); slots.Clear();
        Assert.Equal(forward.Contributions, Only(expander.Expand([], [], [component])).Contributions);
        Assert.Single(forward.BindingRequests);
        Assert.Equal(2, forward.BindingRequests[0].Projections.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<EffectiveStaticContribution>)forward.Contributions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ProfileOptionScope>)forward.BindingRequests[0].OptionOrigins).Clear());
    }

    [Fact]
    public void Diagnostics_are_ordered_value_free_bounded_and_never_conceal_failure()
    {
        var contributions = Enumerable.Range(0, 120).Select(x => Static("/leaf/" + x, Text("private-fragment"), "item-" + x)).ToArray();
        var profile = Profile("bad", documents: [Document(template: Obj(("leaf", Text("private-template"))))], contributions: contributions);
        var forward = Expand([profile], [Use("bad")]);
        var reverse = Expand([Profile("bad", documents: profile.Documents, contributions: contributions.Reverse())], [Use("bad")]);
        Fails(forward, "render.projection.template-collision");
        Assert.Equal(120, forward.ErrorCount);
        Assert.Equal(100, forward.Diagnostics.Count);
        Assert.True(forward.IsTruncated);
        Assert.Equal(forward.Diagnostics, reverse.Diagnostics);
        Assert.DoesNotContain("private-", string.Join(";", forward.Diagnostics));
        var secret = new ConfigurationSecretReference(new(SecretId.New(), SecretVersionId.New()));
        var deferred = new ConfigurationDeferredValue(ConfigurationFormatterId.Create("sample.secret/v1"), Obj(("private-argument", secret)));
        foreach (var value in new ConfigurationValue[] { secret, deferred, Obj(("nested", new ConfigurationArray([secret]))) })
        {
            Fails(Expand([Profile("bad", documents: [Document(template: value)])], [Use("bad")]), "profile.value.unsupported");
            Fails(Expand([Layout(), Profile("bad", contributions: [Static("/x", value)])], [Use("layout"), Use("bad")]), "profile.value.unsupported");
            var errors = Expand([Layout(), Profile("bad", rules: [Rule(defaults: Obj(("private-option", value)))])], [Use("layout"), Use("bad")]);
            Fails(errors, "profile.value.unsupported");
            Assert.DoesNotContain("private-", string.Join(";", errors.Diagnostics));
            Assert.DoesNotContain(secret.Reference.SecretId.ToString(), string.Join(";", errors.Diagnostics));
        }
        Fails(Expand([Profile("bad", documents: [new(Id("settings"), "a.yaml", StructuredDocumentFormat.Yaml, Obj())])], [Use("bad")]), "profile.document.unsupported");
    }

    [Fact]
    public void Full_layout_and_diagnostics_are_independent_of_all_source_enumeration_orders()
    {
        var profiles = new[] { Layout(), Profile("alpha", contributions: [Static("/one"), Static("/two", id: "two")]),
            Profile("beta", rules: [Rule(projections: [new(Id("settings"), "/z"), new(Id("settings"), "/y")])]) };
        var environment = new[] { Use("layout"), Use("alpha"), Use("beta") };
        var application = new[] { Disable("unused"), Use("alpha") };
        var components = new[] { Component(bindings: Binding(), slots: [Slot()]), Component("worker", decisions: [Disable("beta"), Disable("unused")]) };
        var forward = Expand(profiles, environment, components, application);
        var reverse = Expand(profiles.Reverse(), environment.Reverse(), components.Reverse(), application.Reverse());
        Assert.True(forward.IsSuccess);
        Assert.True(reverse.IsSuccess);
        foreach (var pair in forward.Layouts!.Zip(reverse.Layouts!))
        {
            Assert.Equal(pair.First.Component, pair.Second.Component);
            Assert.Equal(pair.First.Decisions, pair.Second.Decisions);
            Assert.Equal(pair.First.ScopeDecisions, pair.Second.ScopeDecisions);
            Assert.Equal(pair.First.Documents, pair.Second.Documents);
            Assert.Equal(pair.First.Contributions, pair.Second.Contributions);
        }
        var conflicting = profiles.Append(Profile("collision", contributions: [Static("/one/child"), Static("/y", id: "two")]));
        var choices = environment.Append(Use("collision"));
        var bad = Expand(conflicting, choices, components, application);
        Fails(bad, "render.projection.path-conflict");
        Assert.Equal(bad.Diagnostics, Expand(conflicting.Reverse(), choices.Reverse(), components.Reverse(), application.Reverse()).Diagnostics);
    }

    [Fact]
    public void Two_distinct_bindings_cannot_request_one_slot_and_one_invalid_component_refuses_all_layouts()
    {
        var twoBindings = Binding()[..^1] + "," + Binding("two").Replace("consume", "other", StringComparison.Ordinal)[1..];
        var invalid = Component("invalid", twoBindings, slots: [Slot(), new(Id("other"), Id("main"))]);
        Fails(Expand([Layout(), Profile("rules", rules: [Rule()])], [Use("layout"), Use("rules")],
            [Component(), invalid]), "profile.slot.duplicate");
        var duplicates = Profile("duplicates", contributions: [Static("/a"), Static("/b")], rules: [Rule(), Rule()]);
        var result = Expand([Layout(), duplicates], [Use("layout"), Use("duplicates")]);
        Fails(result, "profile.contribution.duplicate");
        Fails(result, "profile.rule.duplicate");
        Fails(Expand([Layout()], [Use("layout")], [Component(), Component()]), "profile.component.duplicate");
        Fails(Expand([Profile("bad", documents: [Document(file: "private-directory/file.json")])], [Use("bad")]), "profile.document.invalid-file-name");
    }

    [Fact]
    public void Every_individual_content_field_participates_in_revision_conflict_detection()
    {
        var document = Document();
        var contribution = Static("/value");
        var rule = Rule(projections: [new(Id("settings"), "/client")]);
        var original = Profile("exact", documents: [document], contributions: [contribution], rules: [rule]);
        var variants = new[] {
            Profile("exact", documents: [Document("different")], contributions: [contribution], rules: [rule]),
            Profile("exact", documents: [Document(file: "different.json")], contributions: [contribution], rules: [rule]),
            Profile("exact", documents: [Document(template: Text("different"))], contributions: [contribution], rules: [rule]),
            Profile("exact", documents: [new(document.Id, document.FileName, StructuredDocumentFormat.Yaml, document.Template)], contributions: [contribution], rules: [rule]),
            Profile("exact", documents: [document], contributions: [Static("/value", id: "different")], rules: [rule]),
            Profile("exact", documents: [document], contributions: [Static("/value", document: "different")], rules: [rule]),
            Profile("exact", documents: [document], contributions: [Static("/different")], rules: [rule]),
            Profile("exact", documents: [document], contributions: [Static("/value", Text("different"))], rules: [rule]),
            Profile("exact", documents: [document], contributions: [contribution], rules: [new(Id("different"), rule.Slot, rule.Renderer, rule.Defaults, rule.Projections)]),
            Profile("exact", documents: [document], contributions: [contribution], rules: [new(rule.Id, Id("different"), rule.Renderer, rule.Defaults, rule.Projections)]),
            Profile("exact", documents: [document], contributions: [contribution], rules: [new(rule.Id, rule.Slot, ConfigurationRendererId.Create("different/v1"), rule.Defaults, rule.Projections)]),
            Profile("exact", documents: [document], contributions: [contribution], rules: [new(rule.Id, rule.Slot, rule.Renderer, Obj(("different", ConfigurationNull.Instance)), rule.Projections)]),
            Profile("exact", documents: [document], contributions: [contribution], rules: [new(rule.Id, rule.Slot, rule.Renderer, rule.Defaults, [new(Id("different"), "/client")])]),
            Profile("exact", documents: [document], contributions: [contribution], rules: [new(rule.Id, rule.Slot, rule.Renderer, rule.Defaults, [new(Id("settings"), "/different")])])
        };
        foreach (var variant in variants)
        {
            Assert.NotEqual(original, variant);
            Assert.Throws<ArgumentException>(() => new ConfigurationProfileCatalog([original, variant]));
        }
    }

    private static readonly ConfigurationFormatterId ConnectionFormat = ConfigurationFormatterId.Create("sample.connection/v1");
    private static readonly ConfigurationFormatterId EventsFormat = ConfigurationFormatterId.Create("sample.events/v1");
    private sealed record ConnectionOutput(ConfigurationSecretReference Credential);
    private sealed record EventsOutput(ConfigurationSecretReference Credential);
    private sealed record ConnectionInput(ConnectionOutput Output, ConfigurationString Consumer)
    {
        public ConfigurationFragment Fragment() => new(new ConfigurationDeferredValue(ConnectionFormat, Obj(("credential", Output.Credential), ("consumer", Consumer))));
    }
    private sealed record EventsInput(EventsOutput Output, ConfigurationNumber Batch)
    {
        public ConfigurationFragment Fragment() => new(new ConfigurationDeferredValue(EventsFormat, Obj(("credential", Output.Credential), ("batch", Batch))));
    }
    private sealed record ConnectionContract() : ConfigurationFormatterContract(ConnectionFormat)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => arguments.Members.Count == 2 &&
            arguments.Members.GetValueOrDefault("credential") is ConfigurationSecretReference && arguments.Members.GetValueOrDefault("consumer") is ConfigurationString;
    }
    private sealed record EventsContract() : ConfigurationFormatterContract(EventsFormat)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => arguments.Members.Count == 2 &&
            arguments.Members.GetValueOrDefault("credential") is ConfigurationSecretReference && arguments.Members.GetValueOrDefault("batch") is ConfigurationNumber;
    }

    [Fact]
    public void Two_synthetic_typed_families_use_actual_expansion_composer_and_redacted_preview_with_one_exact_secret()
    {
        var profiles = new[] { Layout(), Profile("logging", contributions: [Static("/Logging")]), Profile("clients", rules: [Rule(), Rule("events", "/Events", "sample.events/v1")]) };
        var components = new[] { Component(bindings: Binding(options: """{"consumer":"api"}"""), slots: [Slot()]),
            Component("worker", Binding(options: """{"consumer":"worker"}"""), slots: [Slot()]),
            Component("events", Binding("two", """{"batch":10}"""), slots: [Slot("events")]) };
        var result = Expand(profiles, profiles.Select(x => new ProfileDecision.Use(x.Reference)), components);
        Assert.True(result.IsSuccess);
        var secret = new ConfigurationSecretReference(new(SecretId.New(), SecretVersionId.New()));
        var formatters = new ConfigurationFormatterCatalog([new ConnectionContract(), new EventsContract()],
            [new(Id("connection"), ConnectionFormat), new(Id("events"), EventsFormat)]);
        var connectionOutput = new ConnectionOutput(secret);
        var eventsOutput = new EventsOutput(secret);
        var documents = new Dictionary<string, ConfigurationValue>(StringComparer.Ordinal);
        foreach (var layout in result.Layouts!)
        {
            var request = Assert.Single(layout.BindingRequests);
            var resource = Assert.IsType<NormalizedLocalResourceTarget>(request.Binding.Target).ResourceRef;
            Assert.Equal(layout.Component == Id("events") ? Id("two") : Id("one"), resource);
            // Test-only typed producers, after explicit exact renderer dispatch. No production renderer execution is added.
            var fragment = request.Renderer.Value switch
            {
                "sample.connection/v1" => new ConnectionInput(connectionOutput, Assert.IsType<ConfigurationString>(request.Options.Members["consumer"])).Fragment(),
                "sample.events/v1" => new EventsInput(eventsOutput, Assert.IsType<ConfigurationNumber>(request.Options.Members["batch"])).Fragment(),
                _ => throw new InvalidOperationException("Unknown test renderer.")
            };
            var produced = request.Projections.Select(x => new ConfigurationProjection(x.Path, fragment, request.BindingId));
            var staticFragments = layout.Contributions.Select(x => new ConfigurationProjection(x.Contribution.Path, new(x.Contribution.Value), x.Origin.Source));
            var composed = new ConfigurationComposer(formatters).Compose(Assert.Single(layout.Documents).Definition.Template, staticFragments.Concat(produced));
            Assert.True(composed.IsSuccess);
            documents.Add(layout.Component.Value, composed.Document!);
            var preview = ConfigurationJson.Preview(composed.Document!);
            Assert.Single(preview.RedactedPaths);
            using var json = JsonDocument.Parse(preview.Json);
            Assert.True(json.RootElement.GetProperty("document"u8).TryGetProperty("Logging"u8, out _));
            Assert.DoesNotContain("credential", preview.Json);
            Assert.DoesNotContain("consumer", preview.Json);
            Assert.DoesNotContain("batch", preview.Json);
            Assert.DoesNotContain(secret.Reference.SecretId.ToString(), preview.Json);
            Assert.DoesNotContain(secret.Reference.SecretVersionId.ToString(), preview.Json);
            Assert.False(ConfigurationJson.Serialize(composed.Document!).IsSuccess);
        }
        Assert.NotEqual(documents["api"], documents["worker"]);
        var apiDeferred = Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(documents["api"]).Members["Connections"]).Members["Main"]);
        var workerDeferred = Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(documents["worker"]).Members["Connections"]).Members["Main"]);
        Assert.Same(apiDeferred.Arguments.Members["credential"], workerDeferred.Arguments.Members["credential"]);
    }
}
