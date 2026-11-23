using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Application;

public enum ResourceAcquisitionMode { Provision, ImportUseAndRetain, Reference }
public enum ResourceOrigin { Provisioned, Imported, Referenced }
public enum ResourceLifecycleAuthority { Provisioned, UseAndRetain, ConsumptionOnly }

/// <summary>Adapter-owned, non-secret verification inputs for one exact accepted Resource.</summary>
public sealed record ResourceImportRequest(ManifestLocalId Resource, DurableActionPayload Input,
    ProtectedCredentialReference? Credential = null);

/// <summary>Separate operator intent. Every Resource is imported; mixed creation is refused.</summary>
public sealed class ResourceImportIntent : IEquatable<ResourceImportIntent>
{
    public IReadOnlyList<ResourceImportRequest> Resources { get; }

    public ResourceImportIntent(IEnumerable<ResourceImportRequest> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var supplied = resources.ToArray();
        if (supplied.Any(x => x?.Resource is null)) throw new ArgumentException("Invalid Resource import identity.");
        var items = supplied.OrderBy(x => x.Resource.Value, StringComparer.Ordinal).ToArray();
        if (items.Length is < 1 or > 256 || items.Select(x => x.Resource).Distinct().Count() != items.Length ||
            items.Any(x => x.Credential is { } credential && (credential.Id == Guid.Empty || credential.Reference is null) || x.Input is null || string.IsNullOrWhiteSpace(x.Input.Format) || x.Input.Format.Length > 128 ||
                string.IsNullOrWhiteSpace(x.Input.Json) || x.Input.Json.Length > 65536))
            throw new ArgumentException("Invalid Resource import intent.");
        Resources = Array.AsReadOnly(items);
    }

    public void Validate(NormalizedManifestSpec spec)
    {
        if (!spec.Resources.Keys.ToHashSet().SetEquals(Resources.Select(x => x.Resource)))
            throw new InvalidDataException("Mixed import and creation is unsupported.");
    }

    public bool Equals(ResourceImportIntent? other) => other is not null && Resources.SequenceEqual(other.Resources);
    public override bool Equals(object? obj) => obj is ResourceImportIntent other && Equals(other);
    public override int GetHashCode() => ResourceImportIntentCodec.Encode(this)!.GetHashCode(StringComparison.Ordinal);
}

public static class ResourceImportIntentCodec
{
    public static string? Encode(ResourceImportIntent? intent)
    {
        if (intent is null) return null;
        var json = JsonSerializer.Serialize(new ImportIntentDocument(2, intent.Resources.Select(x =>
            new ImportResourceDocument(x.Resource.Value, x.Input.Format, x.Input.Json, x.Credential?.Id,
                x.Credential?.Reference.SecretId.ToString(), x.Credential?.Reference.SecretVersionId.ToString())).ToArray()),
            ImportIntentJsonContext.Default.ImportIntentDocument);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576)
            throw new InvalidDataException("Import intent exceeds bound.");
        return json;
    }

    public static ResourceImportIntent? Decode(string? json)
    {
        if (json is null) return null;
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576) throw new InvalidDataException("Import intent exceeds bound.");
            var value = JsonSerializer.Deserialize(json, ImportIntentJsonContext.Default.ImportIntentDocument);
            if (value is null || value.Version != 2) throw new InvalidDataException("Unsupported import intent format.");
            return new(value.Resources.Select(x => new ResourceImportRequest(ManifestLocalId.Create(x.Resource), new(x.Format, x.Input),
                x.Credential is { } id && x.Secret is not null && x.Version is not null
                    ? new(id, new(SecretId.Parse(x.Secret), SecretVersionId.Parse(x.Version)))
                    : x.Credential is null && x.Secret is null && x.Version is null ? null
                    : throw new InvalidDataException("Incomplete protected input reference."))));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Invalid Resource import intent."); }
    }
}

internal sealed record ImportIntentDocument(int Version, ImportResourceDocument[] Resources);
internal sealed record ImportResourceDocument(string Resource, string Format, string Input, Guid? Credential, string? Secret, string? Version);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ImportIntentDocument))]
internal partial class ImportIntentJsonContext : JsonSerializerContext;
