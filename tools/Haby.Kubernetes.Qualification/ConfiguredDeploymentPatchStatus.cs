using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haby.Kubernetes;
using k8s.Autorest;

// Fixture-only: inspect bounded Status text ephemerally; emit only literal categories.
internal static class ConfiguredDeploymentPatchStatus
{
    internal enum ProbeCase { Control, BootstrapValueTest }
    internal static string Probe(ProbeCase probe, int? status) => "Q243 configured update rejection Probe " + probe + " " +
        (status is >= 100 and <= 599 ? status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown");
    internal static IReadOnlyList<string> Classify(HttpResponseMessage response, string body, JsonArray patch)
    {
        if (body.Length > 131072) return ["Q243 configured update rejection Shape oversized"];
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            string Read(JsonElement element, ReadOnlySpan<byte> key) => element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            var reason = Read(root, "reason"u8) switch
            {
                "Invalid" => "Invalid",
                "BadRequest" => "BadRequest",
                "Conflict" => "Conflict",
                "Forbidden" => "Forbidden",
                "NotFound" => "NotFound",
                "Unauthorized" => "Unauthorized",
                _ => "unknown"
            };
            var strict = DeploymentKernel.IsResourceVersionTestRejection(new HttpOperationException
            { Response = new HttpResponseMessageWrapper(response, body) });
            var result = new List<string>
            {
                "Q243 configured update rejection Status " + reason,
                "Q243 configured update rejection RvClassifier " + (strict ? "true" : "false")
            };
            if (root.TryGetProperty("details"u8, out var details) && details.TryGetProperty("causes"u8, out var causes) && causes.ValueKind == JsonValueKind.Array)
            {
                if (causes.GetArrayLength() > 16) return ["Q243 configured update rejection Shape oversized"];
                foreach (var cause in causes.EnumerateArray())
                {
                    var causeReason = Read(cause, "reason"u8) switch
                    {
                        "FieldValueInvalid" => "FieldValueInvalid",
                        "FieldValueRequired" => "FieldValueRequired",
                        "FieldValueForbidden" => "FieldValueForbidden",
                        "FieldValueNotSupported" => "FieldValueNotSupported",
                        "FieldValueDuplicate" => "FieldValueDuplicate",
                        "FieldValueNotFound" => "FieldValueNotFound",
                        "UnexpectedServerResponse" => "UnexpectedServerResponse",
                        _ => "unknown"
                    };
                    result.Add("Q243 configured update rejection Cause " + causeReason + " " + Predicate(Read(cause, "message"u8), Read(cause, "field"u8), patch));
                }
            }
            var rootPredicate = Predicate(Read(root, "message"u8), "", patch);
            if (rootPredicate != "unknown") result.Add("Q243 configured update rejection Cause unknown " + rootPredicate);
            return result;
        }
        catch { return ["Q243 configured update rejection Shape unknown"]; }
    }
    private static string Predicate(string message, string field, JsonArray patch)
    {
        foreach (var operation in patch)
        {
            if (operation?["op"]?.GetValue<string>() != "test" || operation["path"]?.GetValue<string>() is not { } path) continue;
            // evanphx/json-patch's fixed test-failure suffix. Only a source-generated
            // request path can select a fixed category; never emit the path/message.
            if (!message.EndsWith("testing value " + path + " failed: test failed", StringComparison.Ordinal)) continue;
            return TestPathCategory(path);
        }
        if (field == "patch") return "PatchField";
        if (Regex.IsMatch(field, @"\Aspec\.template\.spec\.containers\[[0-9]{1,3}\]\.image\z", RegexOptions.CultureInvariant)) return "ImageField";
        if (Regex.IsMatch(field, @"\Aspec\.template\.spec\.containers\[[0-9]{1,3}\]\.env\[[0-9]{1,3}\]\.value\z", RegexOptions.CultureInvariant)) return "EnvironmentValueField";
        return "unknown";
    }
    internal static string TestPathCategory(string path)
    {
        if (path == "/metadata/uid") return "UidTest";
        if (path == "/metadata/resourceVersion") return "ResourceVersionTest";
        if (Regex.IsMatch(path, @"\A/spec/template/spec/containers/[0-9]{1,3}/name\z", RegexOptions.CultureInvariant)) return "ContainerNameTest";
        if (Regex.IsMatch(path, @"\A/spec/template/spec/containers/[0-9]{1,3}/env/[0-9]{1,3}/name\z", RegexOptions.CultureInvariant)) return "BootstrapNameTest";
        if (Regex.IsMatch(path, @"\A/spec/template/spec/containers/[0-9]{1,3}/env/[0-9]{1,3}/value\z", RegexOptions.CultureInvariant)) return "BootstrapValueTest";
        return "OtherGeneratedTest";
    }
}
