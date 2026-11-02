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

/// <summary>Explicit interpretation of the retained ResourceId + Create representation.</summary>
public sealed record DurableCreateActionMapping(ResourceTypeId ResourceContract, OperationContractId ActionContract);

/// <summary>Build-time action registration. Admission is metadata-only and performs no I/O.</summary>
public interface IDurableActionAdmissionHandler
{
    DurableActionExecutionKey Key { get; }
    bool Validate(NormalizedResource resource, DurablePlanningInputs inputs);
}

public interface IDurableActionHandler : IDurableActionAdmissionHandler
{
    ValueTask<DurableCreateExecutionResult> ExecuteAsync(DurableCreateOperation operation,
        NormalizedResource resource, CancellationToken cancellationToken);
}

/// <summary>Typed preparation stays inside the adapter, without an object target bridge.</summary>
public abstract class DurableActionAdmissionHandler<TInput> : IDurableActionAdmissionHandler where TInput : class
{
    public abstract DurableActionExecutionKey Key { get; }
    public abstract TInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs);
    public bool Validate(NormalizedResource resource, DurablePlanningInputs inputs) =>
        Prepare(resource, new(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, inputs.ImplementationId)) is not null;
}

public abstract class DurableActionHandler<TInput> : DurableActionAdmissionHandler<TInput>, IDurableActionHandler where TInput : class
{
    public abstract ValueTask<DurableCreateExecutionResult> ExecuteTypedAsync(DurableCreateOperation operation,
        TInput? input, CancellationToken cancellationToken);
    public ValueTask<DurableCreateExecutionResult> ExecuteAsync(DurableCreateOperation operation,
        NormalizedResource resource, CancellationToken cancellationToken) => ExecuteTypedAsync(operation,
            Prepare(resource, new DurableActionInputReferences(operation.Environment, operation.ProviderInstance,
                operation.ConfigurationRevision, operation.ImplementationId)), cancellationToken);
}

/// <summary>Validates current registrations at construction, without inspecting stored history.</summary>
public sealed class DurableActionRegistry
{
    private readonly Dictionary<DurableActionAdmissionKey, IDurableActionAdmissionHandler> admissions = [];
    private readonly Dictionary<DurableActionExecutionKey, IDurableActionHandler> handlers = [];
    private readonly Dictionary<ResourceTypeId, OperationContractId> creates = [];

    public DurableActionRegistry(IEnumerable<IDurableActionAdmissionHandler> registrations,
        IEnumerable<IDurableActionHandler> executors,
        IEnumerable<DurableCreateActionMapping> mappings)
    {
        foreach (var mapping in mappings)
            if (mapping.ResourceContract is null || mapping.ActionContract is null ||
                !creates.TryAdd(mapping.ResourceContract, mapping.ActionContract))
                throw new InvalidOperationException("Historical Create mapping is invalid or ambiguous.");
        foreach (var handler in registrations)
        {
            var key = handler.Key;
            if (key?.Admission?.ResourceContract is null || key.ActionContract is null ||
                key.Admission.ProviderKind is null || key.Admission.ConfigurationContract is null ||
                key.Admission.ImplementationId is null ||
                !creates.TryGetValue(key.Admission.ResourceContract, out var action) || action != key.ActionContract ||
                !admissions.TryAdd(key.Admission, handler))
                throw new InvalidOperationException("Durable action registration is invalid or ambiguous.");
        }
        foreach (var executor in executors)
            if (executor.Key is not { } key || !admissions.TryGetValue(key.Admission, out var admission) ||
                admission.Key != key || !handlers.TryAdd(key, executor))
                throw new InvalidOperationException("Durable executor registration is invalid or ambiguous.");
    }

    public IDurableActionAdmissionHandler? Find(DurableActionAdmissionKey key) => admissions.GetValueOrDefault(key);
    public IDurableActionHandler? Find(DurableActionExecutionKey key) =>
        handlers.GetValueOrDefault(key);
    public OperationContractId? HistoricalCreate(ResourceTypeId resource) => creates.GetValueOrDefault(resource);
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
        if (handler is null || registry.Find(handler.Key) is null) return DurablePlanStatus.ProviderCapabilityMismatch;
        return handler.Validate(resource, inputs) ? DurablePlanStatus.Planned : DurablePlanStatus.UnsupportedShape;
    }
}
