using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;

namespace Haby.Persistence.PostgreSql;

internal enum RuntimePublicationOperation { PublishPermission, RevokePermission, RevokeBinding }
internal enum RuntimePublicationOutcome { Published, Revoked, Unchanged, PreconditionFailed, BindingRevoked }
internal sealed record RuntimePublicationOrigin(Guid Deployment, long Generation, Guid Plan, Guid ChangeSet,
    DateTimeOffset CommittedAt, string CommitActor, Guid Environment, Guid Application, Guid ManagedWorkload,
    string Component, string Workload, string Document, string Fingerprint, Guid Checkpoint,
    long IdentityGeneration, string IdentityOperation, int Version = 1)
{
    internal DurableChangeSetCommit Accepted => new(DeploymentChangeSetId.Parse(ChangeSet.ToString()), DeploymentPlanId.Parse(Plan.ToString()),
        ApplicationDeploymentId.Parse(Deployment.ToString()), DeploymentGeneration.Create(Generation), CommittedAt, CommitActor);
}
internal sealed record RuntimePublicationRequest(string Key, RuntimePublicationOperation Operation, Guid Binding,
    Guid? Revision, Guid? ExpectedToken, string Actor, RuntimePublicationOrigin? Origin = null, int Version = 1);
internal sealed record RuntimePermissionState(Guid Id, Guid Binding, Guid Revision, string Status, Guid Token,
    string CreatedBy, DateTimeOffset CreatedAt, string ChangedBy, DateTimeOffset ChangedAt, string LastRequest);
internal sealed record RuntimeBindingState(Guid Id, string Status, Guid Token, string? RevokedBy, DateTimeOffset? RevokedAt);
internal sealed record RuntimePublicationReceipt(Guid Id, RuntimePublicationRequest Request, RuntimePublicationOutcome Outcome,
    DateTimeOffset At, RuntimePermissionState? Before, RuntimePermissionState? After, RuntimeBindingState Binding, int Version = 1);
internal sealed record RuntimePublicationResult(RuntimePublicationReceipt Receipt, RuntimeBindingState CurrentBinding, RuntimePermissionState? CurrentPermission);
internal sealed record RuntimeMetadataAuthority(Guid Binding, Guid Permission, Guid Revision, Guid Environment,
    Guid Application, Guid Deployment, long Generation, string Component, string Workload, string Document);
internal sealed class RuntimePublicationBlockedException(RuntimePublicationRequest request) : Exception("Runtime binding is revoked.")
{
    internal RuntimePublicationRequest Request { get; } = request;
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(RuntimePublicationReceipt))]
[JsonSerializable(typeof(RuntimePublicationOrigin))]
internal partial class RuntimePublicationJsonContext : JsonSerializerContext;

internal static class RuntimePublicationCodec
{
    internal const int MaximumBytes = 16384;
    internal static string Encode(RuntimePublicationReceipt value) => Bound(JsonSerializer.Serialize(value, RuntimePublicationJsonContext.Default.RuntimePublicationReceipt));
    internal static string Encode(RuntimePublicationOrigin value) => Bound(JsonSerializer.Serialize(value, RuntimePublicationJsonContext.Default.RuntimePublicationOrigin));
    internal static RuntimePublicationReceipt Receipt(string payload)
    {
        var value = JsonSerializer.Deserialize(Bound(payload), RuntimePublicationJsonContext.Default.RuntimePublicationReceipt)
            ?? throw new InvalidDataException("Runtime publication receipt unavailable.");
        Validate(value.Request);
        if (value.Version != 1 || value.Id == Guid.Empty || !Enum.IsDefined(value.Outcome) || value.At.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid runtime publication receipt.");
        return value;
    }
    internal static RuntimePublicationOrigin Origin(string payload)
    {
        var value = JsonSerializer.Deserialize(Bound(payload), RuntimePublicationJsonContext.Default.RuntimePublicationOrigin)
            ?? throw new InvalidDataException("Runtime publication origin unavailable.");
        if (value.Version != 1) throw new InvalidDataException("Unsupported runtime publication origin.");
        return value;
    }
    private static string Bound(string value) => Encoding.UTF8.GetByteCount(value) is > 0 and <= MaximumBytes ? value :
        throw new InvalidDataException("Runtime publication payload exceeds its bound.");
    internal static void Validate(RuntimePublicationRequest value)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(value.Actor);
        if (value.Version != 1 || string.IsNullOrWhiteSpace(value.Key) || Encoding.UTF8.GetByteCount(value.Key) > 128 ||
            value.Binding == Guid.Empty || value.ExpectedToken == Guid.Empty || !Enum.IsDefined(value.Operation) ||
            (value.Operation == RuntimePublicationOperation.RevokeBinding ? value.Revision is not null || value.ExpectedToken is null : value.Revision is null || value.Revision == Guid.Empty) ||
            (value.Operation == RuntimePublicationOperation.PublishPermission ? value.Origin is not { Version: 1 } : value.Origin is not null))
            throw new InvalidDataException("Invalid runtime publication request.");
    }
}
