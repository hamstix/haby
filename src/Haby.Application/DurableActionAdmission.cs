using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>Exact Resource and Provider envelope identity; no compatibility fallback.</summary>
public sealed record DurableActionAdmissionKey(ResourceTypeId ResourceContract, ProviderKindId ProviderKind,
    ProviderConfigurationContractId ConfigurationContract, ProviderImplementationId ImplementationId)
{
    public ResourceAcquisitionMode Mode { get; init; }

    public DurableActionAdmissionKey(ResourceTypeId resourceContract, ProviderAdapterKey provider)
        : this(resourceContract, ProviderKindId.Create(provider.ProviderKind),
            ProviderConfigurationContractId.Create(provider.ConfigurationContract), ProviderImplementationId.Create(provider.ImplementationId))
    { }
}
public sealed record DurableActionInputReferences(EnvironmentRecordId Environment, ProviderInstanceId ProviderInstance,
    ProviderConfigurationRevisionNumber ConfigurationRevision, string ImplementationId)
{
    public ResourceNamingSnapshot Naming { get; init; } = ResourceNamingSnapshot.Default;
}
public sealed record DurableActionExecutionKey(DurableActionAdmissionKey Admission, OperationContractId ActionContract);

/// <summary>Build-time action registration. Admission is metadata-only and performs no I/O.</summary>
public interface IDurableActionAdmissionHandler
{
    DurableActionExecutionKey Key { get; }
    bool Validate(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs);
    IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs);

}

/// <summary>Optional pure composition seam. The exact Resource registration owns its nodes and relationships.
/// Configuration is the validated pinned envelope, never a current alias lookup or credential material.</summary>
public interface IDurableResourcePlanComposer
{
    bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration);
    IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration);
    bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration, DurableOperationGraph graph);
}

public interface IDurableActionHandler
{
    /// <summary>Execution requires exactly one registered atomic external-output validator for this producer.</summary>
    bool RequiresExternalOutput => false;
    ExternalOperationOutputIdentity? OutputIdentity => null;
    DurableActionExecutionKey Key { get; }
    ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation,
        CancellationToken cancellationToken);
    bool Supports(DurableOperationNode node);
}

/// <summary>Explicit trusted capability for observational verification only. Restart may reverify
/// the same dispatched attempt because this handler performs no external mutation.</summary>
public interface IReadOnlyResourceObservationHandler : IDurableActionHandler;
public interface IReadOnlyResourceImportHandler : IReadOnlyResourceObservationHandler;

/// <summary>Typed preparation stays inside the adapter, without an object target bridge.</summary>
public abstract class DurableActionAdmissionHandler<TInput> : IDurableActionAdmissionHandler where TInput : class
{
    public abstract DurableActionExecutionKey Key { get; }
    public abstract IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs);
    public abstract TInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs);
    public bool Validate(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
    {
        var provider = inputs.Providers.Resource(resourceId);
        return Prepare(resource, new(provider.Environment, provider.Instance, provider.Configuration, provider.Implementation.Value)
        { Naming = provider.ResourceNaming() }) is not null;
    }
}

public abstract class DurableActionHandler<TInput> : DurableActionAdmissionHandler<TInput>, IDurableActionHandler where TInput : class
{
    public virtual bool RequiresExternalOutput => false;
    public virtual ExternalOperationOutputIdentity? OutputIdentity => null;
    public abstract string InputFormat { get; }
    public abstract JsonTypeInfo<TInput> InputTypeInfo { get; }
    protected DurableOperationNode PrepareNode(OperationId id, ManifestLocalId resourceId,
        NormalizedResource resource, DurablePlanningInputs inputs)
    {
        var provider = inputs.Providers.Resource(resourceId);
        var references = new DurableActionInputReferences(provider.Environment, provider.Instance, provider.Configuration, provider.Implementation.Value);
        var input = Prepare(resource, references with { Naming = provider.ResourceNaming() }) ?? throw new InvalidDataException("Invalid action input.");
        return new(id, resourceId, Key, references, new(InputFormat, JsonSerializer.Serialize(input, InputTypeInfo)), [])
        { RequiresExternalOutput = RequiresExternalOutput, RequiredOutput = OutputIdentity };
    }
    public bool Supports(DurableOperationNode node) => Decode(node) is not null;
    public TInput? Decode(DurableOperationNode node)
    {
        if (node.Action != Key || node.Input.Format != InputFormat) return null;
        try { return JsonSerializer.Deserialize(node.Input.Json, InputTypeInfo); }
        catch (JsonException) { return null; }
    }
    public abstract ValueTask<DurableExecutionResult> ExecuteTypedAsync(DurableOperation operation,
        TInput? input, CancellationToken cancellationToken);
    public ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        var input = Decode(operation.Node);
        return input is null ? ValueTask.FromResult(new DurableExecutionResult(DurableExecutionOutcome.InputUnavailable, operation))
            : ExecuteTypedAsync(operation, input, cancellationToken);
    }
}

/// <summary>Validates current registrations at construction, without inspecting stored history.</summary>
public sealed class DurableActionRegistry
{
    private readonly Dictionary<DurableActionAdmissionKey, IDurableActionAdmissionHandler> admissions = [];
    private readonly Dictionary<DurableActionExecutionKey, IDurableActionHandler> handlers = [];
    private readonly Dictionary<DurableActionExecutionKey, IExternalOperationOutputContract> outputContracts;
    private readonly Dictionary<DurableActionExecutionKey, ExternalOperationOutputIdentity> outputIdentities;

    public DurableActionRegistry(IEnumerable<IDurableActionAdmissionHandler> registrations,
        IEnumerable<IDurableActionHandler> executors, IEnumerable<IExternalOperationOutputContract>? outputs = null)
    {
        var contracts = (outputs ?? []).ToArray();
        if (contracts.GroupBy(c => c.Producer).Any(g => g.Count() != 1))
            throw new InvalidOperationException("External output registration is ambiguous.");
        outputContracts = contracts.ToDictionary(c => c.Producer);
        outputIdentities = contracts.ToDictionary(c => c.Producer, c => c.Identity);
        foreach (var handler in registrations)
        {
            var key = handler.Key;
            if (key?.Admission?.ResourceContract is null || key.ActionContract is null ||
                key.Admission.ProviderKind is null || key.Admission.ConfigurationContract is null ||
                key.Admission.ImplementationId is null || !Enum.IsDefined(key.Admission.Mode) ||
                !admissions.TryAdd(key.Admission, handler))
                throw new InvalidOperationException("Durable action registration is invalid or ambiguous.");
        }
        foreach (var executor in executors)
            if (executor.Key is not { } key || key.Admission?.ResourceContract is null || key.ActionContract is null ||
                key.Admission.ProviderKind is null || key.Admission.ConfigurationContract is null || key.Admission.ImplementationId is null || !Enum.IsDefined(key.Admission.Mode) ||
                !handlers.TryAdd(key, executor))
                throw new InvalidOperationException("Durable executor registration is invalid or ambiguous.");
    }

    public IDurableActionAdmissionHandler? Find(DurableActionAdmissionKey key) => admissions.GetValueOrDefault(key);
    internal DurableOperationNode FreezeEvidence(DurableOperationNode node, DurablePlanningInputs inputs)
    {
        // Exact registered metadata wins while planning. Absence never writes a false
        // obligation over the typed planner's explicit declaration.
        if (handlers.TryGetValue(node.Action, out var handler))
        {
            if (handler.RequiresExternalOutput != (handler.OutputIdentity is not null))
                throw new InvalidDataException("Action evidence metadata is incomplete.");
            if (node.RequiresExternalOutput != handler.RequiresExternalOutput || node.RequiredOutput != handler.OutputIdentity)
                throw new InvalidDataException("Planned evidence obligation disagrees with the exact action registration.");
        }
        if (outputIdentities.TryGetValue(node.Action, out var identity) && node.RequiredOutput != identity)
            throw new InvalidDataException("Planned evidence obligation disagrees with the exact output registration.");
        var request = inputs.Acquisitions.Resource(node.ResourceId).SharingRequest;
        if (request is null) return node with { Sharing = null };
        if (node.Action.Admission.Mode != ResourceAcquisitionMode.Reference ||
            outputContracts.GetValueOrDefault(node.Action) is not IResourceReferenceSharingContract sharing ||
            sharing.AuthorizeSharing(node with { Sharing = null }, request) is not { } decision)
            throw new InvalidDataException("Explicit Reference sharing capability is unavailable.");
        node = node with { Sharing = new(sharing.SharingContract, request, decision) };
        if (!ReferenceSharingAuthorizationCodec.Supports(node, sharing)) throw new InvalidDataException("Reference sharing authorization is invalid.");
        return node;
    }
    private bool SupportsRequiredEvidence(DurableActionExecutionKey key) => key.Admission.Mode switch
    {
        ResourceAcquisitionMode.Provision => true,
        ResourceAcquisitionMode.ImportUseAndRetain => outputContracts.GetValueOrDefault(key) is IResourceObservationOutputContract,
        ResourceAcquisitionMode.Reference => outputContracts.GetValueOrDefault(key) is IResourceReferenceOutputContract,
        _ => false
    };
    public IDurableActionHandler? Find(DurableActionExecutionKey key) =>
        handlers.TryGetValue(key, out var handler) && SupportsRequiredEvidence(key) &&
        (!handler.RequiresExternalOutput || outputContracts.ContainsKey(key)) ? handler : null;
    internal bool MatchesEvidence(DurableOperationNode node) => Find(node.Action) is { } handler &&
        node.RequiresExternalOutput == handler.RequiresExternalOutput && node.RequiredOutput == handler.OutputIdentity &&
        (node.Sharing is null || outputContracts.TryGetValue(node.Action, out var contract) && ReferenceSharingAuthorizationCodec.Supports(node, contract)) &&
        (node.RequiredOutput is null || outputIdentities.TryGetValue(node.Action, out var identity) && identity == node.RequiredOutput);

}

/// <summary>The same pure admission rule is used by durable Plan, Check and atomic Commit.</summary>
public sealed record DurableResourceProviderContext(ProviderAdapterKey Provider, PreparedProviderConfiguration Configuration);

public sealed class DurableActionAdmission(DurableActionRegistry registry)
{
    private static DurableActionAdmissionKey AdmissionKey(ResourceTypeId type, ProviderAdapterKey provider, DurablePlanningInputs inputs, ManifestLocalId resource) =>
        new(type, provider) { Mode = inputs.Acquisitions.Resource(resource).Mode };

    public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target,
        ProviderAdapterKey provider, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        accepted.Resources.All(r =>
            target.Resources.ContainsKey(r.Key) &&
            registry.Find(AdmissionKey(r.Value.Type, provider, inputs, r.Key)) is IDurableResourceReusePolicy policy &&
            policy.CanReuse(accepted, target, r.Key, inputs, configuration)) &&
        target.Resources.Where(r => !accepted.Resources.ContainsKey(r.Key)).All(r =>
            registry.Find(AdmissionKey(r.Value.Type, provider, inputs, r.Key)) is IDurableResourceAdditionPolicy policy &&
            policy.CanAdd(accepted, target, r.Key, inputs, configuration));

    public static bool SupportsPilotShape(NormalizedManifestSpec spec) => spec.Resources.Count is > 0 and <= 256 &&
        spec.Exports.Count == 0 && spec.Parameters.Count == 0 && SupportsConfigurationComponents(spec);

    public static bool SupportsConfigurationComponents(NormalizedManifestSpec spec) => spec.Components.Values.All(c =>
        c.Workloads.Count == 0 && c.Bindings.Values.All(b => b.Target is NormalizedLocalResourceTarget));

    // This pilot pins one Provider. A distinct alias is refused, never collapsed to a first match.
    public static ProviderAlias? SingleAlias(NormalizedManifestSpec spec) =>
        spec.Resources.Values.Select(r => r.ProviderRef).Distinct().Take(2).ToArray() is [var alias] ? alias : null;

    public bool HasPlanningSupport(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs? inputs = null,
        IReadOnlySet<ManifestLocalId>? existingResources = null) =>
        spec.Resources.Where(r => existingResources?.Contains(r.Key) != true).All(r => registry.Find(new DurableActionAdmissionKey(r.Value.Type, provider)
        { Mode = inputs?.Acquisitions.Resource(r.Key).Mode ?? ResourceAcquisitionMode.Provision }) is not null);

    internal DurablePlanStatus PlanningSupportStatus(NormalizedManifestSpec spec, ProviderAdapterKey provider,
        DurablePlanningInputs inputs, IReadOnlySet<ManifestLocalId>? existingResources = null, IReadOnlyDictionary<ManifestLocalId, DurableResourceProviderContext>? exact = null)
    {
        foreach (var resource in spec.Resources.Where(x => existingResources?.Contains(x.Key) != true))
            if (registry.Find(AdmissionKey(resource.Value.Type, exact?.GetValueOrDefault(resource.Key)?.Provider ?? provider, inputs, resource.Key)) is null)
                return UnsupportedMode(inputs.Acquisitions.Resource(resource.Key).Mode);
        return DurablePlanStatus.Planned;
    }
    private static DurablePlanStatus UnsupportedMode(ResourceAcquisitionMode mode) => mode switch
    {
        ResourceAcquisitionMode.ImportUseAndRetain => DurablePlanStatus.ImportNotSupported,
        ResourceAcquisitionMode.Reference => DurablePlanStatus.ReferenceNotSupported,
        _ => DurablePlanStatus.ProviderCapabilityMismatch
    };

    public DurablePlanStatus Admit(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs,
        PreparedProviderConfiguration? configuration = null, IReadOnlySet<ManifestLocalId>? existingResources = null,
        IReadOnlyDictionary<ManifestLocalId, DurableResourceProviderContext>? exact = null)
    {
        if (!SupportsPilotShape(spec)) return DurablePlanStatus.UnsupportedShape;
        inputs.Validate();
        var completeInputs = spec.Resources.Keys.ToHashSet().SetEquals(inputs.Providers.Entries.Keys.OfType<ProviderInput.Resource>().Select(x => x.Id)) &&
            inputs.Providers.Entries.Keys.All(x => x is ProviderInput.Resource);
        if (exact is null)
        {
            var pilot = inputs.Providers.RequireUniformResourceProvider();
            if (SingleAlias(spec) != pilot.Alias || provider.ImplementationId != pilot.Implementation.Value)
                return DurablePlanStatus.ProviderRevisionUnavailable;
        }
        else if (!spec.Resources.Keys.ToHashSet().SetEquals(exact.Keys) || spec.Resources.Any(r =>
            inputs.Providers.Resource(r.Key).Alias != r.Value.ProviderRef ||
            exact[r.Key].Provider.ImplementationId != inputs.Providers.Resource(r.Key).Implementation.Value))
            return DurablePlanStatus.ProviderRevisionUnavailable;
        foreach (var resource in spec.Resources.Where(r => existingResources?.Contains(r.Key) != true))
        {
            var selected = exact?.GetValueOrDefault(resource.Key);
            var handler = registry.Find(AdmissionKey(resource.Value.Type, selected?.Provider ?? provider, inputs, resource.Key));
            var prepared = selected?.Configuration ?? configuration;
            if (handler is null) return UnsupportedMode(inputs.Acquisitions.Resource(resource.Key).Mode);
            var valid = handler is IDurableResourcePlanComposer composer && prepared is not null
                ? composer.Validate(spec, resource.Key, inputs, prepared)
                : spec.Resources.Count == 1 && SupportsConfigurationComponents(spec) && handler.Validate(resource.Key, resource.Value, inputs);
            if (!valid) return DurablePlanStatus.UnsupportedShape;
            if (!completeInputs) return DurablePlanStatus.PlanInputUnavailable;
            var names = inputs.Providers.Resource(resource.Key).ResourceNaming();
            if (names.Reference is not null && handler is not IResourceNamingPolicyHandler)
                return DurablePlanStatus.ProviderCapabilityMismatch;
            if (handler is IResourceNamingPolicyHandler naming && !naming.SupportsNaming(names.Policy))
                return DurablePlanStatus.UnsupportedShape;
        }
        return completeInputs ? DurablePlanStatus.Planned : DurablePlanStatus.PlanInputUnavailable;
    }
    public IReadOnlyList<DurableOperationNode> DescribeProducers(NormalizedManifestSpec spec, ProviderAdapterKey provider,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, IReadOnlySet<ManifestLocalId>? existingResources = null,
        IReadOnlyDictionary<ManifestLocalId, DurableResourceProviderContext>? exact = null)
    {
        if (Admit(spec, provider, inputs, configuration, existingResources, exact) != DurablePlanStatus.Planned) throw new InvalidDataException("Planning support unavailable.");
        return spec.Resources.Where(r => existingResources?.Contains(r.Key) != true).SelectMany(r =>
        {
            var handler = registry.Find(AdmissionKey(r.Value.Type, exact?.GetValueOrDefault(r.Key)?.Provider ?? provider, inputs, r.Key))!;
            return handler is IDurableResourcePlanComposer composer ? composer.Plan(spec, r.Key, inputs, exact?.GetValueOrDefault(r.Key)?.Configuration ?? configuration)
                : handler.Plan(r.Key, r.Value, inputs);
        }).ToArray();
    }

    public DurableOperationGraph Plan(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs,
        PreparedProviderConfiguration? configuration = null, IReadOnlyDictionary<ManifestLocalId, DurableResourceProviderContext>? exact = null)
    {
        if (Admit(spec, provider, inputs, configuration, exact: exact) != DurablePlanStatus.Planned)
            throw new InvalidDataException("Planning support unavailable.");
        var nodes = new List<DurableOperationNode>();
        foreach (var resource in spec.Resources.OrderBy(r => r.Key.Value, StringComparer.Ordinal))
        {
            var handler = registry.Find(AdmissionKey(resource.Value.Type, exact?.GetValueOrDefault(resource.Key)?.Provider ?? provider, inputs, resource.Key))!;
            nodes.AddRange(handler is IDurableResourcePlanComposer composer && configuration is not null
                ? composer.Plan(spec, resource.Key, inputs, exact?.GetValueOrDefault(resource.Key)?.Configuration ?? configuration)
                : handler.Plan(resource.Key, resource.Value, inputs));
        }
        var graph = new DurableOperationGraph(nodes.Select(n => registry.FreezeEvidence(n, inputs))) { Import = inputs.Import, Acquisitions = inputs.Acquisitions };
        graph.ValidateReferences(inputs);
        if (!ValidateGraph(spec, provider, inputs, configuration, graph, exact))
            throw new InvalidDataException("Graph ownership or contracts are inconsistent.");
        return graph;
    }
    public bool ValidateGraph(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs,
        PreparedProviderConfiguration? configuration, DurableOperationGraph graph, IReadOnlyDictionary<ManifestLocalId, DurableResourceProviderContext>? exact = null)
    {
        if (Admit(spec, provider, inputs, configuration, exact: exact) != DurablePlanStatus.Planned) return false;
        graph.ValidateReferences(inputs);
        foreach (var node in graph.Nodes)
            if (!spec.Resources.TryGetValue(node.ResourceId, out var declared) ||
                node.Action.Admission != AdmissionKey(declared.Type, exact?.GetValueOrDefault(node.ResourceId)?.Provider ?? provider, inputs, node.ResourceId)) return false;
        foreach (var resource in spec.Resources)
        {
            var handler = registry.Find(AdmissionKey(resource.Value.Type, exact?.GetValueOrDefault(resource.Key)?.Provider ?? provider, inputs, resource.Key))!;
            if (handler is IDurableResourcePlanComposer composer && configuration is not null)
            {
                if (!composer.ValidateGraph(spec, resource.Key, inputs, exact?.GetValueOrDefault(resource.Key)?.Configuration ?? configuration, graph)) return false;
            }
            else if (!graph.Nodes.Any(n => n.ResourceId == resource.Key) ||
                graph.Nodes.Where(n => n.ResourceId == resource.Key).Any(n => registry.Find(n.Action)?.Supports(n) != true ||
                    n.OutputReferences.Any(o => o.OutputContract != GeneratedCredentialContracts.OutputContract))) return false;
        }
        return true;
    }
    public DurableOperationGraph PlanUpdate(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration, IReadOnlyList<ResourceReuse> reused, ResourceReuseBaseline baseline, IReadOnlyList<ResourceReuse>? retained = null, IReadOnlyDictionary<ManifestLocalId, DurableResourceProviderContext>? exact = null)
    {
        if (Admit(spec, provider, inputs, configuration, reused.Select(x => x.Resource).ToHashSet(), exact) != DurablePlanStatus.Planned) throw new InvalidDataException("Planning support unavailable.");
        var nodes = spec.Resources.Where(r => !reused.Any(x => x.Resource == r.Key)).SelectMany(r =>
        {
            var handler = registry.Find(AdmissionKey(r.Value.Type, exact?.GetValueOrDefault(r.Key)?.Provider ?? provider, inputs, r.Key))!;
            return handler is IDurableResourcePlanComposer composer ? composer.Plan(spec, r.Key, inputs, exact?.GetValueOrDefault(r.Key)?.Configuration ?? configuration)
                : handler.Plan(r.Key, r.Value, inputs);
        });
        var graph = new DurableOperationGraph(nodes.Select(n => registry.FreezeEvidence(n, inputs)), reused, baseline, retained) { Acquisitions = inputs.Acquisitions };
        graph.ValidateReferences(inputs);
        if (!spec.Resources.Keys.ToHashSet().SetEquals(graph.Nodes.Select(n => n.ResourceId).Concat(reused.Select(r => r.Resource))) ||
            graph.Nodes.Any(n => !spec.Resources.TryGetValue(n.ResourceId, out var declared) ||
                n.Action.Admission != AdmissionKey(declared.Type, exact?.GetValueOrDefault(n.ResourceId)?.Provider ?? provider, inputs, n.ResourceId)))
            throw new InvalidDataException("Graph Resource inventory is inconsistent.");
        foreach (var resource in spec.Resources.Where(r => !reused.Any(x => x.Resource == r.Key)))
        {
            var handler = registry.Find(AdmissionKey(resource.Value.Type, exact?.GetValueOrDefault(resource.Key)?.Provider ?? provider, inputs, resource.Key))!;
            if (handler is IDurableResourcePlanComposer composer && !composer.ValidateGraph(spec, resource.Key, inputs, exact?.GetValueOrDefault(resource.Key)?.Configuration ?? configuration, graph))
                throw new InvalidDataException("Graph ownership or contracts are inconsistent.");
            if (handler is not IDurableResourcePlanComposer && graph.Nodes.Where(n => n.ResourceId == resource.Key).Any(n =>
                registry.Find(n.Action)?.Supports(n) != true || n.OutputReferences.Any(o => o.OutputContract != GeneratedCredentialContracts.OutputContract)))
                throw new InvalidDataException("Graph action support is unavailable.");
        }
        return graph;
    }

    public bool SameResourceNames(NormalizedManifestSpec spec, ManifestLocalId resource, ProviderAdapterKey provider,
        DurablePlanningInputs original, DurablePlanningInputs proposed, PreparedProviderConfiguration configuration) =>
        registry.Find(AdmissionKey(spec.Resources[resource].Type, provider, original, resource)) is IDurableResourceNamingCompatibility policy
            ? policy.SameNames(spec, resource, original, proposed, configuration)
            : original.Providers.Resource(resource).ResourceNaming() == proposed.Providers.Resource(resource).ResourceNaming();

    public bool CanReuseResource(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
        ProviderAdapterKey provider, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, DurablePlanningInputs originalInputs) =>
        accepted.Resources.TryGetValue(resource, out var declared) &&
        registry.Find(AdmissionKey(declared.Type, provider, originalInputs, resource)) is IDurableResourceReusePolicy policy &&
        policy.CanReuse(accepted, target, resource, inputs, configuration);

    public bool CanRetainResource(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource,
        ProviderAdapterKey provider, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, DurablePlanningInputs originalInputs) =>
        original.Resources.TryGetValue(resource, out var declared) &&
        registry.Find(AdmissionKey(declared.Type, provider, originalInputs, resource)) is IDurableResourceRetentionPolicy policy &&
        policy.CanRetain(original, target, resource, inputs, configuration);

    public bool CanAddResource(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
        ProviderAdapterKey provider, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        target.Resources.TryGetValue(resource, out var declared) &&
        registry.Find(AdmissionKey(declared.Type, provider, inputs, resource)) is IDurableResourceAdditionPolicy policy &&
        policy.CanAdd(accepted, target, resource, inputs, configuration);

    // Validate original creation nodes against their own frozen declaration, including
    // a source generation that itself reused peers. No baseline traversal is needed.
    public bool ValidateCreationGraph(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration, DurableOperationGraph graph, IReadOnlyDictionary<ManifestLocalId, DurableResourceProviderContext>? exact = null)
    {
        if (!spec.Resources.Keys.ToHashSet().SetEquals(graph.Nodes.Select(n => n.ResourceId).Concat(graph.ReusedResources.Select(r => r.Resource)))) return false;
        var expected = graph.Baseline is { } baseline
            ? PlanUpdate(spec, provider, inputs, configuration, graph.ReusedResources, baseline, graph.RetainedResources, exact)
            : Plan(spec, provider, inputs, configuration, exact);
        return expected.Equals(graph);
    }

    public bool Supports(DurableOperationGraph graph) => graph.Nodes.All(n =>
        (n.Action.Admission.Mode == ResourceAcquisitionMode.Provision ||
         n.Action.Admission.Mode is ResourceAcquisitionMode.ImportUseAndRetain or ResourceAcquisitionMode.Reference && n.RequiresExternalOutput && !GeneratedCredentialContracts.IsProducer(n)) &&
        registry.MatchesEvidence(n) && registry.Find(n.Action)?.Supports(n) == true &&
        (n.Action.Admission.Mode != ResourceAcquisitionMode.Reference || registry.Find(n.Action) is IReadOnlyResourceObservationHandler));
}
