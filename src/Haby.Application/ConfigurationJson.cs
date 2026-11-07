using System.Collections.Immutable;
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

    private static Utf8JsonWriter Writer(Stream stream) => new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, MaxDepth = 512 });
    private static bool Confidential(ConfigurationValue value) => value switch
    {
        ConfigurationSecretReference or ConfigurationDeferredValue => true,
        ConfigurationArray array => array.Items.Any(Confidential),
        ConfigurationObject obj => obj.Members.Values.Any(Confidential),
        _ => false
    };
    private static void Write(Utf8JsonWriter writer, ConfigurationValue value, string path, List<string>? redacted)
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
                    Write(writer, array.Items[i], path + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), redacted);
                writer.WriteEndArray();
                break;
            case ConfigurationObject obj:
                writer.WriteStartObject();
                foreach (var (key, child) in obj.Members.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    Write(writer, child, path + "/" + ConfigurationComposer.Escape(key), redacted);
                }
                writer.WriteEndObject();
                break;
            case ConfigurationSecretReference or ConfigurationDeferredValue when redacted is not null:
                writer.WriteStringValue(RedactedMarker);
                redacted.Add(path);
                break;
            default: throw new InvalidOperationException("Unsupported configuration value for JSON serialization.");
        }
    }
}
