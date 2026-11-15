using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Haby.Configuration.Contracts;

/// <summary>Non-secret immutable delivery settings supplied by the accepted deployment.</summary>
public sealed record RuntimeTls(string Mode, string? CaPem = null)
{
    public override string ToString() => nameof(RuntimeTls);
}
public sealed record RuntimeClientSettings(int TotalTimeoutSeconds = 300, int RpcTimeoutMilliseconds = 30000,
    int InitialBackoffMilliseconds = 250, int MaximumBackoffMilliseconds = 5000, int MaximumAggregateBytes = 16777216);
public sealed record RuntimeConfiguration(string Endpoint, RuntimeTls Tls, RuntimeClientSettings Client)
{
    public override string ToString() => nameof(RuntimeConfiguration);
}
public sealed record RuntimeBootstrap(int Format, string BindingId, ImmutableSortedDictionary<string, string> Documents,
    string ProjectedTokenPath, RuntimeConfiguration Delivery)
{
    public const string Variable = "HABY_RUNTIME_BOOTSTRAP";
    public const string TokenPath = "/var/run/haby/runtime/token";
    public override string ToString() => nameof(RuntimeBootstrap);
}

/// <summary>Closed, bounded bootstrap codec. It does not authenticate or grant document authority.</summary>
public static class RuntimeBootstrapCodec
{
    public const int MaximumBytes = 65536;
    public static RuntimeConfiguration ReadConfiguration(JsonElement element)
    {
        try
        {
            Object(element, ["endpoint", "tls", "client"], ["endpoint", "tls"]);
            var tls = element.GetProperty("tls"); Object(tls, ["mode", "caPem"], ["mode"]);
            var settings = new RuntimeClientSettings();
            if (element.TryGetProperty("client", out var client))
            {
                Object(client, ["totalTimeoutSeconds", "rpcTimeoutMilliseconds", "initialBackoffMilliseconds", "maximumBackoffMilliseconds", "maximumAggregateBytes"], []);
                int Number(string name, int fallback) => client.TryGetProperty(name, out var value) ?
                    value.ValueKind == JsonValueKind.Number && value.GetDecimal() == decimal.Truncate(value.GetDecimal()) ? checked((int)value.GetDecimal()) : throw Invalid() : fallback;
                settings = new(Number("totalTimeoutSeconds", 300), Number("rpcTimeoutMilliseconds", 30000),
                    Number("initialBackoffMilliseconds", 250), Number("maximumBackoffMilliseconds", 5000), Number("maximumAggregateBytes", 16777216));
            }
            var result = new RuntimeConfiguration(element.GetProperty("endpoint").GetString()!,
                new(tls.GetProperty("mode").GetString()!, tls.TryGetProperty("caPem", out var pem) ? pem.GetString() : null), settings);
            Validate(result); return result;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    public static void Validate(RuntimeConfiguration value)
    {
        if (value is not { Endpoint: not null, Tls: not null, Client: not null } || Encoding.UTF8.GetByteCount(value.Endpoint) is < 1 or > 2048 ||
            value.Endpoint.Any(c => !char.IsAscii(c) || char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '%') ||
            !Uri.TryCreate(value.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host.Length == 0 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || uri.IsLoopback ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || uri.Port is < 1 or > 65535 ||
            IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))) throw Invalid();
        var c = value.Client;
        if (c.TotalTimeoutSeconds is < 1 or > 3600 || c.RpcTimeoutMilliseconds is < 100 or > 30000 || c.RpcTimeoutMilliseconds > c.TotalTimeoutSeconds * 1000 ||
            c.InitialBackoffMilliseconds is < 1 or > 5000 || c.MaximumBackoffMilliseconds < c.InitialBackoffMilliseconds || c.MaximumBackoffMilliseconds > 30000 ||
            c.MaximumAggregateBytes is < 1 or > 67108864) throw Invalid();
        if (value.Tls.Mode == "system") { if (value.Tls.CaPem is not null) throw Invalid(); return; }
        if (value.Tls.Mode != "custom-ca" || value.Tls.CaPem is not { } text || Encoding.UTF8.GetByteCount(text) is < 1 or > 16384) throw Invalid();
        var remaining = text.AsSpan(); var count = 0; var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (!remaining.Trim().IsEmpty)
            {
                if (!PemEncoding.TryFind(remaining, out var pem) || !remaining[..pem.Location.Start.Value].Trim().IsEmpty ||
                    !remaining[pem.Label].SequenceEqual("CERTIFICATE") || ++count > 8) throw Invalid();
                using var certificate = X509Certificate2.CreateFromPem(remaining[pem.Location]);
                if (!certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(x => x.CertificateAuthority) || !seen.Add(certificate.GetCertHashString())) throw Invalid();
                remaining = remaining[pem.Location.End.Value..];
            }
            if (count == 0) throw Invalid();
        }
        catch (CryptographicException) { throw Invalid(); }
    }
    public static string Encode(RuntimeBootstrap value)
    {
        Validate(value);
        var json = JsonSerializer.Serialize(new BootstrapWire(value.Format, value.BindingId, value.Documents, value.ProjectedTokenPath,
            new(value.Delivery.Endpoint, value.Delivery.Tls), value.Delivery.Client), BootstrapJson.Default.BootstrapWire);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw Invalid(); return json;
    }
    public static RuntimeBootstrap Decode(string json)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) is < 1 or > MaximumBytes) throw Invalid();
            var root = JsonSerializer.Deserialize(json, BootstrapJson.Default.JsonElement);
            Object(root, ["format", "bindingId", "documents", "projectedTokenPath", "delivery", "client"], ["format", "bindingId", "documents", "projectedTokenPath", "delivery", "client"]);
            var documents = root.GetProperty("documents");
            if (documents.ValueKind != JsonValueKind.Object) throw Invalid();
            var map = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var pair in documents.EnumerateObject()) if (!map.TryAdd(pair.Name, pair.Value.GetString()!)) throw Invalid();
            var delivery = root.GetProperty("delivery"); Object(delivery, ["endpoint", "tls"], ["endpoint", "tls"]);
            var settings = root.GetProperty("client");
            Object(settings, ["totalTimeoutSeconds", "rpcTimeoutMilliseconds", "initialBackoffMilliseconds", "maximumBackoffMilliseconds", "maximumAggregateBytes"],
                ["totalTimeoutSeconds", "rpcTimeoutMilliseconds", "initialBackoffMilliseconds", "maximumBackoffMilliseconds", "maximumAggregateBytes"]);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); foreach (var property in delivery.EnumerateObject()) property.WriteTo(writer);
                writer.WritePropertyName("client"); settings.WriteTo(writer); writer.WriteEndObject();
            }
            var configuration = JsonSerializer.Deserialize(stream.ToArray(), BootstrapJson.Default.JsonElement);
            var result = new RuntimeBootstrap(root.GetProperty("format").GetInt32(), root.GetProperty("bindingId").GetString()!, map.ToImmutable(),
                root.GetProperty("projectedTokenPath").GetString()!, ReadConfiguration(configuration));
            Validate(result); return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    public static void Validate(RuntimeBootstrap value)
    {
        if (value is not { Format: 1, Documents: not null, Delivery: not null } || !Guid.TryParseExact(value.BindingId, "D", out var binding) ||
            binding == Guid.Empty || binding.ToString("D") != value.BindingId || value.ProjectedTokenPath != RuntimeBootstrap.TokenPath ||
            value.Documents.Count is < 1 or > 64 || value.Documents.Values.Distinct(StringComparer.Ordinal).Count() != value.Documents.Count) throw Invalid();
        foreach (var (name, id) in value.Documents)
            if (name.Length is < 1 or > 63 || !Regex.IsMatch(name, "\\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) ||
                !Guid.TryParseExact(id, "D", out var revision) || revision.Version != 7 || revision.ToString("D") != id) throw Invalid();
        Validate(value.Delivery);
    }
    private static void Object(JsonElement value, string[] allowed, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in value.EnumerateObject()) if (!allowed.Contains(pair.Name, StringComparer.Ordinal) || !seen.Add(pair.Name) || pair.Value.ValueKind == JsonValueKind.Null) throw Invalid();
        if (required.Any(x => !seen.Contains(x))) throw Invalid();
    }
    private static ArgumentException Invalid() => new("Runtime bootstrap is invalid.");
}

internal sealed record DeliveryWire(string Endpoint, RuntimeTls Tls);
internal sealed record BootstrapWire(int Format, string BindingId, ImmutableSortedDictionary<string, string> Documents,
    string ProjectedTokenPath, DeliveryWire Delivery, RuntimeClientSettings Client);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 8)]
[JsonSerializable(typeof(RuntimeBootstrap))]
[JsonSerializable(typeof(BootstrapWire))]
[JsonSerializable(typeof(JsonElement))]
internal partial class BootstrapJson : JsonSerializerContext;
