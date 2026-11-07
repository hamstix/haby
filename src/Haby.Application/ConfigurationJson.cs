using System.Collections.Immutable;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Failure publishes no bytes or partial JSON.</summary>
public sealed class ConfigurationJsonResult
{
    internal ConfigurationJsonResult(string? json) => Json = json;
    public string? Json { get; }
    public bool IsSuccess => Json is not null;
}

/// <summary>A separate redacted JSON envelope, never an application-ready document.</summary>
public sealed class ConfigurationJsonPreview
{
    internal ConfigurationJsonPreview(string json, IEnumerable<string> paths)
    {
        Json = json;
        RedactedPaths = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }
    public string Json { get; }
    public IReadOnlyList<string> RedactedPaths { get; }
}

/// <summary>Compact UTF-8 JSON profile haby.configuration-json/v1; no BOM or trailing newline.</summary>
public static class ConfigurationJson
{
    public const string Profile = "haby.configuration-json/v1";
    public const string RedactedMarker = "***REDACTED***";

    public static ConfigurationJsonResult Serialize(ConfigurationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Confidential(value)) return new(null);
        using var stream = new MemoryStream();
        using (var writer = Writer(stream)) Write(writer, value, "", null);
        return new(Encoding.UTF8.GetString(stream.ToArray()));
    }

    public static ConfigurationJsonPreview Preview(ConfigurationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ConfigurationDocumentCodec.ValidateTree(value);
        using var stream = new ConfigurationBoundedStream(ConfigurationDocumentCodec.MaxBytes);
        var paths = new List<string>();
        using (var writer = Writer(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("document"u8);
            Write(writer, value, "", paths);
            writer.WriteStartArray("redactedPaths"u8);
            foreach (var path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) writer.WriteStringValue(path);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return new(Encoding.UTF8.GetString(stream.ToArray()), paths);
    }

    internal static byte[] SerializeDelivery(ConfigurationValue value, IReadOnlyDictionary<string, ConfidentialConfigurationString>? resolved = null)
    {
        ConfigurationDocumentCodec.ValidateTree(value);
        if (resolved is null && Confidential(value)) throw new InvalidDataException();
        MeasureDelivery(value, resolved);
        using var stream = new ConfigurationBoundedStream(ConfigurationDocumentCodec.MaxBytes, reserve: true);
        try
        {
            using (var writer = Writer(stream)) Write(writer, value, "", null, resolved);
            return stream.ToArray();
        }
        finally
        {
            if (stream.TryGetBuffer(out var buffer)) Array.Clear(buffer.Array!, buffer.Offset, buffer.Count);
        }
    }

    // Count actual escaped UTF-8 before Utf8JsonWriter can allocate a large value buffer.
    // The same pinned encoder is used below; bounded scratch space never grows with material.
    private static void MeasureDelivery(ConfigurationValue value, IReadOnlyDictionary<string, ConfidentialConfigurationString>? resolved)
    {
        long remaining = ConfigurationDocumentCodec.MaxBytes;
        Visit(value, "");
        void Charge(long count) { remaining -= count; if (remaining < 0) throw new ConfigurationDocumentLimitException(); }
        void Text(string text)
        {
            Charge(2);
            ReadOnlySpan<char> input = text;
            Span<char> encoded = stackalloc char[256];
            try
            {
                while (!input.IsEmpty)
                {
                    var status = JavaScriptEncoder.Default.Encode(input, encoded, out var consumed, out var written, isFinalBlock: true);
                    Charge(Encoding.UTF8.GetByteCount(encoded[..written]));
                    if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall) || consumed == 0) throw new InvalidDataException();
                    input = input[consumed..];
                }
            }
            finally { encoded.Clear(); }
        }
        void Visit(ConfigurationValue node, string path)
        {
            switch (node)
            {
                case ConfigurationNull: Charge(4); break;
                case ConfigurationBoolean b: Charge(b.Value ? 4 : 5); break;
                case ConfigurationNumber n: Charge(n.CanonicalText.Length); break;
                case ConfigurationString s: Text(s.Value); break;
                case ConfigurationDeferredValue when resolved is not null && resolved.TryGetValue(path, out var confidential): Text(confidential.Reveal()); break;
                case ConfigurationObject o:
                    Charge(2 + Math.Max(0, o.Members.Count - 1));
                    foreach (var (key, child) in o.Members) { Text(key); Charge(1); Visit(child, path + "/" + ConfigurationComposer.Escape(key)); }
                    break;
                case ConfigurationArray a:
                    Charge(2 + Math.Max(0, a.Items.Count - 1));
                    for (var i = 0; i < a.Items.Count; i++) Visit(a.Items[i], path + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default: throw new InvalidDataException();
            }
        }
    }

    private static Utf8JsonWriter Writer(Stream stream) => new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, MaxDepth = 512 });
    private static bool Confidential(ConfigurationValue value) => value switch
    {
        ConfigurationSecretReference or ConfigurationDeferredValue => true,
        ConfigurationArray array => array.Items.Any(Confidential),
        ConfigurationObject obj => obj.Members.Values.Any(Confidential),
        _ => false
    };
    private static void Write(Utf8JsonWriter writer, ConfigurationValue value, string path, List<string>? redacted,
        IReadOnlyDictionary<string, ConfidentialConfigurationString>? resolved = null)
    {
        switch (value)
        {
            case ConfigurationNull: writer.WriteNullValue(); break;
            case ConfigurationBoolean boolean: writer.WriteBooleanValue(boolean.Value); break;
            case ConfigurationString text: writer.WriteStringValue(text.Value); break;
            case ConfigurationNumber number: writer.WriteRawValue(number.CanonicalText); break;
            case ConfigurationArray array:
                writer.WriteStartArray();
                for (var i = 0; i < array.Items.Count; i++)
                    Write(writer, array.Items[i], path + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), redacted, resolved);
                writer.WriteEndArray();
                break;
            case ConfigurationObject obj:
                writer.WriteStartObject();
                foreach (var (key, child) in obj.Members.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    Write(writer, child, path + "/" + ConfigurationComposer.Escape(key), redacted, resolved);
                }
                writer.WriteEndObject();
                break;
            case ConfigurationDeferredValue when resolved is not null && resolved.TryGetValue(path, out var confidential):
                writer.WriteStringValue(confidential.Reveal());
                break;
            case ConfigurationSecretReference or ConfigurationDeferredValue when redacted is not null:
                writer.WriteStringValue(RedactedMarker);
                redacted.Add(path);
                break;
            default: throw new InvalidOperationException("Unsupported configuration value for JSON serialization.");
        }
    }
}
