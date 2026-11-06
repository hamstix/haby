using System.Text;
using System.Text.Json;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Closed internal format, independent of the portable assignment parser.</summary>
internal static class AssignmentSnapshotCodec
{
    internal const int Version = 1;
    internal static string Encode(ConfigurationProfileAssignment declaration)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("format"u8, Version); writer.WriteNumber("model"u8, Version);
            writer.WriteStartArray("decisions"u8);
            foreach (var decision in declaration.Decisions.OrderBy(x => x.ProfileId.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("id"u8, decision.ProfileId.Value);
                writer.WriteString("kind"u8, decision is ProfileDecision.Use ? "use" : "disable");
                if (decision is ProfileDecision.Use use) writer.WriteNumber("revision"u8, use.Reference.Revision);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    internal static ConfigurationProfileAssignment Decode(string snapshot, ProfileAssignmentTarget target)
    {
        try
        {
            if (snapshot.Length > 32768) throw new InvalidDataException();
            using var document = JsonDocument.Parse(snapshot, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Exact(root, "format", "model", "decisions");
            if (root.GetProperty("format"u8).GetInt32() != Version || root.GetProperty("model"u8).GetInt32() != Version) throw new InvalidDataException();
            var decisions = root.GetProperty("decisions"u8);
            if (decisions.GetArrayLength() > 64) throw new InvalidDataException();
            var list = new List<ProfileDecision>();
            foreach (var item in decisions.EnumerateArray())
            {
                var kind = item.GetProperty("kind"u8).GetString();
                Exact(item, kind == "use" ? ["id", "kind", "revision"] : ["id", "kind"]);
                var id = ProfileId.Create(item.GetProperty("id"u8).GetString()!);
                list.Add(kind switch
                {
                    "use" => new ProfileDecision.Use(new(id, item.GetProperty("revision"u8).GetInt32())),
                    "disable" => new ProfileDecision.Disable(id),
                    _ => throw new InvalidDataException()
                });
            }
            return new(target, list);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException or InvalidDataException)
        { throw new InvalidDataException("Stored assignment snapshot is invalid or unsupported."); }
    }
    private static void Exact(JsonElement element, params string[] names)
    {
        var properties = element.EnumerateObject().Select(x => x.Name).ToArray();
        if (properties.Length != names.Length || properties.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            properties.Except(names, StringComparer.Ordinal).Any()) throw new InvalidDataException();
    }
}
