using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredDeploymentPatchStatusTests
{
    [Fact]
    public void Status_classifier_identifies_generated_test_path_without_publishing_body_message_field_or_value()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        var patch = JsonNode.Parse("""[{"op":"test","path":"/spec/template/spec/containers/1/env/0/value","value":"protected-bootstrap"}]"""u8)!.AsArray();
        const string body = """
            {"reason":"Invalid","code":422,"message":"protected-message","details":{"causes":[
            {"field":"patch","reason":"FieldValueInvalid","message":"Invalid value: protected-bootstrap: testing value /spec/template/spec/containers/1/env/0/value failed: test failed"},
            {"field":"protected-field","reason":"protected-reason","message":"protected-response-body"}]}}
            """;
        var records = ConfiguredDeploymentPatchStatus.Classify(response, body, patch);
        Assert.Equal(new[] { "Q243 configured update rejection Status Invalid", "Q243 configured update rejection RvClassifier false",
            "Q243 configured update rejection Cause FieldValueInvalid BootstrapValueTest", "Q243 configured update rejection Cause unknown unknown" }, records);
        Assert.DoesNotContain("protected", string.Join("\n", records), StringComparison.Ordinal);
        Assert.Equal(new[] { "Q243 configured update rejection Shape unknown" }, ConfiguredDeploymentPatchStatus.Classify(response, "malformed protected-body", patch));
        Assert.Equal(new[] { "Q243 configured update rejection Shape oversized" }, ConfiguredDeploymentPatchStatus.Classify(response, new string('x', 131073), patch));
        Assert.Equal("Q243 configured update rejection Probe Control 200", ConfiguredDeploymentPatchStatus.Probe(ConfiguredDeploymentPatchStatus.ProbeCase.Control, 200));
        Assert.Equal("Q243 configured update rejection Probe BootstrapValueTest 422", ConfiguredDeploymentPatchStatus.Probe(ConfiguredDeploymentPatchStatus.ProbeCase.BootstrapValueTest, 422));
        Assert.Equal("Q243 configured update rejection Probe BootstrapValueTest unknown", ConfiguredDeploymentPatchStatus.Probe(ConfiguredDeploymentPatchStatus.ProbeCase.BootstrapValueTest, 1000));
        var positive = """
            {"reason":"Invalid","code":422,"details":{"causes":[{"field":"patch","reason":"FieldValueInvalid",
            "message":"Invalid value: patch: testing value /metadata/resourceVersion failed: test failed"}]}}
            """;
        Assert.Contains("Q243 configured update rejection RvClassifier true", ConfiguredDeploymentPatchStatus.Classify(response, positive,
            JsonNode.Parse("""[{"op":"test","path":"/metadata/resourceVersion","value":"19"}]"""u8)!.AsArray()));
    }
}
