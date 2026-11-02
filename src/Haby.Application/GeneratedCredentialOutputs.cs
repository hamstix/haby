using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Deployment and generation are always supplied by the containing accepted graph.</summary>
public sealed record OperationOutputRef(OperationId ProducerOperationId, string OutputContract, string OutputKey);
public sealed record GeneratedCredentialOutput(SecretRef Reference);
public sealed record GeneratedCredentialInput(ManagedResourceKey Owner, string SlotId, string GenerationProfile);

public static class GeneratedCredentialContracts
{
    public const string Action = "haby.secret.ensure-generated/v1";
    public const string InputFormat = "haby.generated-slot/v1";
    public const string OutputContract = "haby.generated-credential/v1";
    public const string Password = "password";
    public static bool IsProducer(DurableOperationNode node) => node.Action.ActionContract.Value == Action;
    public static OperationOutputRef PasswordFrom(OperationId producer) => new(producer, OutputContract, Password);

    public static void ValidateGraph(DurableOperationGraph graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (IsProducer(node) && (GeneratedCredentialInputCodec.Decode(node.Input) is not { } input ||
                input.Owner.EnvironmentRecordId != node.References.Environment || input.Owner.ResourceLocalId != node.ResourceId ||
                node.OutputReferences.Count != 0))
                throw new InvalidDataException("Invalid generated credential producer.");
            if (node.OutputReferences.Distinct().Count() != node.OutputReferences.Count)
                throw new InvalidDataException("Duplicate output selector.");
            foreach (var selector in node.OutputReferences)
            {
                var producer = graph.Nodes.SingleOrDefault(n => n.Id == selector.ProducerOperationId);
                if (producer is null || !node.Dependencies.Contains(producer.Id) || producer.References != node.References ||
                    string.IsNullOrEmpty(selector.OutputContract) || string.IsNullOrEmpty(selector.OutputKey) ||
                    ((IsProducer(producer) || selector.OutputContract == OutputContract) &&
                     (!IsProducer(producer) || selector.OutputContract != OutputContract || selector.OutputKey != Password ||
                      producer.ResourceId != node.ResourceId || producer.Action.Admission != node.Action.Admission)))
                    throw new InvalidDataException("Invalid operation output selector.");
            }
        }
    }
}

/// <summary>Explicit current version codecs. Neither format can carry material.</summary>
public static class GeneratedCredentialInputCodec
{
    public static DurableActionPayload Encode(GeneratedCredentialInput input) => new(GeneratedCredentialContracts.InputFormat,
        JsonSerializer.Serialize(new GeneratedSlotDocument(input.Owner.EnvironmentRecordId.ToString(),
            input.Owner.ApplicationRecordId.ToString(), input.Owner.ResourceLocalId.Value, input.SlotId, input.GenerationProfile),
            GeneratedOutputJsonContext.Default.GeneratedSlotDocument));

    public static GeneratedCredentialInput? Decode(DurableActionPayload payload)
    {
        if (payload.Format != GeneratedCredentialContracts.InputFormat) return null;
        try
        {
            var value = JsonSerializer.Deserialize(payload.Json, GeneratedOutputJsonContext.Default.GeneratedSlotDocument);
            return value is null || value.SlotId != GeneratedCredentialSlot.ApplicationPassword ||
                value.GenerationProfile != GeneratedCredentialSlot.RandomPasswordV1 ? null :
                new(new(EnvironmentRecordId.Parse(value.Environment), ApplicationRecordId.Parse(value.Application),
                    ManifestLocalId.Create(value.Resource)), value.SlotId, value.GenerationProfile);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException) { return null; }
    }
}

public static class GeneratedCredentialOutputCodec
{
    public static string Encode(GeneratedCredentialOutput output) => JsonSerializer.Serialize(
        new CredentialOutputDocument(output.Reference.SecretId.ToString(), output.Reference.SecretVersionId.ToString()),
        GeneratedOutputJsonContext.Default.CredentialOutputDocument);
    public static GeneratedCredentialOutput? Decode(string contract, string json)
    {
        if (contract != GeneratedCredentialContracts.OutputContract) return null;
        try
        {
            var value = JsonSerializer.Deserialize(json, GeneratedOutputJsonContext.Default.CredentialOutputDocument);
            return value is null ? null : new(new(SecretId.Parse(value.Secret), SecretVersionId.Parse(value.Version)));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException) { return null; }
    }
}

internal sealed record GeneratedSlotDocument(string Environment, string Application, string Resource, string SlotId, string GenerationProfile);
internal sealed record CredentialOutputDocument(string Secret, string Version);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GeneratedSlotDocument))]
[JsonSerializable(typeof(CredentialOutputDocument))]
internal partial class GeneratedOutputJsonContext : JsonSerializerContext;

/// <summary>Trusted internal catalog boundary. All identities are rederived from committed state.</summary>
public interface IGeneratedCredentialOperations
{
    ValueTask<GeneratedCredentialInput?> FindInputAsync(DurableOperationKey producer, CancellationToken cancellationToken);
    ValueTask<bool> CompleteAsync(DurableOperationKey producer, Guid attempt, GeneratedCredentialOutput output,
        DateTimeOffset at, CancellationToken cancellationToken);
    ValueTask<GeneratedCredentialOutput?> ConsumeAsync(DurableOperationKey consumer, OperationOutputRef selector,
        CancellationToken cancellationToken);
}

/// <summary>Exact local action; no Provider connection, external dispatch marker or independent generation.</summary>
public sealed class GeneratedCredentialAction(DurableActionAdmissionKey admission, IDurableOperationJournal journal,
    IGeneratedCredentialSlots slots, IGeneratedCredentialOperations outputs, TimeProvider clock) : IDurableActionHandler
{
    public DurableActionExecutionKey Key { get; } = new(admission, OperationContractId.Create(GeneratedCredentialContracts.Action));
    public bool Supports(DurableOperationNode node) => node.Action == Key && GeneratedCredentialInputCodec.Decode(node.Input) is not null;
    public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        var input = await outputs.FindInputAsync(operation.Key, cancellationToken);
        if (input is null || !Supports(operation.Node)) return new(DurableExecutionOutcome.InputUnavailable, operation);
        var begin = await journal.TryBeginAttemptAsync(operation.Key, clock.GetUtcNow(), cancellationToken);
        if (begin is not DurableBeginAttemptResult.Started started)
        {
            var saved = await journal.FindAsync(operation.Key, cancellationToken);
            return saved is null ? DurableExecutionResult.NotFound() : new(
                saved.State == DurableOperationState.Succeeded ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.RetryableFailure, saved);
        }
        var material = await slots.GetOrCreateAsync(new(input.Owner, input.SlotId), input.GenerationProfile, cancellationToken);
        if (material.Reference is not null && material.Outcome is GeneratedCredentialOutcome.Created or GeneratedCredentialOutcome.Existing &&
            await outputs.CompleteAsync(operation.Key, started.AttemptId, new(material.Reference), clock.GetUtcNow(), cancellationToken))
            return new(DurableExecutionOutcome.Succeeded, await journal.FindAsync(operation.Key, CancellationToken.None));
        // A failed publication leaves this safe local attempt open. The next explicit invocation
        // abandons it and reuses the retained slot; there is no external-effect uncertainty.
        return new(DurableExecutionOutcome.RetryableFailure, await journal.FindAsync(operation.Key, CancellationToken.None));
    }
}
