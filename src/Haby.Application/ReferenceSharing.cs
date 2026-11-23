using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.Application;

/// <summary>Adapter-authorized, non-secret decision frozen with the exact operation and request.</summary>
public sealed record ReferenceSharingAuthorization(string Contract, DurableActionPayload Request, DurableActionPayload Decision);

/// <summary>Each peer supplies its own immutable association evidence and pinned accepted context.</summary>
public sealed record ReferenceSharingParticipant(AcceptedOperation Accepted, ReferenceSharingAuthorization Authorization,
    ProviderResourceReservation Identity, DurableActionPayload Observation);

/// <summary>Explicit exact-producer capability. Methods are pure, bounded metadata checks: no I/O,
/// material resolution or side effects. Every operation of a Resource must authorize the same
/// Resource-level decision. Contract IDs include a version and never change interpretation.</summary>
public interface IResourceReferenceSharingContract : IResourceReferenceOutputContract
{
    string SharingContract { get; }
    DurableActionPayload? AuthorizeSharing(DurableOperationNode operation, DurableActionPayload request);
    bool AreCompatible(ReferenceSharingParticipant own, ReferenceSharingParticipant peer);
}

public static class ReferenceSharingAuthorizationCodec
{
    public static bool IsBounded(DurableActionPayload payload) => payload.Format is { Length: > 0 and <= 128 } &&
        payload.Json is { Length: > 0 } && System.Text.Encoding.UTF8.GetByteCount(payload.Json) <= 65536;

    public static bool Supports(DurableOperationNode node, IExternalOperationOutputContract contract)
    {
        if (node.Sharing is null) return true;
        try
        {
            return node.Action.Admission.Mode == ResourceAcquisitionMode.Reference && contract.Producer == node.Action &&
                contract is IResourceReferenceSharingContract sharing && node.Sharing.Contract == sharing.SharingContract &&
                node.Sharing.Contract is { Length: > 0 and <= 128 } && IsBounded(node.Sharing.Request) && IsBounded(node.Sharing.Decision) &&
                sharing.AuthorizeSharing(node with { Sharing = null }, node.Sharing.Request) == node.Sharing.Decision;
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or JsonException) { return false; }
    }

    public static string Encode(ReferenceSharingAuthorization authorization)
    {
        if (authorization.Contract is not { Length: > 0 and <= 128 } || !IsBounded(authorization.Request) || !IsBounded(authorization.Decision))
            throw new InvalidDataException("Invalid Reference sharing authorization.");
        var json = JsonSerializer.Serialize(new ReferenceSharingDocument(1, authorization), ReferenceSharingJsonContext.Default.ReferenceSharingDocument);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576) throw new InvalidDataException("Reference sharing authorization exceeds bound.");
        return json;
    }

    public static ReferenceSharingAuthorization Decode(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576) throw new InvalidDataException("Reference sharing authorization exceeds bound.");
        try
        {
            var document = JsonSerializer.Deserialize(json, ReferenceSharingJsonContext.Default.ReferenceSharingDocument);
            if (document?.Version != 1 || document.Authorization is null) throw new InvalidDataException("Invalid Reference sharing authorization.");
            _ = Encode(document.Authorization);
            return document.Authorization;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid Reference sharing authorization."); }
    }
}

internal sealed record ReferenceSharingDocument(int Version, ReferenceSharingAuthorization Authorization);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ReferenceSharingDocument))]
internal partial class ReferenceSharingJsonContext : JsonSerializerContext;
