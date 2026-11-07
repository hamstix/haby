using System.Text;
using System.Text.Json;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Closed current planning format, with explicit frozen capability inputs and a checked saved layout.</summary>
internal static class ConfigurationPlanningSnapshotCodec
{
    internal const int Version = 2;
    internal static string Encode(ConfigurationPlanningSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject(); w.WriteNumber("format"u8, Version);
            w.WriteString("applicationRecord"u8, snapshot.Revision.ApplicationRecordId.ToString());
            w.WriteNumber("revision"u8, snapshot.Revision.RevisionNumber.Value);
            w.WritePropertyName("manifest"u8); Canonical(w, NormalizedSnapshotCodec.Encode(snapshot.Manifest));
            w.WriteStartArray("scopes"u8);
            foreach (var scope in snapshot.Scopes)
            {
                w.WriteStartObject();
                var (kind, application, component) = Target(scope.Target);
                w.WriteString("kind"u8, kind); w.WriteString("application"u8, application); w.WriteString("component"u8, component);
                if (scope.Token is null) w.WriteNull("token"u8); else w.WriteString("token"u8, scope.Token.ToString());
                w.WritePropertyName("decisions"u8); Canonical(w, AssignmentSnapshotCodec.Encode(scope.Declaration));
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteStartArray("profiles"u8);
            foreach (var profile in snapshot.Profiles) Canonical(w, ProfileSnapshotCodec.Encode(profile));
            w.WriteEndArray(); w.WritePropertyName("renderPins"u8); ConfigurationRenderPinCodec.Write(w, snapshot.RenderPins); w.WritePropertyName("layouts"u8); WriteLayouts(w, snapshot.Layouts); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static ConfigurationPlanningSnapshot Decode(string json, ManifestRevisionRef expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 160 });
            var root = doc.RootElement;
            Exact(root, "format", "applicationRecord", "revision", "manifest", "scopes", "profiles", "layouts", "renderPins");
            if (root.GetProperty("format"u8).GetInt32() != Version || root.GetProperty("applicationRecord"u8).GetString() != expected.ApplicationRecordId.ToString() ||
                root.GetProperty("revision"u8).GetInt64() != expected.RevisionNumber.Value) throw new InvalidDataException();
            var m = root.GetProperty("manifest"u8);
            var manifest = NormalizedSnapshotCodec.Decode(m.GetRawText(), m.GetProperty("api"u8).GetString()!,
                m.GetProperty("model"u8).GetInt32(), m.GetProperty("metadata"u8).GetProperty("id"u8).GetString()!, NormalizedSnapshotCodec.FormatVersion);
            var targets = ConfigurationPlanningSnapshot.Targets(manifest);
            var stored = root.GetProperty("scopes"u8).EnumerateArray().ToArray();
            if (stored.Length != targets.Count) throw new InvalidDataException();
            var scopes = stored.Select((s, i) =>
            {
                Exact(s, "kind", "application", "component", "token", "decisions");
                var (kind, application, component) = Target(targets[i]);
                if (s.GetProperty("kind"u8).GetString() != kind || s.GetProperty("application"u8).GetString() != application ||
                    s.GetProperty("component"u8).GetString() != component) throw new InvalidDataException();
                var token = s.GetProperty("token"u8);
                return new ConfigurationPlanningScope(targets[i], token.ValueKind == JsonValueKind.Null ? null : AssignmentToken.Parse(token.GetString()!),
                    AssignmentSnapshotCodec.Decode(s.GetProperty("decisions"u8).GetRawText(), targets[i]));
            });
            var profiles = root.GetProperty("profiles"u8).EnumerateArray().Select(p => ProfileSnapshotCodec.Decode(p.GetRawText(),
                p.GetProperty("id"u8).GetString()!, p.GetProperty("revision"u8).GetInt32(), ProfileSnapshotCodec.FormatVersion, ProfileSnapshotCodec.ModelVersion));
            var result = ConfigurationPlanningSnapshot.PrepareCore(expected, manifest, scopes, profiles, frozenPins: ConfigurationRenderPinCodec.Read(root.GetProperty("renderPins"u8))).Snapshot ?? throw new InvalidDataException();
            // Admission is based exclusively on frozen content. Unknown members, duplicate keys,
            // malformed layouts and a layout that disagrees with its exact inputs all refuse.
            using var canonical = new MemoryStream();
            using (var w = new Utf8JsonWriter(canonical)) Canonical(w, json);
            if (Encoding.UTF8.GetString(canonical.ToArray()) != CanonicalText(Encode(result))) throw new InvalidDataException();
            return result;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or
            FormatException or OverflowException or InvalidDataException or NullReferenceException)
        { throw new InvalidDataException("Stored configuration planning inputs are invalid or unsupported."); }
    }

    private static string CanonicalText(string json)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) Canonical(w, json);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static (string Kind, string Application, string Component) Target(ProfileAssignmentTarget target) => target switch
    {
        ProfileAssignmentTarget.Environment => ("environment", "", ""),
        ProfileAssignmentTarget.Application a => ("application", a.ApplicationId.Value, ""),
        ProfileAssignmentTarget.Component c => ("component", c.ApplicationId.Value, c.ComponentRef.Value),
        _ => throw new InvalidDataException()
    };
    private static void Canonical(Utf8JsonWriter w, string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 160 });
        Write(doc.RootElement);
        void Write(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var members = e.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                if (members.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != members.Length) throw new InvalidDataException();
                w.WriteStartObject(); foreach (var p in members) { w.WritePropertyName(p.Name); Write(p.Value); }
                w.WriteEndObject();
            }
            else if (e.ValueKind == JsonValueKind.Array) { w.WriteStartArray(); foreach (var x in e.EnumerateArray()) Write(x); w.WriteEndArray(); }
            else if (e.ValueKind == JsonValueKind.Number) w.WriteRawValue(e.GetRawText());
            else e.WriteTo(w);
        }
    }
    private static void Exact(JsonElement e, params string[] fields)
    {
        var names = e.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length != fields.Length || names.Distinct().Count() != fields.Length || names.Except(fields).Any()) throw new InvalidDataException();
    }
    private static void WriteLayouts(Utf8JsonWriter w, IReadOnlyList<EffectiveComponentLayout> layouts)
    {
        w.WriteStartArray();
        foreach (var layout in layouts)
        {
            w.WriteStartObject(); w.WriteString("component"u8, layout.Component.Value);
            w.WriteStartArray("decisions"u8);
            foreach (var d in layout.ScopeDecisions)
            {
                w.WriteStartObject(); w.WriteString("scope"u8, d.Scope.ToString()); w.WriteString("id"u8, d.Decision.ProfileId.Value);
                w.WriteString("kind"u8, d.Decision is ProfileDecision.Use ? "use" : "disable");
                if (d.Decision is ProfileDecision.Use use) w.WriteNumber("revision"u8, use.Reference.Revision);
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteStartArray("documents"u8);
            foreach (var d in layout.Documents)
            {
                w.WriteStartObject(); w.WriteString("id"u8, d.Definition.Id.Value); w.WriteString("file"u8, d.Definition.FileName);
                w.WriteString("format"u8, d.Definition.Format.ToString()); Value("template", d.Definition.Template); Origin(d.Origin); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteStartArray("contributions"u8);
            foreach (var c in layout.Contributions)
            {
                w.WriteStartObject(); w.WriteString("id"u8, c.Contribution.Id.Value); w.WriteString("document"u8, c.Contribution.Document.Value);
                w.WriteString("path"u8, c.Contribution.Path); Value("value", c.Contribution.Value); Origin(c.Origin); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteStartArray("requests"u8);
            foreach (var r in layout.BindingRequests)
            {
                w.WriteStartObject(); w.WriteString("binding"u8, r.BindingId.Value); w.WriteString("slot"u8, r.Slot?.Value);
                w.WriteString("renderer"u8, r.Renderer.Value); Value("options", r.Options); Origin(r.Origin);
                w.WriteStartObject("optionOrigins"u8);
                foreach (var pair in r.OptionOrigins.OrderBy(x => x.Key, StringComparer.Ordinal)) w.WriteString(pair.Key, pair.Value.ToString());
                w.WriteEndObject(); w.WriteStartArray("projections"u8);
                foreach (var p in r.Projections) { w.WriteStartObject(); w.WriteString("document"u8, p.Document.Value); w.WriteString("path"u8, p.Path); w.WriteEndObject(); }
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        w.WriteEndArray();
        void Value(string name, ConfigurationValue value) { w.WritePropertyName(name); Canonical(w, ConfigurationJson.Serialize(value).Json ?? throw new InvalidDataException()); }
        void Origin(ConfigurationLayoutOrigin o)
        {
            w.WriteString("source"u8, o.Source.Value); w.WriteString("profile"u8, o.Profile?.ProfileId.Value);
            if (o.Profile is { } p) w.WriteNumber("profileRevision"u8, p.Revision);
        }
    }
}
