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

public interface IDurableActionHandler
{
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

    public DurableActionRegistry(IEnumerable<IDurableActionAdmissionHandler> registrations,
        IEnumerable<IDurableActionHandler> executors)
    {
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
        handlers.GetValueOrDefault(key);

}

/// <summary>The same pure admission rule is used by durable Plan, Check and atomic Commit.</summary>
public sealed class DurableActionAdmission(DurableActionRegistry registry)
{
    public static bool SupportsPilotShape(NormalizedManifestSpec spec) => spec.Resources.Count == 1 &&
        spec.Components.Count == 0 && spec.Exports.Count == 0 && spec.Parameters.Count == 0;

    public DurablePlanStatus Admit(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs)
    {
        if (!SupportsPilotShape(spec)) return DurablePlanStatus.UnsupportedShape;
        var resource = spec.Resources.Single().Value;
        if (resource.ProviderRef != inputs.Alias || provider.ImplementationId != inputs.ImplementationId)
            return DurablePlanStatus.ProviderRevisionUnavailable;
        var handler = registry.Find(new DurableActionAdmissionKey(resource.Type, provider));
        if (handler is null) return DurablePlanStatus.ProviderCapabilityMismatch;
        return handler.Validate(resource, inputs) ? DurablePlanStatus.Planned : DurablePlanStatus.UnsupportedShape;
    }
    public DurableOperationGraph Plan(NormalizedManifestSpec spec, ProviderAdapterKey provider, DurablePlanningInputs inputs)
    {
        var resource = spec.Resources.Single();
        var handler = registry.Find(new DurableActionAdmissionKey(resource.Value.Type, provider))
            ?? throw new InvalidDataException("Planning support unavailable.");
        var graph = new DurableOperationGraph(handler.Plan(resource.Key, resource.Value, inputs));
        graph.ValidateReferences(inputs);
        if (graph.Nodes.Any(n => n.ResourceId != resource.Key || n.Action.Admission != handler.Key.Admission))
            throw new InvalidDataException("Graph ownership is inconsistent.");
        return graph;
    }
    public bool Supports(DurableOperationGraph graph) => graph.Nodes.All(n => registry.Find(n.Action)?.Supports(n) == true);
}
