using System.Text;
using Haby.Domain;
using Xunit;

namespace Haby.Manifest.Tests;

public class ValidatedManifestCandidateTests
{
    [Fact]
    public void Candidate_snapshots_warnings_deeply_and_exposes_no_mutable_collections()
    {
        var payload = Encoding.UTF8.GetBytes("""{"applicationVersion":"1.0","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{}}}""");
        var declaration = Assert.IsType<ManifestCandidateResult.Valid>(
            ManifestCandidatePipeline.Prepare(payload, TestManifestCatalog.Instance)).Candidate.Declaration;
        var parameters = new Dictionary<string, string> { ["kind"] = "resource" };
        var relatedPaths = new List<string> { "/spec/resources/first" };
        var diagnostics = new[]
        {
            new ManifestDiagnostic("manifest.resource.unused", ManifestDiagnosticSeverity.Warning,
                "/spec/resources/second", "A Resource is unused.", parameters, relatedPaths),
            new ManifestDiagnostic("manifest.resource.unused", ManifestDiagnosticSeverity.Warning,
                "/spec/resources/third", "A Resource is unused.", new Dictionary<string, string>(), new List<string>())
        };
        var source = new ManifestValidationReport(diagnostics, true);

        var candidate = new ValidatedManifestCandidate(ApplicationVersion.Create("1.0"), declaration, source);
        diagnostics[1] = diagnostics[0];
        parameters["kind"] = "workload";
        relatedPaths[0] = "/changed";

        Assert.True(candidate.Warnings.Truncated);
        Assert.Equal(2, candidate.Warnings.Diagnostics.Count);
        Assert.Equal("/spec/resources/second", candidate.Warnings.Diagnostics[0].Path);
        Assert.Equal("/spec/resources/third", candidate.Warnings.Diagnostics[1].Path);
        Assert.Equal("resource", candidate.Warnings.Diagnostics[0].Parameters["kind"]);
        Assert.Equal("/spec/resources/first", candidate.Warnings.Diagnostics[0].RelatedPaths[0]);

        Assert.Throws<NotSupportedException>(() =>
            ((IList<ManifestDiagnostic>)candidate.Warnings.Diagnostics)[0] = diagnostics[1]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)candidate.Warnings.Diagnostics[0].Parameters)["kind"] = "changed");
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)candidate.Warnings.Diagnostics[0].RelatedPaths)[0] = "/changed");
        Assert.Equal("/spec/resources/second", candidate.Warnings.Diagnostics[0].Path);
        Assert.Equal("resource", candidate.Warnings.Diagnostics[0].Parameters["kind"]);
        Assert.Equal("/spec/resources/first", candidate.Warnings.Diagnostics[0].RelatedPaths[0]);
    }
}
