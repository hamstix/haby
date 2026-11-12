using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Original confirmed results, never copied effects or secret material.</summary>
public sealed record ResourceOutputReference(string Contract, string Key, string Fingerprint,
    Guid? SecretId = null, Guid? SecretVersionId = null);
public sealed class ResourceOperationResult(string operationId, Guid attemptId, long attemptNumber,
    Guid? recoveryDecisionId, IEnumerable<ResourceOutputReference> outputs)
{
    public string OperationId { get; } = operationId;
    public Guid AttemptId { get; } = attemptId;
    public long AttemptNumber { get; } = attemptNumber;
    public Guid? RecoveryDecisionId { get; } = recoveryDecisionId;
    public IReadOnlyList<ResourceOutputReference> Outputs { get; } = Array.AsReadOnly(outputs.ToArray());
}
public sealed class ResourceReadyResult(string resource, Guid managementId, long sourceGeneration,
    string sourceFingerprint, bool retained, bool reintroduced, IEnumerable<ResourceOperationResult> operations)
{
    public string Resource { get; } = resource;
    public Guid ManagementId { get; } = managementId;
    public long SourceGeneration { get; } = sourceGeneration;
    public string SourceFingerprint { get; } = sourceFingerprint;
    public bool Retained { get; } = retained;
    public bool Reintroduced { get; } = reintroduced;
    public IReadOnlyList<ResourceOperationResult> Operations { get; } = Array.AsReadOnly(operations.ToArray());
}

/// <summary>One immutable, exact-generation Resource proof. It asserts no later stage.</summary>
public sealed class ResourcesReadyCheckpoint(Guid id, Guid deploymentId, long generation,
    string inputFingerprint, DateTimeOffset confirmedAt, IEnumerable<ResourceReadyResult> resources)
{
    public const int FormatVersion = 1;
    public const string Stage = "ResourcesReady";
    public Guid Id { get; } = id;
    public Guid DeploymentId { get; } = deploymentId;
    public long Generation { get; } = generation;
    public string InputFingerprint { get; } = inputFingerprint;
    public DateTimeOffset ConfirmedAt { get; } = confirmedAt;
    public IReadOnlyList<ResourceReadyResult> Resources { get; } = Array.AsReadOnly(resources.ToArray());
}

/// <summary>Pure coverage policy; an empty proof does not admit a generation for execution.</summary>
public static class ResourceReadinessCoverage
{
    public static bool IsComplete(IEnumerable<string> declared, IEnumerable<string> operations, IEnumerable<string> reused)
    {
        var required = declared.ToArray();
        var created = operations.ToArray();
        var existing = reused.ToArray();
        return required.Length <= 256 && created.Length <= 256 && existing.Length <= 256 &&
            required.Distinct(StringComparer.Ordinal).Count() == required.Length &&
            existing.Distinct(StringComparer.Ordinal).Count() == existing.Length &&
            !created.Intersect(existing, StringComparer.Ordinal).Any() &&
            required.ToHashSet(StringComparer.Ordinal).SetEquals(created.Concat(existing));
    }
}

public static class ResourcesReadyCheckpointCodec
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Encode(ResourcesReadyCheckpoint checkpoint)
    {
        var value = Document(checkpoint);
        Validate(value);
        var json = JsonSerializer.Serialize(value, ResourceCheckpointJsonContext.Default.ResourceCheckpointDocument);
        if (Encoding.UTF8.GetByteCount(json) > 2_097_152) throw new InvalidDataException("Resource checkpoint exceeds its bound.");
        return json;
    }
    public static ResourcesReadyCheckpoint Decode(string json)
    {
        try
        {
            if (json.Length > 2_097_152) throw new InvalidDataException("Resource checkpoint exceeds its bound.");
            var value = JsonSerializer.Deserialize(json, ResourceCheckpointJsonContext.Default.ResourceCheckpointDocument)
                ?? throw new InvalidDataException("Missing Resource checkpoint.");
            Validate(value);
            return new(value.Id, value.Deployment, value.Generation, value.InputFingerprint, value.ConfirmedAt,
                value.Resources.Select(r => new ResourceReadyResult(r.Resource, r.Management, r.SourceGeneration,
                    r.SourceFingerprint, r.Retained, r.Reintroduced, r.Operations.Select(o => new ResourceOperationResult(
                        o.Operation, o.Attempt, o.AttemptNumber, o.RecoveryDecision, o.Outputs)))));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Invalid Resource checkpoint."); }
    }
    private static ResourceCheckpointDocument Document(ResourcesReadyCheckpoint value) => new(
        ResourcesReadyCheckpoint.FormatVersion, ResourcesReadyCheckpoint.Stage, value.Id, value.DeploymentId,
        value.Generation, value.InputFingerprint, value.ConfirmedAt, value.Resources.Select(r => new ResourceResultDocument(
            r.Resource, r.ManagementId, r.SourceGeneration, r.SourceFingerprint, r.Retained, r.Reintroduced,
            r.Operations.Select(o => new OperationResultDocument(o.OperationId, o.AttemptId, o.AttemptNumber,
                o.RecoveryDecisionId, o.Outputs.ToArray())).ToArray())).ToArray());
    private static bool Fingerprint(string value) => value is { Length: 67 } && value.StartsWith("v4:", StringComparison.Ordinal) &&
        value.AsSpan(3).ContainsAnyExcept("0123456789ABCDEF".AsSpan()) == false;
    private static void Validate(ResourceCheckpointDocument value)
    {
        if (value.Version != ResourcesReadyCheckpoint.FormatVersion || value.Stage != ResourcesReadyCheckpoint.Stage ||
            value.Id == Guid.Empty || value.Deployment == Guid.Empty || value.Generation <= 0 ||
            !Fingerprint(value.InputFingerprint) || value.ConfirmedAt.Offset != TimeSpan.Zero || value.Resources is null ||
            value.Resources.Length > 512 || value.Resources.Select(r => r.Resource).Distinct(StringComparer.Ordinal).Count() != value.Resources.Length ||
            value.Resources.Select(r => r.Management).Distinct().Count() != value.Resources.Length)
            throw new InvalidDataException("Unsupported or inconsistent Resource checkpoint.");
        foreach (var resource in value.Resources)
        {
            _ = ManifestLocalId.Create(resource.Resource);
            if (resource.Management == Guid.Empty || resource.SourceGeneration <= 0 || resource.SourceGeneration > value.Generation ||
                !Fingerprint(resource.SourceFingerprint) || resource.Retained && resource.Reintroduced ||
                resource.Operations is not { Length: > 0 and <= 256 } ||
                resource.Operations.Select(o => o.Operation).Distinct(StringComparer.Ordinal).Count() != resource.Operations.Length)
                throw new InvalidDataException("Invalid Resource checkpoint result.");
            foreach (var operation in resource.Operations)
            {
                _ = OperationId.Create(operation.Operation);
                if (operation.Attempt == Guid.Empty || operation.AttemptNumber <= 0 || operation.RecoveryDecision == Guid.Empty ||
                    operation.Outputs is null || operation.Outputs.Length > 256 ||
                    operation.Outputs.Select(o => (o.Contract, o.Key)).Distinct().Count() != operation.Outputs.Length)
                    throw new InvalidDataException("Invalid Resource checkpoint operation.");
                foreach (var output in operation.Outputs)
                    if (output.Contract is not { Length: > 0 and <= 128 } || output.Key is not { Length: > 0 and <= 128 } ||
                        output.Fingerprint is not { Length: 64 } || output.Fingerprint.Any(c => !char.IsAsciiHexDigit(c)) ||
                        output.SecretId.HasValue != output.SecretVersionId.HasValue || output.SecretId == Guid.Empty || output.SecretVersionId == Guid.Empty)
                        throw new InvalidDataException("Invalid Resource checkpoint output reference.");
            }
        }
    }
}

internal sealed record ResourceCheckpointDocument(int Version, string Stage, Guid Id, Guid Deployment, long Generation,
    string InputFingerprint, DateTimeOffset ConfirmedAt, ResourceResultDocument[] Resources);
internal sealed record ResourceResultDocument(string Resource, Guid Management, long SourceGeneration,
    string SourceFingerprint, bool Retained, bool Reintroduced, OperationResultDocument[] Operations);
internal sealed record OperationResultDocument(string Operation, Guid Attempt, long AttemptNumber,
    Guid? RecoveryDecision, ResourceOutputReference[] Outputs);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ResourceCheckpointDocument))]
internal partial class ResourceCheckpointJsonContext : JsonSerializerContext;
