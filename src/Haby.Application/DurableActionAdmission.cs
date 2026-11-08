using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>Exact Resource and Provider envelope identity; no compatibility fallback.</summary>
public sealed record DurableActionAdmissionKey(ResourceTypeId ResourceContract, ProviderKindId ProviderKind,
    ProviderConfigurationContractId ConfigurationContract, ProviderImplementationId ImplementationId)
{
    public DurableActionAdmissionKey(ResourceTypeId resourceContract, ProviderAdapterKey provider)
        : this(resourceContract, ProviderKindId.Create(provider.ProviderKind),
            ProviderConfigurationContractId.Create(provider.ConfigurationContract), ProviderImplementationId.Create(provider.ImplementationId))
    { }
}
public sealed record DurableActionInputReferences(EnvironmentRecordId Environment, ProviderInstanceId ProviderInstance,
    ProviderConfigurationRevisionNumber ConfigurationRevision, string ImplementationId);
public sealed record DurableActionExecutionKey(DurableActionAdmissionKey Admission, OperationContractId ActionContract);

/// <summary>Build-time action registration. Admission is metadata-only and performs no I/O.</summary>
public interface IDurableActionAdmissionHandler
{
    DurableActionExecutionKey Key { get; }
    bool Validate(NormalizedResource resource, DurablePlanningInputs inputs);
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
    DurableActionExecutionKey Key { get; }
    ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation,
        CancellationToken cancellationToken);
    bool Supports(DurableOperationNode node);
}

/// <summary>Typed preparation stays inside the adapter, without an object target bridge.</summary>
public abstract class DurableActionAdmissionHandler<TInput> : IDurableActionAdmissionHandler where TInput : class
{
    public abstract DurableActionExecutionKey Key { get; }
    public abstract IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs);
    public abstract TInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs);
    public bool Validate(NormalizedResource resource, DurablePlanningInputs inputs) =>
        Prepare(resource, new(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, inputs.ImplementationId)) is not null;
}

public abstract class DurableActionHandler<TInput> : DurableActionAdmissionHandler<TInput>, IDurableActionHandler where TInput : class
{
    public virtual bool RequiresExternalOutput => false;
    public abstract string InputFormat { get; }
    public abstract JsonTypeInfo<TInput> InputTypeInfo { get; }
    protected DurableOperationNode PrepareNode(OperationId id, ManifestLocalId resourceId,
        NormalizedResource resource, DurablePlanningInputs inputs)
    {
        var references = new DurableActionInputReferences(inputs.Environment, inputs.ProviderInstance,
            inputs.ConfigurationRevision, inputs.ImplementationId);
        var input = Prepare(resource, references) ?? throw new InvalidDataException("Invalid action input.");
        return new(id, resourceId, Key, references, new(InputFormat, JsonSerializer.Serialize(input, InputTypeInfo)), []);
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
    private readonly HashSet<DurableActionExecutionKey> outputProducers;

    public DurableActionRegistry(IEnumerable<IDurableActionAdmissionHandler> registrations,
        IEnumerable<IDurableActionHandler> executors, IEnumerable<IExternalOperationOutputContract>? outputs = null)
    {
        var contracts = (outputs ?? []).ToArray();
        if (contracts.GroupBy(c => c.Producer).Any(g => g.Count() != 1))
            throw new InvalidOperationException("External output registration is ambiguous.");
        outputProducers = contracts.Select(c => c.Producer).ToHashSet();
        foreach (var handler in registrations)
        {
            var key = handler.Key;
            if (key?.Admission?.ResourceContract is null || key.ActionContract is null ||
                key.Admission.ProviderKind is null || key.Admission.ConfigurationContract is null ||
                key.Admission.ImplementationId is null ||
                !admissions.TryAdd(key.Admission, handler))
                throw new InvalidOperationException("Durable action registration is invalid or ambiguous.");
        }
        foreach (var executor in executors)
            if (executor.Key is not { } key || key.Admission?.ResourceContract is null || key.ActionContract is null ||
                key.Admission.ProviderKind is null || key.Admission.ConfigurationContract is null || key.Admission.ImplementationId is null ||
                !handlers.TryAdd(key, executor))
                throw new InvalidOperationException("Durable executor registration is invalid or ambiguous.");
    }

    public IDurableActionAdmissionHandler? Find(DurableActionAdmissionKey key) => admissions.GetValueOrDefault(key);
    public IDurableActionHandler? Find(DurableActionExecutionKey key) =>
        handlers.TryGetValue(key, out var handler) && (!handler.RequiresExternalOutput || outputProducers.Contains(key)) ? handler : null;

}

/// <summary>The same pure admission rule is used by durable Plan, Check and atomic Commit.</summary>
public sealed class DurableActionAdmission(DurableActionRegistry registry)
{
    public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target,
        ProviderAdapterKey provider, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        target.Resources.Count == accepted.Resources.Count && target.Resources.All(r =>
            accepted.Resources.ContainsKey(r.Key) &&
            registry.Find(new DurableActionAdmissionKey(r.Value.Type, provider)) is IDurableResourceReusePolicy policy &&
            policy.CanReuse(accepted, target, r.Key, inputs, configuration));

    public static bool SupportsPilotShape(NormalizedManifestSpec spec) => spec.Resources.Count is > 0 and <= 256 &&
        spec.Exports.Count == 0 && spec.Parameters.Count == 0 && SupportsConfigurationComponents(spec);

    public static bool SupportsConfigurationComponents(NormalizedManifestSpec spec) => spec.Components.Values.All(c =>
        c.Workloads.Count == 0 && c.Bindings.Values.All(b => b.Target is NormalizedLocalResourceTarget));

    // This pilot pins one Provider. A distinct alias is refused, never collapsed to a first match.
    public static ProviderAlias? SingleAlias(NormalizedManifestSpec spec) =>
        spec.Resources.Values.Select(r => r.ProviderRef).Distinct().Take(2).ToArray() is [var alias] ? alias : null;

    public bool HasPlanningSupport(NormalizedManifestSpec spec, ProviderAdapterKey provider) =>
        spec.Resources.Values.All(r => registry.Find(new DurableActionAdmissionKey(r.Type, provider)) is not null);

    public DurablePlanStatus Admit(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs,
        PreparedProviderConfiguration? configuration = null)
    {
        if (!SupportsPilotShape(spec)) return DurablePlanStatus.UnsupportedShape;
        if (SingleAlias(spec) != inputs.Alias || provider.ImplementationId != inputs.ImplementationId)
            return DurablePlanStatus.ProviderRevisionUnavailable;
        foreach (var resource in spec.Resources)
        {
            var handler = registry.Find(new DurableActionAdmissionKey(resource.Value.Type, provider));
            if (handler is null) return DurablePlanStatus.ProviderCapabilityMismatch;
            var valid = handler is IDurableResourcePlanComposer composer && configuration is not null
                ? composer.Validate(spec, resource.Key, inputs, configuration)
                : spec.Resources.Count == 1 && SupportsConfigurationComponents(spec) && handler.Validate(resource.Value, inputs);
            if (!valid) return DurablePlanStatus.UnsupportedShape;
        }
        return DurablePlanStatus.Planned;
    }
    public DurableOperationGraph Plan(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs,
        PreparedProviderConfiguration? configuration = null)
    {
        if (Admit(spec, provider, inputs, configuration) != DurablePlanStatus.Planned)
            throw new InvalidDataException("Planning support unavailable.");
        var nodes = new List<DurableOperationNode>();
        foreach (var resource in spec.Resources.OrderBy(r => r.Key.Value, StringComparer.Ordinal))
        {
            var handler = registry.Find(new DurableActionAdmissionKey(resource.Value.Type, provider))!;
            nodes.AddRange(handler is IDurableResourcePlanComposer composer && configuration is not null
                ? composer.Plan(spec, resource.Key, inputs, configuration)
                : handler.Plan(resource.Key, resource.Value, inputs));
        }
        var graph = new DurableOperationGraph(nodes);
        graph.ValidateReferences(inputs);
        if (!ValidateGraph(spec, provider, inputs, configuration, graph))
            throw new InvalidDataException("Graph ownership or contracts are inconsistent.");
        return graph;
    }
    public bool ValidateGraph(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs,
        PreparedProviderConfiguration? configuration, DurableOperationGraph graph)
    {
        if (Admit(spec, provider, inputs, configuration) != DurablePlanStatus.Planned) return false;
        graph.ValidateReferences(inputs);
        foreach (var node in graph.Nodes)
            if (!spec.Resources.TryGetValue(node.ResourceId, out var declared) ||
                node.Action.Admission != new DurableActionAdmissionKey(declared.Type, provider)) return false;
        foreach (var resource in spec.Resources)
        {
            var handler = registry.Find(new DurableActionAdmissionKey(resource.Value.Type, provider))!;
            if (handler is IDurableResourcePlanComposer composer && configuration is not null)
            {
                if (!composer.ValidateGraph(spec, resource.Key, inputs, configuration, graph)) return false;
            }
            else if (!graph.Nodes.Any(n => n.ResourceId == resource.Key) ||
                graph.Nodes.Where(n => n.ResourceId == resource.Key).Any(n => registry.Find(n.Action)?.Supports(n) != true ||
                    n.OutputReferences.Any(o => o.OutputContract != GeneratedCredentialContracts.OutputContract))) return false;
        }
        return true;
    }
    public bool Supports(DurableOperationGraph graph) => graph.Nodes.All(n => registry.Find(n.Action)?.Supports(n) == true);
}
