using ApplicationId = Haby.Domain.ApplicationId;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Closed tagged storage encoding. Ordinary objects are always explicitly tagged, including deferred arguments.</summary>
public static class ConfigurationDocumentCodec
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxContainers = 64;
    public const int CodecDepth = 512;
    public const string Version = "haby.configuration-revision/v1";

    public static string Encode(ConfigurationDocumentRevision revision)
    {
        ValidateTree(revision.Tree);
        return EncodeBounded(w =>
        {
            var m = revision.Metadata;
            w.WriteStartArray(); w.WriteStringValue(Version); w.WriteStringValue(m.Id.ToString());
            w.WriteStringValue(m.Target.Deployment.ToString()); w.WriteNumberValue(m.Target.Generation.Value);
            w.WriteStringValue(m.Target.Component.Value); w.WriteStringValue(m.Target.Document.Value);
            w.WriteStringValue(m.Environment.Value); w.WriteStringValue(m.Application.Value);
            w.WriteStringValue(m.Revision.ApplicationRecordId.ToString()); w.WriteNumberValue(m.Revision.RevisionNumber.Value);
            w.WriteStringValue(m.Plan.ToString()); w.WriteStringValue(m.FileName); w.WriteStringValue(m.Format);
            w.WriteStringValue(m.Serializer); w.WriteStringValue(m.FrozenInputFingerprint);
            w.WriteStringValue(m.Actor); w.WriteStringValue(m.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteStringValue(revision.FrozenInputs); w.WriteStringValue(revision.OutputAssociations);
            WriteNode(w, revision.Tree); w.WriteEndArray();
        });
    }

    public static string Fingerprint(string payload) => "sha256:configuration-revision-v1:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    public static ConfigurationDocumentRevision Decode(string payload, string fingerprint)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(payload) > MaxBytes || Fingerprint(payload) != fingerprint) throw new InvalidDataException();
            using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = CodecDepth });
            var a = doc.RootElement;
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 20 || a[0].GetString() != Version) throw new InvalidDataException();
            string S(int i) => a[i].GetString() ?? throw new InvalidDataException();
            var tree = ReadNode(a[19]); ValidateTree(tree);
            var result = new ConfigurationDocumentRevision(new(ConfigurationDocumentRevisionId.Parse(S(1)),
                new(ApplicationDeploymentId.Parse(S(2)), DeploymentGeneration.Create(a[3].GetInt64()), ManifestLocalId.Create(S(4)), ManifestLocalId.Create(S(5))),
                EnvironmentId.Create(S(6)), ApplicationId.Create(S(7)), ManifestRevisionRef.Create(ApplicationRecordId.Parse(S(8)), ManifestRevisionNumber.Create(a[9].GetInt64())),
                DeploymentPlanId.Parse(S(10)), S(11), S(12), S(13), fingerprint, S(14), S(15),
                DateTimeOffset.ParseExact(S(16), "O", System.Globalization.CultureInfo.InvariantCulture)), S(17), S(18), tree);
            if (result.Metadata.Serializer != ConfigurationJson.Profile || result.Metadata.Format != "json" ||
                result.Metadata.CreatedAt.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(result.Metadata.Actor) || Encode(result) != payload)
                throw new InvalidDataException();
            _ = ConfigurationJson.Preview(tree);
            return result;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or IndexOutOfRangeException)
        { throw new InvalidDataException("Stored configuration revision is corrupt or unsupported."); }
    }

    public static void ValidateTree(ConfigurationValue tree)
        => ValidateTrees([tree]);

    internal static void ValidateTrees(IEnumerable<ConfigurationValue> trees)
    {
        long budget = MaxBytes;
        foreach (var tree in trees) Visit(tree, 0);
        void Charge(long count) { budget -= count; if (budget < 0) throw new ConfigurationDocumentLimitException(); }
        void Visit(ConfigurationValue value, int depth)
        {
            Charge(1);
            if (value is ConfigurationObject or ConfigurationArray && ++depth > MaxContainers) throw new ConfigurationDocumentLimitException();
            switch (value)
            {
                case ConfigurationString s: Charge(s.Value.Length); break;
                case ConfigurationNumber n: Charge(n.CanonicalText.Length); break;
                case ConfigurationObject o:
                    foreach (var (key, child) in o.Members) { Charge(key.Length); Visit(child, depth); }
                    break;
                case ConfigurationArray a: foreach (var child in a.Items) Visit(child, depth); break;
                // Arguments are protected payload, but must also be bounded before recursive encoding.
                case ConfigurationDeferredValue d: Visit(d.Arguments, 0); break;
            }
        }
    }

    internal static string EncodeBounded(Action<Utf8JsonWriter> encode)
    {
        using var stream = new ConfigurationBoundedStream(MaxBytes);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = CodecDepth })) encode(w);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter w, ConfigurationValue node)
    {
        w.WriteStartArray();
        switch (node)
        {
            case ConfigurationNull: w.WriteStringValue("null"u8); break;
            case ConfigurationBoolean b: w.WriteStringValue("bool"u8); w.WriteBooleanValue(b.Value); break;
            case ConfigurationString s: w.WriteStringValue("string"u8); w.WriteStringValue(s.Value); break;
            case ConfigurationNumber n: w.WriteStringValue("number"u8); w.WriteStringValue(n.CanonicalText); break;
            case ConfigurationSecretReference s:
                w.WriteStringValue("secret"u8); w.WriteStringValue(s.Reference.SecretId.ToString()); w.WriteStringValue(s.Reference.SecretVersionId.ToString()); break;
            case ConfigurationDeferredValue d:
                w.WriteStringValue("deferred"u8); w.WriteStringValue(d.Formatter.Value); WriteNode(w, d.Arguments); break;
            case ConfigurationArray a:
                w.WriteStringValue("array"u8); w.WriteStartArray(); foreach (var child in a.Items) WriteNode(w, child); w.WriteEndArray(); break;
            case ConfigurationObject o:
                w.WriteStringValue("object"u8); w.WriteStartObject();
                foreach (var (key, child) in o.Members.OrderBy(x => x.Key, StringComparer.Ordinal)) { w.WritePropertyName(key); WriteNode(w, child); }
                w.WriteEndObject(); break;
            default: throw new InvalidDataException();
        }
        w.WriteEndArray();
    }

    private static ConfigurationValue ReadNode(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() is < 1 or > 3) throw new InvalidDataException();
        var count = a.GetArrayLength();
        return (a[0].GetString(), count) switch
        {
            ("null", 1) => ConfigurationNull.Instance,
            ("bool", 2) => new ConfigurationBoolean(a[1].GetBoolean()),
            ("string", 2) => new ConfigurationString(a[1].GetString()!),
            ("number", 2) => ConfigurationNumber.Parse(a[1].GetString()!),
            ("secret", 3) => new ConfigurationSecretReference(new(SecretId.Parse(a[1].GetString()!), SecretVersionId.Parse(a[2].GetString()!))),
            ("deferred", 3) => new ConfigurationDeferredValue(ConfigurationFormatterId.Create(a[1].GetString()!), ReadNode(a[2]) as ConfigurationObject ?? throw new InvalidDataException()),
            ("array", 2) => new ConfigurationArray(a[1].EnumerateArray().Select(ReadNode)),
            ("object", 2) => new ConfigurationObject(a[1].EnumerateObject().Select(p => KeyValuePair.Create(p.Name, ReadNode(p.Value)))),
            _ => throw new InvalidDataException()
        };
    }
}

public sealed class ConfigurationDocumentLimitException() : IOException("Configuration document exceeds the supported limits.");

internal sealed class ConfigurationBoundedStream(int limit, bool reserve = false) : MemoryStream(reserve ? limit : 0)
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Length + count > limit) throw new ConfigurationDocumentLimitException();
        base.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Length + buffer.Length > limit) throw new ConfigurationDocumentLimitException();
        var bytes = buffer.ToArray();
        try { base.Write(bytes, 0, bytes.Length); }
        finally { Array.Clear(bytes); }
    }
}
