using System.Text;
using Haby.Manifest;
using Xunit;

namespace Haby.Manifest.Tests;

public class DocumentProjectionValidationTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    public void Non_object_template_root_is_valid_until_projected(string template)
    {
        Assert.IsType<ManifestParseResult.Valid>(Parse(template));
        var diagnostic = Assert.Single(Invalid(template, Projection("/value")).Diagnostics);
        Assert.Equal("manifest.document.projection-target-invalid", diagnostic.Code);
        Assert.Equal("non-object-root", diagnostic.Parameters["reason"]);
        Assert.Equal("/spec/components/c/bindings/a/documentProjections/0/documentRef", diagnostic.Path);
    }

    [Theory]
    [InlineData("", "root")]
    [InlineData("value", "invalid-pointer")]
    [InlineData("/bad~2escape", "invalid-pointer")]
    [InlineData("/bad~", "invalid-pointer")]
    public void Invalid_pointer_uses_specific_diagnostic(string pointer, string reason)
    {
        var diagnostic = Assert.Single(Invalid("{}", Projection(pointer)).Diagnostics);
        Assert.Equal("manifest.document.projection-path-invalid", diagnostic.Code);
        Assert.Equal(reason, diagnostic.Parameters["reason"]);
        Assert.Equal("/spec/components/c/bindings/a/documentProjections/0/path", diagnostic.Path);
    }

    [Fact]
    public void Escaped_segments_and_object_keys_are_valid()
    {
        var projections = string.Join(',', Projection("/a~1b/~0key"), Projection("/0"), Projection("/-"));
        Assert.IsType<ManifestParseResult.Valid>(Parse("{}", projections));
    }

    [Fact]
    public void Empty_key_pointer_passes_core_validation_and_maps()
    {
        var valid = Assert.IsType<ManifestParseResult.Valid>(Parse("{}", Projection("/")));

        var mapped = NormalizedApplicationManifestMapper.Map(valid.Manifest);

        Assert.Equal("/", Assert.Single(mapped.Spec.Components.Values.Single().Bindings.Values
            .SelectMany(binding => binding.DocumentProjections)).Path.Value);
    }

    [Theory]
    [InlineData("{\"leaf\":1}", "/leaf", "manifest.document.projection-collision", "existing-leaf")]
    [InlineData("{\"leaf\":null}", "/leaf/child", "manifest.document.projection-collision", "non-object-parent")]
    [InlineData("{\"leaf\":[]}", "/leaf/0", "manifest.document.projection-path-invalid", "array-traversal")]
    [InlineData("{\"a/b\":{\"~key\":true}}", "/a~1b/~0key", "manifest.document.projection-collision", "existing-leaf")]
    public void Static_template_collisions_have_accepted_mapping(string template, string pointer, string code, string reason)
    {
        var diagnostic = Assert.Single(Invalid(template, Projection(pointer)).Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(reason, diagnostic.Parameters["reason"]);
        Assert.Equal("/spec/components/c/bindings/a/documentProjections/0/path", diagnostic.Path);
    }

    [Fact]
    public void Same_and_overlapping_paths_across_bindings_report_related_paths()
    {
        var overlap = Assert.Single(Invalid("{}", Projection("/parent"), Projection("/parent/child")).Diagnostics);
        CheckConflict(overlap, "ancestor-descendant", 0, 0);
        var same = Assert.Single(Invalid("{}", Projection("/same"), Projection("/same", renderer: "b/v1")).Diagnostics);
        CheckConflict(same, "same-path", 0, 0);
    }

    [Fact]
    public void Exact_duplicate_in_one_binding_uses_specific_code_at_schema_stage()
    {
        var projection = Projection("/x");
        var diagnostic = Assert.Single(Invalid("{}", projection + "," + projection).Diagnostics);
        Assert.Equal("manifest.document.projection-duplicate", diagnostic.Code);
        Assert.Equal("/spec/components/c/bindings/a/documentProjections/0/path", diagnostic.Path);
        Assert.Equal(new[] { "/spec/components/c/bindings/a/documentProjections/1/path" }, diagnostic.RelatedPaths);
    }

    [Fact]
    public void Exact_duplicate_across_bindings_uses_specific_code()
    {
        var diagnostic = Assert.Single(Invalid("{}", Projection("/x"), Projection("/x")).Diagnostics);
        Assert.Equal("manifest.document.projection-duplicate", diagnostic.Code);
        Assert.Empty(diagnostic.Parameters);
        Assert.Equal("/spec/components/c/bindings/a/documentProjections/0/path", diagnostic.Path);
        Assert.Equal(new[] { "/spec/components/c/bindings/b/documentProjections/0/path" }, diagnostic.RelatedPaths);
    }

    [Fact]
    public void Missing_document_does_not_cascade_to_projection_target_or_collision()
    {
        var diagnostic = Assert.Single(Invalid("{}", Projection("/a", "missing")).Diagnostics);
        Assert.Equal("manifest.reference.not-found", diagnostic.Code);
        Assert.Equal("/spec/components/c/bindings/a/documentProjections/0/documentRef", diagnostic.Path);
    }

    [Fact]
    public void Projection_diagnostics_are_deterministic_and_redacted()
    {
        var first = Invalid("{\"password\":\"very-secret-value\"}", Projection("/password"), Projection("/password/child"));
        var second = Invalid("{\"password\":\"very-secret-value\"}", Projection("/password/child"), Projection("/password"));
        Assert.Equal(first.Diagnostics.Select(d => (d.Code, d.Parameters["reason"])).OrderBy(item => item.Item2),
            second.Diagnostics.Select(d => (d.Code, d.Parameters["reason"])).OrderBy(item => item.Item2));
        Assert.DoesNotContain("very-secret-value", string.Join(' ', first.Diagnostics.Select(d => d.Message + string.Join(' ', d.Parameters.Values))));
    }

    [Fact]
    public void Reordering_bindings_and_projections_preserves_full_ordered_diagnostics()
    {
        var a = string.Join(',', Projection("/same", renderer: "a/v1"), Projection("/same", renderer: "b/v1"));
        var aReversed = string.Join(',', Projection("/same", renderer: "b/v1"), Projection("/same", renderer: "a/v1"));
        var b = string.Join(',', Projection("/same", renderer: "c/v1"), Projection("/same", renderer: "d/v1"));
        var bReversed = string.Join(',', Projection("/same", renderer: "d/v1"), Projection("/same", renderer: "c/v1"));

        var original = Assert.IsType<ManifestParseResult.InvalidManifest>(ParseWithBindingOrder("{}", a, b, false)).Report;
        var reordered = Assert.IsType<ManifestParseResult.InvalidManifest>(ParseWithBindingOrder("{}", aReversed, bReversed, true)).Report;

        Assert.Equal(original.Diagnostics.Select(Describe), reordered.Diagnostics.Select(Describe));
        Assert.Equal(6, original.Diagnostics.Count);
        Assert.All(original.Diagnostics, diagnostic =>
        {
            Assert.Equal("manifest.document.projection-collision", diagnostic.Code);
            Assert.Equal("same-path", diagnostic.Parameters["reason"]);
        });
        Assert.Equal("/spec/components/c/bindings/a/documentProjections/0/path", original.Diagnostics[0].Path);
        Assert.Equal(new[] { "/spec/components/c/bindings/a/documentProjections/1/path" }, original.Diagnostics[0].RelatedPaths);
    }

    [Theory]
    [InlineData("{}", "/", true)]
    [InlineData("{\"\":1}", "/", false)]
    [InlineData("{\"a\":\"{{ parameters.value }}\"}", "/a/b", false)]
    public void Empty_key_and_liquid_value_follow_static_template_shape(string template, string pointer, bool valid)
    {
        var result = Parse(template, Projection(pointer));
        if (valid)
            Assert.IsType<ManifestParseResult.Valid>(result);
        else
        {
            var diagnostic = Assert.Single(Assert.IsType<ManifestParseResult.InvalidManifest>(result).Report.Diagnostics);
            Assert.Equal("manifest.document.projection-collision", diagnostic.Code);
            Assert.Equal(pointer == "/" ? "existing-leaf" : "non-object-parent", diagnostic.Parameters["reason"]);
        }
    }

    [Fact]
    public void Projection_diagnostics_obey_report_limit()
    {
        var projections = string.Join(',', Enumerable.Range(0, 101).Select(i => Projection($"/missing{i:D3}", "absent")));
        var report = Invalid("{}", projections);
        Assert.True(report.Truncated);
        Assert.Equal(100, report.Diagnostics.Count);
    }

    private static void CheckConflict(ManifestDiagnostic diagnostic, string reason, int first, int second)
    {
        Assert.Equal("manifest.document.projection-collision", diagnostic.Code);
        Assert.Equal(reason, diagnostic.Parameters["reason"]);
        Assert.Equal($"/spec/components/c/bindings/a/documentProjections/{first}/path", diagnostic.Path);
        Assert.Equal(new[] { $"/spec/components/c/bindings/b/documentProjections/{second}/path" }, diagnostic.RelatedPaths);
    }

    private static string Projection(string path, string document = "settings", string renderer = "a/v1") =>
        "{\"documentRef\":\"" + document + "\",\"path\":\"" + path + "\",\"rendererRef\":\"" + renderer + "\"}";

    private static ManifestValidationReport Invalid(string template, params string[] projections) =>
        Assert.IsType<ManifestParseResult.InvalidManifest>(Parse(template, projections)).Report;

    private static (string Code, ManifestDiagnosticSeverity Severity, string Path, string Parameters, string RelatedPaths) Describe(ManifestDiagnostic diagnostic) =>
        (diagnostic.Code, diagnostic.Severity, diagnostic.Path,
            string.Join(';', diagnostic.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + "=" + item.Value)),
            string.Join(';', diagnostic.RelatedPaths));

    private static ManifestParseResult Parse(string template, params string[] projections)
    {
        var a = projections.Length > 0 ? projections[0] : "";
        var b = projections.Length > 1 ? string.Join(',', projections.Skip(1)) : "";
        return ParseWithBindingOrder(template, a, b, false);
    }

    private static ManifestParseResult ParseWithBindingOrder(string template, string a, string b, bool reverseBindings)
    {
        const string target = "\"target\":{\"kind\":\"application-export\",\"applicationId\":\"other\",\"exportRef\":\"out\",\"contract\":\"a/v1\"}";
        var aBinding = "\"a\":{" + target + ",\"documentProjections\":[" + a + "]}";
        var bBinding = "\"b\":{" + target + ",\"documentProjections\":[" + b + "]}";
        var bindings = reverseBindings ? bBinding + "," + aBinding : aBinding + "," + bBinding;
        var json = "{\"applicationVersion\":\"1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"example\"},\"spec\":{\"components\":{\"c\":{\"documents\":{\"settings\":{\"fileName\":\"settings.json\",\"format\":\"json\",\"template\":" + template + "}},\"bindings\":{" + bindings + "}}}}}}";
        return ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(json), TestManifestCatalog.Instance);
    }
}
