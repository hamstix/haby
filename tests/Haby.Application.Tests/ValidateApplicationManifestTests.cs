using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Xunit;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Application.Tests;

public class ValidateApplicationManifestTests
{
    private const string Schema = """{"type":"object","required":["enabled"],"properties":{"enabled":{"type":"boolean"}}}""";
    private static readonly ManifestContractCatalog Catalog = new(
        [new ResourceTypeDefinition(ResourceTypeId.Create("example.resource/v1"), Schema)],
        [], [], []);

    [Fact]
    public void Valid_input_creates_a_normalized_candidate_but_exposes_only_identity_and_warnings()
    {
        var payload = Envelope("\"resources\":{\"database\":{\"type\":\"example.resource/v1\",\"providerRef\":\"main\",\"properties\":{\"enabled\":true}}}");

        var prepared = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(payload, Catalog));
        Assert.Equal(ApplicationId.Create("example"), prepared.Candidate.ApplicationId);
        Assert.Equal(ApplicationVersion.Create("1.0"), prepared.Candidate.ApplicationVersion);
        Assert.Equal("haby.dev/v1alpha1", prepared.Candidate.Declaration.InputApiVersion);
        Assert.Equal(NormalizedApplicationManifest.CurrentModelVersion, prepared.Candidate.Declaration.NormalizedModelVersion);
        Assert.Single(prepared.Candidate.Declaration.Spec.Resources);
        Assert.Contains(prepared.Candidate.Warnings.Diagnostics, d => d.Code == "manifest.resource.unused");

        var result = Assert.IsType<ValidateApplicationManifestResult.Valid>(ValidateApplicationManifest.Execute(payload, Catalog));
        Assert.Equal(prepared.Candidate.ApplicationId, result.ApplicationId);
        Assert.Equal(prepared.Candidate.ApplicationVersion, result.ApplicationVersion);
        Assert.Contains(result.Warnings.Diagnostics, d => d.Code == "manifest.resource.unused");
        Assert.DoesNotContain(typeof(ValidateApplicationManifestResult.Valid).GetProperties(),
            property => property.PropertyType == typeof(NormalizedApplicationManifest) ||
                        property.PropertyType == typeof(ApplicationManifest));
    }

    [Fact]
    public void Invalid_request_and_manifest_keep_the_parser_outcomes()
    {
        var request = Assert.IsType<ValidateApplicationManifestResult.InvalidRequest>(
            ValidateApplicationManifest.Execute(Encoding.UTF8.GetBytes("""{"applicationVersion":"bad value","manifest":{}}"""), Catalog));
        Assert.Contains(request.RequestErrors, e => e.Code == "manifest-import.invalid-application-version" && e.Path == "/applicationVersion");

        var manifest = Assert.IsType<ValidateApplicationManifestResult.InvalidManifest>(
            ValidateApplicationManifest.Execute(Envelope("\"resources\":{\"database\":{\"type\":\"example.resource/v1\",\"providerRef\":\"main\",\"properties\":{\"enabled\":\"secret-value\"}}}"), Catalog));
        Assert.Contains(manifest.ValidationReport.Diagnostics, d => d.Code == "manifest.properties.schema-violation" && d.Path.StartsWith("/spec/resources/database/properties", StringComparison.Ordinal));
        Assert.DoesNotContain("secret-value", string.Join("", manifest.ValidationReport.Diagnostics.Select(d => d.Message + string.Join("", d.Parameters.Values))));
    }

    [Fact]
    public void Extension_errors_never_create_a_candidate()
    {
        var result = ManifestCandidatePipeline.Prepare(
            Envelope("\"resources\":{\"database\":{\"type\":\"example.resource/v1\",\"providerRef\":\"main\",\"properties\":{}}}"), Catalog);
        Assert.IsType<ManifestCandidateResult.InvalidManifest>(result);
    }

    [Fact]
    public void Core_manifest_errors_reach_the_application_outcome()
    {
        var payload = Encoding.UTF8.GetBytes("""{"applicationVersion":"1.0","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"Bad_Id"},"spec":{}}}""");

        var result = Assert.IsType<ValidateApplicationManifestResult.InvalidManifest>(
            ValidateApplicationManifest.Execute(payload, Catalog));
        Assert.Contains(result.ValidationReport.Diagnostics,
            diagnostic => diagnostic.Code == "manifest.id.invalid" && diagnostic.Path == "/metadata/id");
        Assert.IsType<ManifestCandidateResult.InvalidManifest>(ManifestCandidatePipeline.Prepare(payload, Catalog));
    }

    [Fact]
    public void Truncated_warnings_still_allow_normalization()
    {
        var resources = string.Join(",", Enumerable.Range(0, 101).Select(index =>
            $"\"resource{index:D3}\":{{\"type\":\"example.resource/v1\",\"providerRef\":\"main\",\"properties\":{{\"enabled\":true}}}}"));
        var payload = Envelope("\"resources\":{" + resources + "}");

        var prepared = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(payload, Catalog));
        Assert.Equal(101, prepared.Candidate.Declaration.Spec.Resources.Count);
        Assert.True(prepared.Candidate.Warnings.Truncated);
        Assert.Equal(100, prepared.Candidate.Warnings.Diagnostics.Count);
        var result = Assert.IsType<ValidateApplicationManifestResult.Valid>(ValidateApplicationManifest.Execute(payload, Catalog));
        Assert.True(result.Warnings.Truncated);
        Assert.Equal(100, result.Warnings.Diagnostics.Count);
    }

    [Fact]
    public void Repeated_validation_is_read_only_and_has_explicit_empty_warnings()
    {
        var payload = Envelope("");
        var first = Assert.IsType<ValidateApplicationManifestResult.Valid>(ValidateApplicationManifest.Execute(payload, Catalog));
        var second = Assert.IsType<ValidateApplicationManifestResult.Valid>(ValidateApplicationManifest.Execute(payload, Catalog));

        Assert.Equal(first.ApplicationId, second.ApplicationId);
        Assert.Equal(first.ApplicationVersion, second.ApplicationVersion);
        Assert.Empty(first.Warnings.Diagnostics);
        Assert.Empty(second.Warnings.Diagnostics);
        Assert.False(first.Warnings.Truncated);

        var firstCandidate = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(payload, Catalog)).Candidate;
        var secondCandidate = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(payload, Catalog)).Candidate;
        Assert.Equal(firstCandidate.Declaration, secondCandidate.Declaration);
    }

    private static byte[] Envelope(string specMembers) => Encoding.UTF8.GetBytes(
        "{\"applicationVersion\":\"1.0\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"example\"},\"spec\":{" + specMembers + "}}}");
}
