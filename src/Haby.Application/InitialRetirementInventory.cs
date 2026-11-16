using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.Application;

// This closed initial-shape authority has no nonempty retirement executor.
internal sealed class InitialRetirementInventory
{
    private readonly string json;
    internal const int MaximumHistory = 256;
    internal const int MaximumBytes = 524288;
    internal InitialRetirementInventory(InitialRetirementDocument document)
    {
        if (document is null || document.History is null || document.Targets is null ||
            document.History.Any(x => x is null) || document.Targets.Any(x => x is null || x.Documents is null) || document.Version != 1 || document.Shape is not ("Initial" or "ResourceBaseline") ||
            document.History is not { Length: <= MaximumHistory } || document.Targets is not { Length: > 0 and <= 512 } ||
            document.ObsoleteObjects is null || document.ObsoletePublications is null ||
            document.ObsoleteObjects.Length > 512 || document.ObsoletePublications.Length > 512 ||
            document.Shape == "Initial" && (document.PriorApplied is not null || document.History.Length != 0) ||
            document.Shape == "ResourceBaseline" && (document.PriorApplied is not > 0 || document.History.Length != document.PriorApplied) ||
            document.History.Where((x, i) => x.Generation != i + 1 || x.Plan == Guid.Empty || x.ChangeSet == Guid.Empty ||
                x.Revision <= 0 || x.Checkpoint == Guid.Empty || !Hash(x.CheckpointHash) || x.Fingerprint is not { Length: 67 }).Any() ||
            !document.Targets.Select(x => x.Subject).SequenceEqual(document.Targets.Select(x => x.Subject).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) ||
            document.Targets.Sum(x => (long)x.Documents.Length) > 512 || document.Targets.Any(x =>
                x.Subject is not { Length: > 0 and <= 256 } || x.Contract is not { Length: > 0 and <= 128 } ||
                x.Provider == Guid.Empty || x.Configuration <= 0 || !Hash(x.PlacementHash) || x.Documents.Length > 64 ||
                x.RuntimeIdentity != (x.Documents.Length > 0) || !x.Documents.SequenceEqual(x.Documents.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))))
            throw new InvalidDataException("Unsupported initial retirement inventory.");
        json = JsonSerializer.Serialize(document, InitialRetirementMetadata.Default.InitialRetirementDocument);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Retirement inventory exceeds bound.");
    }
    internal InitialRetirementDocument Document => JsonSerializer.Deserialize(json, InitialRetirementMetadata.Default.InitialRetirementDocument)!;
    internal string Fingerprint => ResourcesReadyCheckpointCodec.Hash(json);
    internal static bool Hash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

internal sealed record InitialRetirementDocument(int Version, string Shape, long? PriorApplied,
    InitialRetirementHistory[] History, InitialRetirementTarget[] Targets, string[] ObsoleteObjects, string[] ObsoletePublications);
internal sealed record InitialRetirementHistory(long Generation, Guid Plan, Guid ChangeSet, long Revision,
    string Fingerprint, Guid Checkpoint, string CheckpointHash);
internal sealed record InitialRetirementTarget(string Subject, string Contract, Guid Provider, long Configuration,
    string PlacementHash, string[] Documents, bool RuntimeIdentity);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(InitialRetirementDocument))]
internal partial class InitialRetirementMetadata : JsonSerializerContext;
