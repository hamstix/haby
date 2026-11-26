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
    public bool RequiresExternalOutput { get; init; }
    public ExternalOperationOutputIdentity? RequiredOutput { get; init; }
    public ReferenceSharingAuthorization? Sharing { get; init; }
}

/// <summary>Validated, defensively copied and canonically ordered immutable graph.</summary>
public sealed class DurableOperationGraph : IEquatable<DurableOperationGraph>
{
    public IReadOnlyList<DurableOperationNode> Nodes { get; }
    public IReadOnlyList<ResourceReuse> ReusedResources { get; }
    public IReadOnlyList<ResourceReuse> RetainedResources { get; }
    public ResourceReuseBaseline? Baseline { get; }
    private ResourceImportIntent? import;
    private ResourceAcquisitionIntent acquisitions = null!;
    public ResourceAcquisitionIntent Acquisitions
    {
        get => acquisitions;
        init
        {
            acquisitions = value;
            if (Nodes.Any(x => x.Action.Admission.Mode == ResourceAcquisitionMode.ImportUseAndRetain && value.Resources.SingleOrDefault(r => r.Resource == x.ResourceId)?.ImportRequestId is not null))
            {
                var groups = Nodes.Where(x => x.Action.Admission.Mode == ResourceAcquisitionMode.ImportUseAndRetain)
                    .GroupBy(x => value.Resource(x.ResourceId).ImportRequestId)
                    .Select(g => new ResourceObservationGroup(g.Key is { } request ? "import." + request.ToString("N") : "import",
                        ResourceAcquisitionMode.ImportUseAndRetain, g.Select(x => x.Id).ToArray()));
                ObservationGroups = groups.Concat(Nodes.Where(x => x.Action.Admission.Mode == ResourceAcquisitionMode.Reference).GroupBy(x => x.ResourceId)
                    .Select(g => new ResourceObservationGroup("reference." + g.Key.Value, ResourceAcquisitionMode.Reference, g.Select(x => x.Id).ToArray()))).ToArray();
            }
        }
    }
    public ResourceImportIntent? Import
    {
        get => import;
        init { import = value; if (value is not null) Acquisitions = ResourceAcquisitionIntent.Import(value); }
    }
    private IReadOnlyList<ResourceObservationGroup> observationGroups = [];
    public IReadOnlyList<ResourceObservationGroup> ObservationGroups
    {
        get => observationGroups;
        init => observationGroups = Array.AsReadOnly(value.Select(x => new ResourceObservationGroup(x.Id, x.Mode,
            Array.AsReadOnly(x.Operations.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray()))).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
    }
    public void ValidateObservationGroups()
    {
        if (Nodes.Any(n => Acquisitions.Resources.SingleOrDefault(x => x.Resource == n.ResourceId)?.Mode != n.Action.Admission.Mode))
            throw new InvalidDataException("Graph acquisition mode differs from its Resource.");
        var observational = Nodes.Where(x => x.Action.Admission.Mode != ResourceAcquisitionMode.Provision).ToArray();
        var members = ObservationGroups.SelectMany(x => x.Operations).ToArray();
        if (ObservationGroups.Count > 256 || ObservationGroups.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != ObservationGroups.Count ||
            members.Length != members.Distinct().Count() || !members.ToHashSet().SetEquals(observational.Select(x => x.Id)))
            throw new InvalidDataException("Observation groups must completely partition observational operations.");
        foreach (var group in ObservationGroups)
        {
            if (group.Id is not { Length: > 0 and <= 128 } || group.Operations.Count == 0 || group.Mode is not (ResourceAcquisitionMode.ImportUseAndRetain or ResourceAcquisitionMode.Reference))
                throw new InvalidDataException("Invalid observation group.");
            if (group.Mode == ResourceAcquisitionMode.ImportUseAndRetain)
            {
                var requests = group.Operations.Select(id => Acquisitions.Resource(Nodes.Single(n => n.Id == id).ResourceId).ImportRequestId).Distinct().ToArray();
                if (requests.Length != 1 || !group.Operations.ToHashSet().SetEquals(Nodes.Where(n => n.Action.Admission.Mode == ResourceAcquisitionMode.ImportUseAndRetain &&
                    Acquisitions.Resource(n.ResourceId).ImportRequestId == requests[0]).Select(n => n.Id)))
                    throw new InvalidDataException("Import group does not match its exact protected request.");
            }
            foreach (var id in group.Operations)
            {
                var node = Nodes.Single(x => x.Id == id);
                if (node.Action.Admission.Mode != group.Mode || !node.RequiresExternalOutput || GeneratedCredentialContracts.IsProducer(node) ||
                    node.Dependencies.Any(group.Operations.Contains) || node.OutputReferences.Any(x => group.Operations.Contains(x.ProducerOperationId)))
                    throw new InvalidDataException("Unsupported observation group membership or dependency.");
            }
        }
    }
    internal InitialRetirementInventory? Retirement { get; private init; }
    internal WorkloadUpdateInventory? WorkloadUpdate { get; private init; }
    internal DurableOperationGraph WithWorkloadUpdate(WorkloadUpdateInventory inventory) =>
        new(Nodes, ReusedResources, Baseline, RetainedResources) { Import = Import, Acquisitions = Acquisitions, ObservationGroups = ObservationGroups, WorkloadUpdate = inventory };
    internal DurableOperationGraph WithRetirement(InitialRetirementInventory inventory) =>
        new(Nodes, ReusedResources, Baseline, RetainedResources) { Import = Import, Acquisitions = Acquisitions, ObservationGroups = ObservationGroups, Retirement = inventory };
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
            reuse.Length > 0 && baseline is null || baseline is { Generation: <= 0 })
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
        // An empty Resource-stage graph is structural only. Its accepted full
        // declaration and internal generation authority are checked by admission.
        if (ordered.Length > 256 ||
            ordered.Any(n => reuse.Any(r => r.Resource == n.ResourceId)) ||
            ordered.Select(n => n.Id).Distinct().Count() != ordered.Length)
            throw new InvalidDataException("Invalid graph identity set.");
        var ids = ordered.Select(n => n.Id).ToHashSet();
        foreach (var node in ordered)
        {
            if (node.RequiresExternalOutput != (node.RequiredOutput is not null) ||
                node.RequiredOutput is { } required && (required.Contract is not { Length: > 0 and <= 128 } || required.Key is not { Length: > 0 and <= 128 }))
                throw new InvalidDataException("Invalid accepted output obligation.");
            if (node.Dependencies.Distinct().Count() != node.Dependencies.Count ||
                node.Dependencies.Any(d => !ids.Contains(d)) ||
                node.OutputReferences.Distinct().Count() != node.OutputReferences.Count ||
                node.OutputReferences.Any(o => !node.Dependencies.Contains(o.ProducerOperationId) ||
                    string.IsNullOrWhiteSpace(o.OutputContract) || string.IsNullOrWhiteSpace(o.OutputKey)) || node.ResourceId is null ||
                node.Action?.ActionContract is null || node.Action.Admission?.ResourceContract is null || node.Action.Admission.ProviderKind is null ||
                node.Action.Admission.ConfigurationContract is null || node.Action.Admission.ImplementationId is null || !Enum.IsDefined(node.Action.Admission.Mode) ||
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
        Acquisitions = new ResourceAcquisitionIntent(ordered.GroupBy(x => x.ResourceId).Select(g => new ResourceAcquisitionRequest(g.Key,
            g.First().Action.Admission.Mode, g.First().Action.Admission.Mode == ResourceAcquisitionMode.Provision ? null : g.First().Input))
            .Concat(active.Select(x => new ResourceAcquisitionRequest(x.Resource, ResourceAcquisitionMode.Provision))));
        var groups = new List<ResourceObservationGroup>();
        var imported = ordered.Where(x => x.Action.Admission.Mode == ResourceAcquisitionMode.ImportUseAndRetain).Select(x => x.Id).ToArray();
        if (imported.Length > 0) groups.Add(new("import", ResourceAcquisitionMode.ImportUseAndRetain, Array.AsReadOnly(imported)));
        groups.AddRange(ordered.Where(x => x.Action.Admission.Mode == ResourceAcquisitionMode.Reference).GroupBy(x => x.ResourceId)
            .Select(g => new ResourceObservationGroup("reference." + g.Key.Value, ResourceAcquisitionMode.Reference, Array.AsReadOnly(g.Select(x => x.Id).ToArray()))));
        ObservationGroups = groups.AsReadOnly();
        GeneratedCredentialContracts.ValidateGraph(this);
    }

    public bool Equals(DurableOperationGraph? other) => other is not null &&
        DurableOperationGraphCodec.Encode(this) == DurableOperationGraphCodec.Encode(other);
    public override bool Equals(object? obj) => obj is DurableOperationGraph other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(DurableOperationGraphCodec.Encode(this));

    public void ValidateReferences(DurablePlanningInputs inputs)
    {
        inputs.Validate();
        ValidateObservationGroups();
        if (Nodes.GroupBy(x => x.ResourceId).Any(g => g.Select(x => x.Sharing).Distinct().Count() != 1))
            throw new InvalidDataException("A Resource requires one exact sharing authorization.");
        if (!Acquisitions.Equals(inputs.Acquisitions)) throw new InvalidDataException("Acquisition set differs from accepted graph.");
        if (!Equals(Import, inputs.Import) || Import is not null && (Baseline is not null || ReusedResources.Count != 0 || RetainedResources.Count != 0))
            throw new InvalidDataException("Import intent does not match the accepted graph.");
        var active = Nodes.Select(n => n.ResourceId).Concat(ReusedResources.Select(r => r.Resource)).ToHashSet();
        if (!active.SetEquals(inputs.Providers.Declaration.Spec.Resources.Keys))
            throw new InvalidDataException("Graph Resource coverage does not match the exact input set.");
        foreach (var node in Nodes)
        {
            if (node.Sharing?.Request != inputs.Acquisitions.Resource(node.ResourceId).SharingRequest ||
                node.Sharing is not null && node.Action.Admission.Mode != ResourceAcquisitionMode.Reference)
                throw new InvalidDataException("Sharing authorization differs from accepted acquisition.");
            if (node.Sharing is not null) _ = ReferenceSharingAuthorizationCodec.Encode(node.Sharing);
            var provider = inputs.Providers.Resource(node.ResourceId);
            if (node.Action.Admission.Mode != Acquisitions.Resource(node.ResourceId).Mode ||
                node.Action.Admission.Mode != ResourceAcquisitionMode.Provision && (!node.RequiresExternalOutput || GeneratedCredentialContracts.IsProducer(node)))
                throw new InvalidDataException("Operation has no acquisition authority.");
            if (node.Action.Admission.ResourceContract != inputs.Providers.Declaration.Spec.Resources[node.ResourceId].Type)
                throw new InvalidDataException("Graph Resource contract does not match the declaration.");
            if (GeneratedCredentialContracts.IsProducer(node) &&
                GeneratedCredentialInputCodec.Decode(node.Input)?.Owner != new ManagedResourceKey(inputs.Environment, inputs.Revision.ApplicationRecordId, node.ResourceId))
                throw new InvalidDataException("Inconsistent generated credential owner.");
            if (node.References != new DurableActionInputReferences(provider.Environment, provider.Instance,
                    provider.Configuration, provider.Implementation.Value) ||
                node.Action.Admission.ImplementationId != provider.Implementation)
                throw new InvalidDataException("Inconsistent graph references.");
        }
    }
}

/// <summary>One current, explicitly versioned graph format. No legacy readers.</summary>
public static class DurableOperationGraphCodec
{
    public static string Encode(DurableOperationGraph graph)
    {
        graph.ValidateObservationGroups();
        return JsonSerializer.Serialize(new GraphDocument(8,
        graph.Nodes.Select(n => new NodeDocument(n.Id.Value, n.ResourceId.Value,
            n.Action.Admission.ResourceContract.Value, n.Action.Admission.ProviderKind.Value,
            n.Action.Admission.ConfigurationContract.Value, n.Action.Admission.ImplementationId.Value,
            n.Action.ActionContract.Value, n.References.Environment.ToString(), n.References.ProviderInstance.ToString(),
            n.References.ConfigurationRevision.Value, n.Input.Format, n.Input.Json,
            n.Dependencies.Select(d => d.Value).ToArray(), n.OutputReferences.Select(r =>
                new OutputRefDocument(r.ProducerOperationId.Value, r.OutputContract, r.OutputKey)).ToArray(), n.RequiresExternalOutput,
            n.RequiredOutput is { } required ? new(n.Id.Value, required.Contract, required.Key) : null, (int)n.Action.Admission.Mode, n.Sharing)).ToArray(),
        graph.ReusedResources.Count == 0 ? null : graph.ReusedResources.Select(r => new ReuseDocument(r.Resource.Value, r.ManagementId, r.SourceGeneration, r.EvidenceFingerprint, r.Reintroduced)).ToArray(),
        graph.Baseline is { } b ? new BaselineDocument(b.Deployment.ToString(), b.Generation, b.Fingerprint) : null,
        graph.RetainedResources.Count == 0 ? null : graph.RetainedResources.Select(r => new ReuseDocument(r.Resource.Value, r.ManagementId, r.SourceGeneration, r.EvidenceFingerprint, false)).ToArray(), graph.Retirement?.Document, graph.WorkloadUpdate?.Document, ResourceImportIntentCodec.Encode(graph.Import), ResourceAcquisitionIntentCodec.Encode(graph.Acquisitions),
        graph.ObservationGroups.Select(g => new ObservationGroupDocument(g.Id, (int)g.Mode, g.Operations.Select(x => x.Value).ToArray())).ToArray()), GraphJsonContext.Default.GraphDocument);
    }

    public static DurableOperationGraph Decode(string json)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > 2097152) throw new InvalidDataException("Operation graph exceeds bound.");
            var document = JsonSerializer.Deserialize(json, GraphJsonContext.Default.GraphDocument);
            if (document is null || document.Version != 8 || document.Nodes is null || document.Retirement is not null && document.WorkloadUpdate is not null)
                throw new InvalidDataException("Unsupported operation graph format.");
            var graph = new DurableOperationGraph(document.Nodes.Select(n => new DurableOperationNode(OperationId.Create(n.Id),
                ManifestLocalId.Create(n.Resource), new(new(ResourceTypeId.Create(n.ResourceContract),
                    ProviderKindId.Create(n.Kind), ProviderConfigurationContractId.Create(n.Configuration),
                    ProviderImplementationId.Create(n.Implementation))
                { Mode = (ResourceAcquisitionMode)n.Mode }, OperationContractId.Create(n.Action)),
                new(EnvironmentRecordId.Parse(n.Environment), ProviderInstanceId.Parse(n.Provider),
                    ProviderConfigurationRevisionNumber.Create(n.Revision), n.Implementation),
                new(n.Format, n.Input), n.Dependencies.Select(OperationId.Create).ToArray())
            {
                OutputReferences = n.Outputs.Select(r => new OperationOutputRef(OperationId.Create(r.Producer), r.Contract, r.Key)).ToArray(),
                RequiresExternalOutput = n.RequiresExternalOutput,
                Sharing = n.Sharing,
                RequiredOutput = n.RequiredOutput is { } required && required.Producer == n.Id ? new(required.Contract, required.Key) :
                    n.RequiredOutput is null ? null : throw new InvalidDataException("Wrong accepted output producer.")
            }),
                (document.Reuse ?? []).Select(r => new ResourceReuse(ManifestLocalId.Create(r.Resource), r.Management, r.Generation, r.Evidence) { Reintroduced = r.Reintroduced }),
                document.Baseline is { } b ? new(ApplicationDeploymentId.Parse(b.Deployment), b.Generation, b.Fingerprint) : null,
                (document.Retained ?? []).Select(r => new ResourceReuse(ManifestLocalId.Create(r.Resource), r.Management, r.Generation, r.Evidence) { Reintroduced = r.Reintroduced }))
            {
                Import = ResourceImportIntentCodec.Decode(document.Import),
                Acquisitions = ResourceAcquisitionIntentCodec.Decode(document.Acquisitions),
                ObservationGroups = Array.AsReadOnly(document.Groups.Select(g => new ResourceObservationGroup(g.Id, (ResourceAcquisitionMode)g.Mode,
                    Array.AsReadOnly(g.Operations.Select(OperationId.Create).ToArray()))).ToArray())
            };
            graph.ValidateObservationGroups();
            return document.WorkloadUpdate is { } update ? graph.WithWorkloadUpdate(new(update)) :
                document.Retirement is { } retirement ? graph.WithRetirement(new(retirement)) : graph;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException or FormatException)
        { throw new InvalidDataException("Invalid operation graph."); }
    }

    public static string Fingerprint(DurablePlanningInputs inputs, DurableOperationGraph graph)
    {
        graph.ValidateReferences(inputs);
        if (inputs.Configuration is { } frozen && frozen.Revision != inputs.Revision) throw new InvalidDataException("Configuration revision mismatch.");
        var text = string.Join('\n', "haby-operation-plan/v6", inputs.Environment.ToString(), inputs.EnvironmentKey.Value,
            inputs.Revision.ApplicationRecordId.ToString(), inputs.Revision.RevisionNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ExactProviderResolutionSetCodec.Encode(inputs.Providers), Encode(graph),
            inputs.Configuration is { } configuration ? ConfigurationPlanningSnapshotCodec.Encode(configuration) : "unprepared");
        return "v6:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

public sealed record ResourceObservationGroup(string Id, ResourceAcquisitionMode Mode, IReadOnlyList<OperationId> Operations);
internal sealed record ObservationGroupDocument(string Id, int Mode, string[] Operations);
internal sealed record GraphDocument(int Version, NodeDocument[] Nodes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReuseDocument[]? Reuse = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BaselineDocument? Baseline = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReuseDocument[]? Retained = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InitialRetirementDocument? Retirement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkloadUpdateDocument? WorkloadUpdate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Import = null, string Acquisitions = "", ObservationGroupDocument[] Groups = null!);
internal sealed record ReuseDocument(string Resource, Guid Management, long Generation, string Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Reintroduced = false);
internal sealed record BaselineDocument(string Deployment, long Generation, string Fingerprint);
internal sealed record NodeDocument(string Id, string Resource, string ResourceContract, string Kind,
    string Configuration, string Implementation, string Action, string Environment, string Provider,
    long Revision, string Format, string Input, string[] Dependencies, OutputRefDocument[] Outputs, bool RequiresExternalOutput, OutputRefDocument? RequiredOutput, int Mode, ReferenceSharingAuthorization? Sharing);
internal sealed record OutputRefDocument(string Producer, string Contract, string Key);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(GraphDocument))]
internal partial class GraphJsonContext : JsonSerializerContext;
