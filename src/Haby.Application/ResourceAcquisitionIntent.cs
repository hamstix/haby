using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Application;

/// <summary>One exact Resource's acquisition. Payloads are closed adapter contracts, never material.</summary>
public sealed record ResourceAcquisitionRequest(ManifestLocalId Resource, ResourceAcquisitionMode Mode,
    DurableActionPayload? Input = null, ProtectedCredentialReference? Credential = null)
{
    public DurableActionPayload? SharingRequest { get; init; }
}

public sealed class ResourceAcquisitionIntent : IEquatable<ResourceAcquisitionIntent>
{
    public IReadOnlyList<ResourceAcquisitionRequest> Resources { get; }
    public ResourceAcquisitionIntent(IEnumerable<ResourceAcquisitionRequest> resources)
    {
        var values = resources.ToArray();
        if (values.Length > 256 || values.Any(x => x?.Resource is null || !Enum.IsDefined(x.Mode) ||
                x.Mode == ResourceAcquisitionMode.Provision && (x.Input is not null || x.Credential is not null) ||
                x.Mode != ResourceAcquisitionMode.Provision && (x.Input is null || x.Input.Format is not { Length: > 0 and <= 128 } || x.Input.Json is not { Length: > 0 and <= 65536 }) ||
                x.Mode != ResourceAcquisitionMode.ImportUseAndRetain && x.Credential is not null ||
                x.SharingRequest is not null && (x.Mode != ResourceAcquisitionMode.Reference || !ReferenceSharingAuthorizationCodec.IsBounded(x.SharingRequest)) ||
                x.Credential is { } credential && (credential.Id == Guid.Empty || credential.Reference is null)) ||
            values.Select(x => x.Resource).Distinct().Count() != values.Length)
            throw new ArgumentException("Invalid Resource acquisition set.");
        Resources = Array.AsReadOnly(values.OrderBy(x => x.Resource.Value, StringComparer.Ordinal).ToArray());
    }
    public ResourceAcquisitionRequest Resource(ManifestLocalId resource) => Resources.Single(x => x.Resource == resource);
    public void Validate(NormalizedManifestSpec spec)
    {
        if (!spec.Resources.Keys.ToHashSet().SetEquals(Resources.Select(x => x.Resource)))
            throw new InvalidDataException("Acquisition Resource coverage differs from declaration.");
    }
    public static ResourceAcquisitionIntent Provision(IEnumerable<ManifestLocalId> resources) => new(resources.Select(x => new ResourceAcquisitionRequest(x, ResourceAcquisitionMode.Provision)));
    public static ResourceAcquisitionIntent Import(ResourceImportIntent intent) => new(intent.Resources.Select(x => new ResourceAcquisitionRequest(x.Resource, ResourceAcquisitionMode.ImportUseAndRetain, x.Input, x.Credential)));
    public bool Equals(ResourceAcquisitionIntent? other) => other is not null && Resources.SequenceEqual(other.Resources);
    public override bool Equals(object? obj) => obj is ResourceAcquisitionIntent other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ResourceAcquisitionIntentCodec.Encode(this));
    public static ResourceOrigin Origin(ResourceAcquisitionMode mode) => mode switch
    { ResourceAcquisitionMode.Provision => ResourceOrigin.Provisioned, ResourceAcquisitionMode.ImportUseAndRetain => ResourceOrigin.Imported, ResourceAcquisitionMode.Reference => ResourceOrigin.Referenced, _ => throw new InvalidDataException() };
    public static ResourceLifecycleAuthority Authority(ResourceAcquisitionMode mode) => mode switch
    { ResourceAcquisitionMode.Provision => ResourceLifecycleAuthority.Provisioned, ResourceAcquisitionMode.ImportUseAndRetain => ResourceLifecycleAuthority.UseAndRetain, ResourceAcquisitionMode.Reference => ResourceLifecycleAuthority.ConsumptionOnly, _ => throw new InvalidDataException() };
}

public static class ResourceAcquisitionIntentCodec
{
    public static string Encode(ResourceAcquisitionIntent intent)
    {
        var json = JsonSerializer.Serialize(new AcquisitionDocument(2, intent.Resources.Select(x => new AcquisitionMember(x.Resource.Value, (int)x.Mode,
            x.Input?.Format, x.Input?.Json, x.Credential?.Id, x.Credential?.Reference.SecretId.ToString(), x.Credential?.Reference.SecretVersionId.ToString(), x.SharingRequest)).ToArray()), AcquisitionJsonContext.Default.AcquisitionDocument);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576) throw new InvalidDataException("Acquisition set exceeds bound.");
        return json;
    }
    public static ResourceAcquisitionIntent Decode(string json)
    {
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576) throw new InvalidDataException("Acquisition set exceeds bound.");
            var document = JsonSerializer.Deserialize(json, AcquisitionJsonContext.Default.AcquisitionDocument);
            if (document?.Version != 2) throw new InvalidDataException("Unsupported acquisition format.");
            return new(document.Resources.Select(x => new ResourceAcquisitionRequest(ManifestLocalId.Create(x.Resource), (ResourceAcquisitionMode)x.Mode,
                x.Format is null && x.Input is null ? null : new(x.Format!, x.Input!),
                x.Credential is null && x.Secret is null && x.Version is null ? null :
                new(x.Credential ?? throw new InvalidDataException(), new(SecretId.Parse(x.Secret!), SecretVersionId.Parse(x.Version!))))
            { SharingRequest = x.SharingRequest }));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException or InvalidOperationException)
        { throw new InvalidDataException("Invalid acquisition set.", e); }
    }
}
internal sealed record AcquisitionDocument(int Version, AcquisitionMember[] Resources);
internal sealed record AcquisitionMember(string Resource, int Mode, string? Format, string? Input, Guid? Credential, string? Secret, string? Version, DurableActionPayload? SharingRequest);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(AcquisitionDocument))]
internal partial class AcquisitionJsonContext : JsonSerializerContext;
