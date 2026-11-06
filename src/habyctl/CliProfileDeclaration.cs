using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal static partial class ProfileDeclarationReply
{
    // Structural transport validation only. Portable semantic acceptance belongs to the Host.
    internal static JsonElement Read(ProfileReply reply)
    {
        try
        {
            using var json = JsonDocument.Parse(reply.DeclarationJson.Memory, new JsonDocumentOptions { MaxDepth = 64 });
            var root = json.RootElement;
            UniqueMembers(root);
            Fields(root, ["apiVersion", "kind", "metadata", "spec"]);
            if (Text(root, "apiVersion") != "haby.dev/v1alpha1" || Text(root, "kind") != "ConfigurationProfile") throw Invalid();
            var metadata = root.GetProperty("metadata"u8);
            Fields(metadata, ["id", "revision"]);
            if (Text(metadata, "id") != reply.Reference.ProfileId ||
                !PositiveInt32(metadata.GetProperty("revision"u8), out var revision) || revision != reply.Reference.Revision) throw Invalid();
            var spec = root.GetProperty("spec"u8);
            Fields(spec, [], "documents", "staticContributions", "bindingRules");
            foreach (var member in Map(spec, "documents"))
            {
                var value = member.Value;
                Fields(value, ["fileName", "format", "template"]);
                _ = Text(value, "fileName");
                if (Text(value, "format") != "json") throw Invalid();
            }
            foreach (var member in Map(spec, "staticContributions"))
            {
                var value = member.Value;
                Fields(value, ["documentRef", "path", "value"], "secretProjections");
                _ = Text(value, "documentRef"); _ = Text(value, "path");
                if (!value.TryGetProperty("secretProjections"u8, out var secrets)) continue;
                if (secrets.ValueKind != JsonValueKind.Array) throw Invalid();
                foreach (var secret in secrets.EnumerateArray())
                {
                    Fields(secret, ["path", "format", "secretRef"]);
                    _ = Text(secret, "path");
                    if (Text(secret, "format") is not ("string" or "json")) throw Invalid();
                    var reference = secret.GetProperty("secretRef"u8);
                    Fields(reference, ["name", "version"]);
                    _ = Text(reference, "name");
                    if (!PositiveInt32(reference.GetProperty("version"u8), out _)) throw Invalid();
                }
            }
            foreach (var member in Map(spec, "bindingRules"))
            {
                var value = member.Value;
                Fields(value, ["slot", "rendererRef", "documentProjections"], "options");
                _ = Text(value, "slot"); _ = Text(value, "rendererRef");
                if (value.TryGetProperty("options"u8, out _)) _ = Object(value, "options");
                var projections = value.GetProperty("documentProjections"u8);
                if (projections.ValueKind != JsonValueKind.Array) throw Invalid();
                foreach (var projection in projections.EnumerateArray())
                {
                    Fields(projection, ["documentRef", "path"]);
                    _ = Text(projection, "documentRef"); _ = Text(projection, "path");
                }
            }
            return root.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // Never retain parser messages or body fragments in diagnostics.
            throw Invalid();
        }
    }

    // Exact bounded-integer transport decoding, not portable semantic validation.
    // Trim zero padding before parsing; huge exponents are refused without decimal
    // expansion or arbitrary-size integer arithmetic. JSON syntax is already checked.
    private static bool PositiveInt32(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText();
        if (raw[0] == '-') return false;
        var separator = raw.IndexOfAny(['e', 'E']);
        var mantissa = separator < 0 ? raw : raw[..separator];
        var exponent = 0;
        if (separator >= 0)
        {
            var token = raw[(separator + 1)..];
            var negative = token[0] == '-';
            if (token[0] is '+' or '-') token = token[1..];
            token = token.TrimStart('0');
            // No admissible body can have enough coefficient digits to balance
            // an exponent this large into a positive Int32.
            if (token.Length > 8) return false;
            if (token.Length > 0) exponent = int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
            if (negative) exponent = -exponent;
        }
        var point = mantissa.IndexOf('.');
        var fraction = point < 0 ? 0 : mantissa.Length - point - 1;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return false;
        var significant = digits.TrimEnd('0');
        exponent += digits.Length - significant.Length - fraction;
        if (exponent is < 0 or > 9 || significant.Length + exponent > 10) return false;
        return int.TryParse(significant + new string('0', exponent), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out result) && result > 0;
    }

    private static InvalidDataException Invalid() => new("Invalid Profile declaration reply.");
    private static string Text(JsonElement parent, string name) => parent.GetProperty(name).GetString() ?? throw Invalid();
    private static IEnumerable<JsonProperty> Map(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out _) ? Object(parent, name).EnumerateObject().ToArray() : [];
    private static JsonElement Object(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Object ? value : throw Invalid();
    }
    private static void Fields(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object || required.Any(name => !value.TryGetProperty(name, out _)) ||
            value.EnumerateObject().Any(p => !required.Contains(p.Name, StringComparer.Ordinal) && !optional.Contains(p.Name, StringComparer.Ordinal))) throw Invalid();
    }
    private static void UniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!names.Add(member.Name)) throw Invalid();
                UniqueMembers(member.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueMembers(item);
        else if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
    }
}

// The complete object has already passed structural/identity validation. Retain its
// portable number/string tokens when nesting it in the envelope; re-escaping here
// could make the extracted declaration exceed the unchanged import byte budget.
internal sealed class ProfileDeclarationElementConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) => writer.WriteRawValue(value.GetRawText());
}
