using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Haby.Manifest;

namespace Haby.Application;

// Local portable output policy only. Domain canonical numbers, configuration JSON,
// snapshot codecs and strict intake metadata deliberately retain their contracts.
internal static class PortableProfileJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = ManifestJsonContext.Default.WithAddedModifier(OmitDefaults),
        Converters = { new OrdinaryConverter(), new StringConverter() }
    };
    internal static readonly JsonTypeInfo<ConfigurationProfileDocument> DeclarationType =
        (JsonTypeInfo<ConfigurationProfileDocument>)Options.GetTypeInfo(typeof(ConfigurationProfileDocument));

    private static void OmitDefaults(JsonTypeInfo type)
    {
        foreach (var property in type.Properties)
        {
            if (type.Type == typeof(Haby.Manifest.ProfileMetadata) && property.Name == "revision" ||
                type.Type == typeof(ProfileNamedSecretDocument) && property.Name == "version")
                property.CustomConverter = new RevisionConverter();
            if (type.Type == typeof(ProfileSpec) || type.Type == typeof(ProfileContributionDocument) && property.Name == "secretProjections")
                property.ShouldSerialize = (_, value) => value is System.Collections.ICollection { Count: > 0 };
            else if (type.Type == typeof(ProfileRuleDocument) && property.Name == "options")
                property.ShouldSerialize = (_, value) => value is JsonElement element && element.EnumerateObject().Any();
        }
    }

    // Override the intake DTO's property-level converter only in this output
    // metadata. Contract integers have the same compact budget as ordinary numbers.
    private sealed class RevisionConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteRawValue(CompactNumber(Haby.Domain.ConfigurationNumber.Parse(value.ToString(CultureInfo.InvariantCulture)).CanonicalText));
    }

    private sealed class StringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteRawValue(Quote(value));
        // Portable declaration map keys are bounded ASCII local IDs; arbitrary
        // ordinary keys are encoded inside OrdinaryConverter instead.
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(value);
    }
    private sealed class OrdinaryConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
        {
            var text = new StringBuilder();
            WriteValue(text, value);
            writer.WriteRawValue(text.ToString());
        }
    }
    private static void WriteValue(StringBuilder text, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String: text.Append(Quote(value.GetString()!)); break;
            case JsonValueKind.Number: text.Append(CompactNumber(value.GetRawText())); break;
            case JsonValueKind.Null: text.Append("null"); break;
            case JsonValueKind.True: text.Append("true"); break;
            case JsonValueKind.False: text.Append("false"); break;
            case JsonValueKind.Array:
                text.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) text.Append(',');
                    firstItem = false;
                    WriteValue(text, item);
                }
                text.Append(']');
                break;
            case JsonValueKind.Object:
                text.Append('{');
                var firstMember = true;
                foreach (var member in value.EnumerateObject())
                {
                    if (!firstMember) text.Append(',');
                    firstMember = false;
                    text.Append(Quote(member.Name)).Append(':');
                    WriteValue(text, member.Value);
                }
                text.Append('}');
                break;
            default: throw new InvalidDataException("Unsupported Profile value.");
        }
    }

    // JSON-only minimal escaping also covers characters in the framework encoder's
    // global block list. Ordinary property names and contract string values follow
    // the same policy. Validate surrogate pairs before UTF-8 transcoding.
    private static string Quote(string value)
    {
        var text = new StringBuilder().Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) throw new InvalidDataException("Invalid Profile string.");
                        text.Append(c).Append(value[++i]);
                    }
                    else if (char.IsLowSurrogate(c)) throw new InvalidDataException("Invalid Profile string.");
                    else text.Append(c);
                    break;
            }
        }
        return text.Append('"').ToString();
    }

    internal static string CompactNumber(string canonical)
    {
        var separator = canonical.IndexOf('e');
        var coefficient = canonical[..separator];
        if (coefficient == "0") return "0";
        var exponent = BigInteger.Parse(canonical.AsSpan(separator + 1), CultureInfo.InvariantCulture);
        if (exponent.IsZero) return coefficient;
        var negative = coefficient[0] == '-';
        var digits = negative ? coefficient[1..] : coefficient;
        var point = digits.Length + exponent;
        // Compute lengths before allocating: huge exponents always stay scientific.
        var plainLength = (negative ? 1 : 0) + (exponent.Sign > 0 ? point : point > 0 ? digits.Length + 1 : 2 - exponent);
        if (plainLength >= canonical.Length) return canonical;
        var position = (int)point;
        var plain = exponent.Sign > 0 ? digits + new string('0', (int)exponent)
            : position > 0 ? digits.Insert(position, ".") : "0." + new string('0', -position) + digits;
        return negative ? "-" + plain : plain;
    }
}
