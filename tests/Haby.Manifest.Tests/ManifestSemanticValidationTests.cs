using System.Text;
using Haby.Manifest;
using Xunit;

namespace Haby.Manifest.Tests;

public class ManifestSemanticValidationTests
{
    [Fact]
    public void Resolves_local_references_and_does_not_resolve_external_exports()
    {
        var result = Assert.IsType<ManifestParseResult.Valid>(Parse("""
            "resources":{"db":{"type":"a/v1","providerRef":"p"}},
            "exports":{"out":{"resourceRef":"db","contract":"a/v1"}},
            "components":{"first":{"bindings":{
              "local":{"target":{"kind":"local-resource","resourceRef":"db"},"documentProjections":[{"documentRef":"settings","path":"/Db","rendererRef":"a/v1"}]},
              "external":{"target":{"kind":"application-export","applicationId":"other","exportRef":"unknown","contract":"a/v1"}}
            },"documents":{"settings":{"fileName":"settings.json","format":"json","template":{}}}}}
            """));
        Assert.True(result.Report.IsValid);
        Assert.Empty(result.Report.Diagnostics);
    }

    [Fact]
    public void Export_alone_marks_its_resource_used()
    {
        var result = Assert.IsType<ManifestParseResult.Valid>(Parse("""
            "resources":{"db":{"type":"a/v1","providerRef":"p"}},
            "exports":{"out":{"resourceRef":"db","contract":"a/v1"}}
            """));
        Assert.True(result.Report.IsValid);
        Assert.Empty(result.Report.Diagnostics);
    }

    [Fact]
    public void Local_binding_alone_marks_its_resource_used()
    {
        var result = Assert.IsType<ManifestParseResult.Valid>(Parse("""
            "resources":{"db":{"type":"a/v1","providerRef":"p"}},
            "components":{"first":{"bindings":{"local":{"target":{"kind":"local-resource","resourceRef":"db"}}}}}
            """));
        Assert.True(result.Report.IsValid);
        Assert.Empty(result.Report.Diagnostics);
    }

    [Fact]
    public void Missing_references_are_ordered_and_report_safe_parameters()
    {
        var report = Invalid("""
            "exports":{"out":{"resourceRef":"missing-export","contract":"a/v1"}},
            "components":{"first":{"bindings":{"b":{"target":{"kind":"local-resource","resourceRef":"missing-local"},"documentProjections":[{"documentRef":"missing-document","path":"/Db","rendererRef":"a/v1"}]}}}}
            """);
        Assert.Equal(new[]
        {
            "/spec/components/first/bindings/b/documentProjections/0/documentRef",
            "/spec/components/first/bindings/b/target/resourceRef",
            "/spec/exports/out/resourceRef"
        }, report.Diagnostics.Select(d => d.Path));
        Assert.Equal(new[] { "document", "resource", "resource" }, report.Diagnostics.Select(d => d.Parameters["targetKind"]));
        Assert.Equal(new[] { "missing-document", "missing-local", "missing-export" }, report.Diagnostics.Select(d => d.Parameters["reference"]));
        Assert.All(report.Diagnostics, d =>
        {
            Assert.Equal("manifest.reference.not-found", d.Code);
            Assert.Equal(ManifestDiagnosticSeverity.Error, d.Severity);
            Assert.Equal(2, d.Parameters.Count);
            Assert.Empty(d.RelatedPaths);
        });
    }

    [Fact]
    public void Projection_resolves_only_within_owning_component_and_rejects_text_target()
    {
        var report = Invalid("""
            "components":{
              "first":{"bindings":{"b":{"target":{"kind":"application-export","applicationId":"other","exportRef":"out","contract":"a/v1"},"documentProjections":[
                {"documentRef":"text","path":"/A","rendererRef":"a/v1"},
                {"documentRef":"other","path":"/B","rendererRef":"a/v1"}
              ]}},"documents":{"text":{"fileName":"text.txt","format":"text","template":"secret-value"}}},
              "second":{"documents":{"other":{"fileName":"text.txt","format":"json","template":{}}}}
            }
            """);
        Assert.Collection(report.Diagnostics,
            d =>
            {
                Assert.Equal("manifest.document.projection-target-invalid", d.Code);
                Assert.Equal("/spec/components/first/bindings/b/documentProjections/0/documentRef", d.Path);
                Assert.Equal("text-document", d.Parameters["reason"]);
                Assert.Equal(ManifestDiagnosticSeverity.Error, d.Severity);
            },
            d =>
            {
                Assert.Equal("manifest.reference.not-found", d.Code);
                Assert.Equal("/spec/components/first/bindings/b/documentProjections/1/documentRef", d.Path);
                Assert.Equal("document", d.Parameters["targetKind"]);
                Assert.Equal("other", d.Parameters["reference"]);
            });
        Assert.DoesNotContain("secret-value", string.Join("", report.Diagnostics.Select(d => d.Message + string.Join("", d.Parameters.Values))));
    }

    [Fact]
    public void Duplicate_file_names_use_ordinal_first_path_and_related_paths()
    {
        var report = Invalid("""
            "components":{"c":{"documents":{
              "z":{"fileName":"same.json","format":"json","template":{}},
              "a":{"fileName":"same.json","format":"yaml","template":{}},
              "m":{"fileName":"same.json","format":"json","template":{}},
              "other":{"fileName":"different.json","format":"text","template":""}
            }}}
            """);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("manifest.document.file-name-conflict", diagnostic.Code);
        Assert.Equal(ManifestDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("/spec/components/c/documents/a/fileName", diagnostic.Path);
        Assert.Equal("same.json", diagnostic.Parameters["fileName"]);
        Assert.Equal(new[] { "/spec/components/c/documents/m/fileName", "/spec/components/c/documents/z/fileName" }, diagnostic.RelatedPaths);
    }

    [Fact]
    public void Unused_resources_are_warnings_on_a_successful_result()
    {
        var result = Assert.IsType<ManifestParseResult.Valid>(Parse("""
            "resources":{"z":{"type":"a/v1","providerRef":"p"},"a":{"type":"a/v1","providerRef":"p"}},
            "components":{"c":{"bindings":{"external":{"target":{"kind":"application-export","applicationId":"elsewhere","exportRef":"z","contract":"a/v1"}}}}}
            """));
        Assert.True(result.Report.IsValid);
        Assert.False(result.Report.Truncated);
        Assert.Equal(new[] { "/spec/resources/a", "/spec/resources/z" }, result.Report.Diagnostics.Select(d => d.Path));
        Assert.All(result.Report.Diagnostics, d =>
        {
            Assert.Equal("manifest.resource.unused", d.Code);
            Assert.Equal(ManifestDiagnosticSeverity.Warning, d.Severity);
            Assert.Empty(d.Parameters);
            Assert.Empty(d.RelatedPaths);
        });
    }

    [Fact]
    public void Semantic_errors_and_independent_warnings_share_one_report()
    {
        var report = Invalid("""
            "resources":{"unused":{"type":"a/v1","providerRef":"p"}},
            "exports":{"out":{"resourceRef":"missing","contract":"a/v1"}}
            """);
        Assert.False(report.IsValid);
        Assert.Collection(report.Diagnostics,
            d =>
            {
                Assert.Equal("manifest.reference.not-found", d.Code);
                Assert.Equal(ManifestDiagnosticSeverity.Error, d.Severity);
            },
            d =>
            {
                Assert.Equal("manifest.resource.unused", d.Code);
                Assert.Equal(ManifestDiagnosticSeverity.Warning, d.Severity);
            });
    }

    [Fact]
    public void Semantic_warnings_obey_the_shared_diagnostic_limit()
    {
        var resources = string.Join(',', Enumerable.Range(0, 101)
            .Select(index => $"\"r{index:D3}\":{{\"type\":\"a/v1\",\"providerRef\":\"p\"}}"));
        var result = Assert.IsType<ManifestParseResult.Valid>(Parse("\"resources\":{" + resources + "}"));
        Assert.True(result.Report.IsValid);
        Assert.True(result.Report.Truncated);
        Assert.Equal(100, result.Report.Diagnostics.Count);
        Assert.Equal("/spec/resources/r000", result.Report.Diagnostics[0].Path);
        Assert.Equal("/spec/resources/r099", result.Report.Diagnostics[^1].Path);
    }

    [Fact]
    public void Schema_failure_prevents_semantic_reference_cascades()
    {
        var report = Invalid("""
            "resources":{"unused":{"type":"a/v1","providerRef":"p"}},
            "exports":{"out":{"resourceRef":"missing"}}
            """);
        Assert.Contains(report.Diagnostics, d => d.Code == "manifest.schema.required" && d.Path == "/spec/exports/out/contract");
        Assert.DoesNotContain(report.Diagnostics, d => d.Code is "manifest.reference.not-found" or "manifest.resource.unused");
    }

    private static ManifestValidationReport Invalid(string spec) =>
        Assert.IsType<ManifestParseResult.InvalidManifest>(Parse(spec)).Report;

    private static ManifestParseResult Parse(string spec)
    {
        var json = """{"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{SPEC}}}"""
            .Replace("SPEC", spec, StringComparison.Ordinal);
        return ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(json), TestManifestCatalog.Instance);
    }
}
