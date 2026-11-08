using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliConfigurationDocumentTarget(string DeploymentId, long Generation, string ComponentId, string DocumentId);
internal sealed record CliConfigurationDocumentArtifact(string RevisionId, string Environment, string ApplicationId,
    CliReference SourceRevision, string PlanId, string FileName, string Format, string Serializer, string Fingerprint,
    string FrozenInputFingerprint, string TechnicalActorId, string CreatedAtUtc);
internal sealed record CliConfigurationDocumentData(CliConfigurationDocumentTarget? Target, CliConfigurationDocumentArtifact? Artifact, JsonElement? Preview);

internal static class ConfigurationDocumentPresentation
{
    private const int MaxBytes = 4 * 1024 * 1024;
    internal const int MaxDocumentDepth = 64;
    internal static (string Outcome, int Exit, CliConfigurationDocumentData Data) Check(string command, ConfigurationDocumentRequest request, ConfigurationDocumentReply reply)
    {
        var status = reply.Status;
        var success = status is ConfigurationDocumentStatus.Created or ConfigurationDocumentStatus.Existing or ConfigurationDocumentStatus.Found;
        if (!Equals(reply.Selector, request) || reply.CalculateSize() > 8 * 1024 * 1024 || status is ConfigurationDocumentStatus.Unspecified or ConfigurationDocumentStatus.NotAttempted ||
            !Enum.IsDefined(status) || command == "materialize" && status == ConfigurationDocumentStatus.Found ||
            command != "materialize" && status is ConfigurationDocumentStatus.Created or ConfigurationDocumentStatus.Existing ||
            success != (reply.Artifact is not null) || success && reply.Target is null ||
            (command == "preview" && success) != !reply.PreviewJson.IsEmpty)
            throw new InvalidDataException("Invalid configuration reply.");
        CliConfigurationDocumentTarget? target = null;
        if (reply.Target is { } t)
        {
            if (!Uuid(t.DeploymentId) || t.Generation <= 0 || request.HasGeneration && request.Generation != t.Generation ||
                t.ComponentId != request.ComponentId || t.DocumentId != request.DocumentId) throw new InvalidDataException("Foreign configuration target.");
            target = new(t.DeploymentId, t.Generation, t.ComponentId, t.DocumentId);
        }
        CliConfigurationDocumentArtifact? artifact = null;
        if (reply.Artifact is { } a)
        {
            if (!Equals(a.Target, reply.Target) || a.Environment != request.Environment || a.ApplicationId != request.ApplicationId ||
                !Uuid(a.RevisionId) || !Uuid(a.PlanId) || a.SourceRevision is not { } r || !Uuid(r.ApplicationRecordId) || r.RevisionNumber <= 0 ||
                a.Format != "json" || a.Serializer != "haby.configuration-json/v1" || !Safe(a.FileName, 255) || a.FileName.IndexOfAny(['/', '\\']) >= 0 ||
                !Safe(a.TechnicalActorId, 256) || !a.Fingerprint.StartsWith("sha256:configuration-revision-v1:", StringComparison.Ordinal) ||
                a.Fingerprint.Length != 97 || a.FrozenInputFingerprint.Length != 64 || !Hex(a.FrozenInputFingerprint) || !Hex(a.Fingerprint[33..]) ||
                !DateTimeOffset.TryParseExact(a.CreatedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Invalid configuration artifact.");
            artifact = new(a.RevisionId, a.Environment, a.ApplicationId, new(r.ApplicationRecordId, r.RevisionNumber), a.PlanId, a.FileName, a.Format,
                a.Serializer, a.Fingerprint, a.FrozenInputFingerprint, a.TechnicalActorId, a.CreatedAtUtc);
        }
        JsonElement? preview = reply.PreviewJson.IsEmpty ? null : Preview(reply.PreviewJson);
        var name = status.ToString();
        return (char.ToLowerInvariant(name[0]) + name[1..], success ? 0 : 1, new(target, artifact, preview));
    }

    internal static JsonElement Preview(ByteString bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Oversized configuration preview.");
        try
        {
            using var doc = JsonDocument.Parse(bytes.Memory, new JsonDocumentOptions { MaxDepth = MaxDocumentDepth + 2 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "document", "redactedPaths" }))
                throw new InvalidDataException();
            var document = root.GetProperty("document");
            Validate(document, 0);
            var paths = root.GetProperty("redactedPaths").EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException()).ToArray();
            if (!paths.SequenceEqual(paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))) throw new InvalidDataException();
            foreach (var path in paths)
            {
                var node = document;
                if (path != "")
                {
                    if (!path.StartsWith('/')) throw new InvalidDataException();
                    foreach (var encoded in path[1..].Split('/'))
                    {
                        var segment = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                        if (segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal) != encoded) throw new InvalidDataException();
                        node = node.ValueKind == JsonValueKind.Object ? node.GetProperty(segment) :
                            node.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) &&
                            i >= 0 && i < node.GetArrayLength() && i.ToString(CultureInfo.InvariantCulture) == segment ? node[i] : throw new InvalidDataException();
                    }
                }
                if (node.ValueKind != JsonValueKind.String || node.GetString() != "***REDACTED***") throw new InvalidDataException();
            }
            return root.Clone();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Invalid configuration preview."); }
        static void Validate(JsonElement node, int depth)
        {
            if (node.ValueKind is JsonValueKind.Object or JsonValueKind.Array && ++depth > MaxDocumentDepth)
                throw new InvalidDataException("Configuration preview exceeds the semantic depth limit.");
            if (node.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in node.EnumerateObject()) { if (!seen.Add(p.Name)) throw new InvalidDataException(); Validate(p.Value, depth); }
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Validate(child, depth);
        }
    }
    private static bool Uuid(string text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.Version == 7;
    private static bool Safe(string text, int max) => text.Length > 0 && text.Length <= max && !text.Any(char.IsControl);
    private static bool Hex(string text) => text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed partial class CliCommandContext
{
    // CliEnvelope, data and preview add three containers around the semantic document.
    // Clone the generated options so other command groups retain their existing budget.
    private static readonly CliOutputJsonContext ConfigurationDocumentJsonContext = new(
        new JsonSerializerOptions(CliOutputJsonContext.Default.Options) { MaxDepth = ConfigurationDocumentPresentation.MaxDocumentDepth + 3 });

    internal Task<int> ExecuteConfigurationDocumentAsync(string command, ConfigurationDocumentRequest request, string? environment, long? generation, CancellationToken token)
    {
        if (generation <= 0) return ExecuteRevisionAsync(0, (_, _) => Task.FromResult(0), token);
        if (generation.HasValue) request.Generation = generation.Value;
        return ExecuteSecretAsync(environment, async (client, env, ct) =>
        {
            request.Environment = env;
            var reply = command switch
            {
                "materialize" => await client.MaterializeConfigurationAsync(request, ct),
                "get" => await client.GetConfigurationAsync(request, ct),
                _ => await client.PreviewConfigurationAsync(request, ct)
            };
            var checkedReply = ConfigurationDocumentPresentation.Check(command, request, reply);
            // Serialize and size-check the entire envelope before touching stdout.
            var envelope = new CliEnvelope<CliConfigurationDocumentData>(1, "configuration " + command, checkedReply.Outcome, checkedReply.Data, []);
            var json = JsonSerializer.Serialize(envelope, ConfigurationDocumentJsonContext.CliEnvelopeCliConfigurationDocumentData);
            if (Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(output.NewLine) > 8 * 1024 * 1024)
                throw new InvalidDataException("Oversized configuration response.");
            if (jsonOutput()) output.WriteLine(json);
            else
            {
                var target = checkedReply.Data.Target;
                output.WriteLine($"configuration {command}: {checkedReply.Outcome}; {env}/{request.ApplicationId}/{request.ComponentId}/{request.DocumentId}; generation={target?.Generation}; deployment={target?.DeploymentId}; revision={checkedReply.Data.Artifact?.RevisionId}");
                if (checkedReply.Data.Preview is { } preview) output.WriteLine(preview.GetRawText());
            }
            return checkedReply.Exit;
        }, token);
    }
}
