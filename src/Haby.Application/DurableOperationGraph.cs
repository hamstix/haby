using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Deterministic step identity, independent of Resource ownership.</summary>
public sealed record OperationId
{
    public string Value { get; }
    private OperationId(string value) => Value = value;
    public static OperationId Create(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Invalid operation identity.");
        return new(value);
    }
    public override string ToString() => Value;
}

public sealed record DurableActionPayload(string Format, string Json);
public sealed record DurableOperationNode(OperationId Id, ManifestLocalId ResourceId,
    DurableActionExecutionKey Action, DurableActionInputReferences References,
    DurableActionPayload Input, IReadOnlyList<OperationId> Dependencies)
{
    public IReadOnlyList<OperationOutputRef> OutputReferences { get; init; } = [];
}

/// <summary>Validated, defensively copied and canonically ordered immutable graph.</summary>
public sealed class DurableOperationGraph : IEquatable<DurableOperationGraph>
{
    public IReadOnlyList<DurableOperationNode> Nodes { get; }
    public IReadOnlyList<ResourceReuse> ReusedResources { get; }
    public IReadOnlyList<ResourceReuse> RetainedResources { get; }
    public ResourceReuseBaseline? Baseline { get; }
    public DurableOperationGraph(IEnumerable<DurableOperationNode> nodes,
        IEnumerable<ResourceReuse>? reusedResources = null, ResourceReuseBaseline? baseline = null, IEnumerable<ResourceReuse>? retainedResources = null)
    {
        var retained = (retainedResources ?? []).OrderBy(x => x.Resource.Value, StringComparer.Ordinal).ToArray();
        var active = (reusedResources ?? []).ToArray();
        var reuse = active.Concat(retained).OrderBy(x => x.Resource.Value, StringComparer.Ordinal).ToArray();
        if (reuse.Length > 256 || reuse.Select(x => x.Resource).Distinct().Count() != reuse.Length ||
            reuse.Select(x => x.ManagementId).Distinct().Count() != reuse.Length ||
            reuse.Any(x => x.ManagementId == Guid.Empty || x.SourceGeneration <= 0 || x.SourceGeneration > baseline?.Generation ||
                x.EvidenceFingerprint is not { Length: 64 } || x.EvidenceFingerprint.Any(c => !char.IsAsciiHexDigit(c))) ||
            (reuse.Length > 0) != (baseline is not null) || baseline is { Generation: <= 0 })
            throw new InvalidDataException("Invalid reuse requirements.");
        if (retained.Any(x => x.Reintroduced)) throw new InvalidDataException("Invalid retained membership.");
        ReusedResources = Array.AsReadOnly(active.OrderBy(x => x.Resource.Value, StringComparer.Ordinal).ToArray());
        RetainedResources = Array.AsReadOnly(retained);
        Baseline = baseline;
        var ordered = nodes.OrderBy(n => n.Id.Value, StringComparer.Ordinal).Select(n => n with
        {
            Dependencies = Array.AsReadOnly(n.Dependencies.OrderBy(d => d.Value, StringComparer.Ordinal).ToArray()),
            OutputReferences = Array.AsReadOnly(n.OutputReferences.OrderBy(r => r.ProducerOperationId.Value, StringComparer.Ordinal)
              .ThenBy(r => r.OutputContract, StringComparer.Ordinal).ThenBy(r => r.OutputKey, StringComparer.Ordinal).ToArray())
        }).ToArray();
        if ((ordered.Length == 0 && reuse.Length == 0) || ordered.Length > 256 ||
            ordered.Any(n => reuse.Any(r => r.Resource == n.ResourceId)) ||
            ordered.Select(n => n.Id).Distinct().Count() != ordered.Length)
            throw new InvalidDataException("Invalid graph identity set.");
        var ids = ordered.Select(n => n.Id).ToHashSet();
        foreach (var node in ordered)
        {
            if (node.Dependencies.Distinct().Count() != node.Dependencies.Count ||
                node.Dependencies.Any(d => !ids.Contains(d)) ||
                node.OutputReferences.Distinct().Count() != node.OutputReferences.Count ||
                node.OutputReferences.Any(o => !node.Dependencies.Contains(o.ProducerOperationId) ||
                    string.IsNullOrWhiteSpace(o.OutputContract) || string.IsNullOrWhiteSpace(o.OutputKey)) || node.ResourceId is null ||
                node.Action?.ActionContract is null || node.Action.Admission?.ResourceContract is null || node.Action.Admission.ProviderKind is null ||
                node.Action.Admission.ConfigurationContract is null || node.Action.Admission.ImplementationId is null ||
                node.References is null || node.Input is null || string.IsNullOrEmpty(node.Input.Format) ||
                string.IsNullOrEmpty(node.Input.Json)) throw new InvalidDataException("Invalid graph node.");
        }
        var remaining = ordered.ToList();
        var visited = new HashSet<OperationId>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(n => n.Dependencies.All(visited.Contains)).ToArray();
            if (ready.Length == 0) throw new InvalidDataException("Cyclic operation graph.");
            foreach (var node in ready) { visited.Add(node.Id); remaining.Remove(node); }
        }
        Nodes = Array.AsReadOnly(ordered);
        GeneratedCredentialContracts.ValidateGraph(this);
    }

    public bool Equals(DurableOperationGraph? other) => other is not null &&
        DurableOperationGraphCodec.Encode(this) == DurableOperationGraphCodec.Encode(other);
    public override bool Equals(object? obj) => obj is DurableOperationGraph other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(DurableOperationGraphCodec.Encode(this));

    public void ValidateReferences(DurablePlanningInputs inputs)
    {
        foreach (var node in Nodes)
        {
            if (GeneratedCredentialContracts.IsProducer(node) &&
                GeneratedCredentialInputCodec.Decode(node.Input)?.Owner != new ManagedResourceKey(inputs.Environment, inputs.Revision.ApplicationRecordId, node.ResourceId))
                throw new InvalidDataException("Inconsistent generated credential owner.");
            if (node.References != new DurableActionInputReferences(inputs.Environment, inputs.ProviderInstance,
                    inputs.ConfigurationRevision, inputs.ImplementationId) ||
                node.Action.Admission.ImplementationId.Value != inputs.ImplementationId)
                throw new InvalidDataException("Inconsistent graph references.");
        }
    }
}

/// <summary>One current, explicitly versioned graph format. No legacy readers.</summary>
public static class DurableOperationGraphCodec
{
    public static string Encode(DurableOperationGraph graph) => JsonSerializer.Serialize(new GraphDocument(2,
        graph.Nodes.Select(n => new NodeDocument(n.Id.Value, n.ResourceId.Value,
            n.Action.Admission.ResourceContract.Value, n.Action.Admission.ProviderKind.Value,
            n.Action.Admission.ConfigurationContract.Value, n.Action.Admission.ImplementationId.Value,
            n.Action.ActionContract.Value, n.References.Environment.ToString(), n.References.ProviderInstance.ToString(),
            n.References.ConfigurationRevision.Value, n.Input.Format, n.Input.Json,
            n.Dependencies.Select(d => d.Value).ToArray(), n.OutputReferences.Select(r =>
                new OutputRefDocument(r.ProducerOperationId.Value, r.OutputContract, r.OutputKey)).ToArray())).ToArray(),
        graph.ReusedResources.Count == 0 ? null : graph.ReusedResources.Select(r => new ReuseDocument(r.Resource.Value, r.ManagementId, r.SourceGeneration, r.EvidenceFingerprint, r.Reintroduced)).ToArray(),
        graph.Baseline is { } b ? new BaselineDocument(b.Deployment.ToString(), b.Generation, b.Fingerprint) : null,
        graph.RetainedResources.Count == 0 ? null : graph.RetainedResources.Select(r => new ReuseDocument(r.Resource.Value, r.ManagementId, r.SourceGeneration, r.EvidenceFingerprint, false)).ToArray()), GraphJsonContext.Default.GraphDocument);

    public static DurableOperationGraph Decode(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize(json, GraphJsonContext.Default.GraphDocument);
            if (document is null || document.Version != 2 || document.Nodes is null)
                throw new InvalidDataException("Unsupported operation graph format.");
            return new(document.Nodes.Select(n => new DurableOperationNode(OperationId.Create(n.Id),
                ManifestLocalId.Create(n.Resource), new(new(ResourceTypeId.Create(n.ResourceContract),
                    ProviderKindId.Create(n.Kind), ProviderConfigurationContractId.Create(n.Configuration),
                    ProviderImplementationId.Create(n.Implementation)), OperationContractId.Create(n.Action)),
                new(EnvironmentRecordId.Parse(n.Environment), ProviderInstanceId.Parse(n.Provider),
                    ProviderConfigurationRevisionNumber.Create(n.Revision), n.Implementation),
                new(n.Format, n.Input), n.Dependencies.Select(OperationId.Create).ToArray())
            { OutputReferences = n.Outputs.Select(r => new OperationOutputRef(OperationId.Create(r.Producer), r.Contract, r.Key)).ToArray() }),
                (document.Reuse ?? []).Select(r => new ResourceReuse(ManifestLocalId.Create(r.Resource), r.Management, r.Generation, r.Evidence) { Reintroduced = r.Reintroduced }),
                document.Baseline is { } b ? new(ApplicationDeploymentId.Parse(b.Deployment), b.Generation, b.Fingerprint) : null,
                (document.Retained ?? []).Select(r => new ResourceReuse(ManifestLocalId.Create(r.Resource), r.Management, r.Generation, r.Evidence) { Reintroduced = r.Reintroduced }));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException or FormatException)
        { throw new InvalidDataException("Invalid operation graph."); }
    }

    public static string Fingerprint(DurablePlanningInputs inputs, DurableOperationGraph graph)
    {
        graph.ValidateReferences(inputs);
        if (inputs.Configuration is { } frozen && frozen.Revision != inputs.Revision) throw new InvalidDataException("Configuration revision mismatch.");
        var text = string.Join('\n', "haby-operation-plan/v2", inputs.Environment.ToString(), inputs.EnvironmentKey.Value,
            inputs.Revision.ApplicationRecordId.ToString(), inputs.Revision.RevisionNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inputs.Alias.Value, inputs.ProviderInstance.ToString(),
            inputs.ConfigurationRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), inputs.ImplementationId, Encode(graph),
            inputs.Configuration is { } configuration ? ConfigurationPlanningSnapshotCodec.Encode(configuration) : "unprepared");
        return "v2:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

internal sealed record GraphDocument(int Version, NodeDocument[] Nodes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReuseDocument[]? Reuse,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BaselineDocument? Baseline,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReuseDocument[]? Retained = null);
internal sealed record ReuseDocument(string Resource, Guid Management, long Generation, string Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Reintroduced = false);
internal sealed record BaselineDocument(string Deployment, long Generation, string Fingerprint);
internal sealed record NodeDocument(string Id, string Resource, string ResourceContract, string Kind,
    string Configuration, string Implementation, string Action, string Environment, string Provider,
    long Revision, string Format, string Input, string[] Dependencies, OutputRefDocument[] Outputs);
internal sealed record OutputRefDocument(string Producer, string Contract, string Key);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GraphDocument))]
internal partial class GraphJsonContext : JsonSerializerContext;
