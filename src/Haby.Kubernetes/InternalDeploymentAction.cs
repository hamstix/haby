using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using Haby.Configuration.Contracts;
using System.Collections.Immutable;

namespace Haby.Kubernetes;

/// <summary>Internal durable CREATE adapter. UPDATE needs a future genuinely accepted prior
/// Workload result and is deliberately absent from this registration.</summary>
internal sealed class InternalDeploymentAction(ISecretStore secrets,
    Func<KubernetesConnection, DeploymentKernel>? fixtureKernel = null) : IInternalActionHandler
{
    internal const string Create = "haby.kubernetes.deployment-create/v1";
    public InternalActionContract Contract { get; } = new("kubernetes.workload/v1alpha1", KubernetesProviderConfigurationCodec.Kind,
        KubernetesProviderConfigurationCodec.Shape, KubernetesProviderConfigurationCodec.Implementation, Create,
        "haby.kubernetes.deployment-intent/v2", "haby.kubernetes.deployment-receipt/v2");
    public bool ObservationRecovery => true;
    public bool RequiresRuntimePublication => true;
    public InternalManagedTarget ManagedTarget(InternalActionContext context) => RuntimeIdentityCodec.Target(Resolve(context));
    public string Prepare(InternalActionContext context)
    {
        var workload = Resolve(context);
        DeploymentRuntime? runtime = null;
        if (!workload.Declaration.Documents.IsEmpty)
        {
            var prepared = context.Runtime ?? throw new InvalidDataException("Verified runtime preparation required.");
            if (prepared.IdentityContract != "haby.kubernetes.runtime-account-receipt/v1") throw new InvalidDataException("Runtime identity contract unavailable.");
            var account = RuntimeIdentityCodec.Receipt(prepared.IdentityReceipt);
            if (account.Intent.ManagedWorkload != context.ManagedIdentity || account.Intent.Owner != Guid.Parse(context.Key.DeploymentId.ToString()) ||
                account.Intent.Binding.Environment != context.Provider.Environment.ToString() || account.Intent.Binding.Provider != context.Provider.Instance.ToString() ||
                account.Intent.Binding.Configuration != context.Provider.Configuration.Value || account.Intent.Binding.Implementation != context.Provider.Implementation.Value ||
                account.Intent.Namespace != workload.Placement.Declaration.Namespace || RuntimeIdentityCodec.Audience(account.Intent) != prepared.Audience ||
                !prepared.Documents.Keys.OrderBy(x => x.Value, StringComparer.Ordinal).SequenceEqual(workload.Declaration.Documents.OrderBy(x => x.Value, StringComparer.Ordinal)))
                throw new InvalidDataException("Runtime preparation scope mismatch.");
            var bootstrap = new RuntimeBootstrap(1, prepared.Binding.ToString("D"), prepared.Documents.ToImmutableSortedDictionary(x => x.Key.Value,
                x => x.Value.ToString(), StringComparer.Ordinal), RuntimeBootstrap.TokenPath,
                workload.Placement.Declaration.RuntimeConfiguration ?? throw new InvalidDataException("Frozen delivery unavailable."));
            runtime = new(account.Intent.Name, account.Uid, prepared.Audience, RuntimeBootstrapCodec.Encode(bootstrap));
        }
        return DeploymentIntents.Encode(DeploymentIntents.Prepare(workload, Guid.Parse(context.Key.DeploymentId.ToString()),
            context.ManagedIdentity, context.Key.Generation.Value, context.Marker, runtime: runtime));
    }
    public InternalObjectConfirmation? ObjectConfirmation(InternalActionContext context, string payload, string receipt)
    {
        if (DeploymentIntents.Decode(payload).Target.Runtime is null) return null;
        var decoded = DeploymentIntents.DecodeReceipt(receipt);
        if (!ValidateReceipt(context, payload, receipt, decoded.Source == ConfirmationSource.Observation)) throw new InvalidDataException("Deployment receipt invalid.");
        return new("deployment", Contract.OutputContract, 2, receipt, decoded.Uid);
    }

    public IReadOnlyList<ManifestLocalId> PreparationDocuments(InternalActionContext context)
    {
        var workload = Resolve(context);
        DeploymentIntents.ValidatePreparation(workload);
        var layout = context.Inputs.Configuration?.Layouts.Single(x => x.Component == workload.Input.Component)
            ?? throw new InvalidDataException("Frozen configuration unavailable.");
        if (workload.Declaration.Documents.Any(id => !layout.Documents.Any(d => d.Definition.Id == id)))
            throw new InvalidDataException("Selected Component document unavailable.");
        return workload.Declaration.Documents.OrderBy(x => x.Value, StringComparer.Ordinal).ToArray();
    }

    internal static EffectiveWorkload Resolve(InternalActionContext context)
    {
        if (context.Subject is not ProviderInput.Workload subject || context.Placement is null) throw new InvalidDataException("Deployment subject unavailable.");
        var manifest = context.Inputs.Providers.Declaration;
        var revisions = new List<PlacementRevisionInput>(); var assignments = new List<PlacementAssignment>();
        var pins = new Dictionary<(EnvironmentRecordId, ProviderAlias, PlacementReference), (Guid, PlacementDeclaration)>();
        foreach (var frozen in context.Inputs.Providers.Entries.Values.Where(x => x.Input is ProviderInput.Workload))
        {
            var input = (ProviderInput.Workload)frozen.Input;
            var savedPin = frozen.Placement is { } saved ? InternalPlacementInput.Decode(saved) : throw new InvalidDataException("Frozen Placement unavailable.");
            var pin = savedPin.Declaration;
            var placement = KubernetesPlacementCodec.ToTyped(pin);
            var revision = new PlacementRevisionInput(frozen.Environment, frozen.Alias, frozen.Instance, frozen.Configuration, frozen.Implementation, placement);
            var existing = revisions.SingleOrDefault(x => x.Environment == frozen.Environment && x.Alias == frozen.Alias && x.Declaration.Reference == placement.Reference);
            var key = (frozen.Environment, frozen.Alias, placement.Reference);
            if (existing is null) { revisions.Add(revision); pins.Add(key, (savedPin.Id, pin)); }
            else if (existing.Provider != revision.Provider || existing.Configuration != revision.Configuration || existing.Implementation != revision.Implementation || pins[key] != (savedPin.Id, pin))
                throw new InvalidDataException("Conflicting frozen Placement revision.");
            var declaration = manifest.Spec.Components[input.Component].Workloads[input.Id];
            var scope = declaration.PlacementRef is { } selector ? new PlacementScope(frozen.Environment, frozen.Alias, Selector: selector) :
                new PlacementScope(frozen.Environment, frozen.Alias, manifest.ApplicationId, input.Component);
            var assignment = new PlacementAssignment(scope, placement.Reference);
            var selected = assignments.SingleOrDefault(x => x.Scope == scope);
            if (selected is null) assignments.Add(assignment);
            else if (selected.Revision != assignment.Revision) throw new InvalidDataException("Conflicting frozen Placement selection.");
        }
        // Resolve the complete frozen generation once, including cross-Workload name
        // collisions, before selecting this action's exact declared subject.
        var effective = KubernetesWorkloadPlanner.ResolvePreparation(manifest, ApplicationVersion.Create(context.ApplicationVersion),
            context.Inputs.Providers, new PlacementInputs(revisions, assignments), context.Inputs.Configuration
                ?? throw new InvalidDataException("Frozen configuration unavailable."));
        return effective.Single(x => x.Input == subject);
    }
    public bool Validate(InternalActionContext context, string payload)
    {
        try
        {
            var intent = DeploymentIntents.Decode(payload);
            return intent.ExpectedUid is null && intent.LastConfirmed is null && Prepare(context) == payload;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException) { return false; }
    }
    public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation)
    {
        try
        {
            if (!Validate(context, payload)) return false;
            var intent = DeploymentIntents.Decode(payload); var result = DeploymentIntents.DecodeReceipt(receipt);
            return result.Binding == intent.Binding && result.Namespace == intent.Namespace && result.Name == intent.Name && result.Owner == intent.Owner &&
                result.ManagedWorkload == intent.ManagedWorkload && result.TargetGeneration == intent.TargetGeneration && result.Operation == intent.Operation &&
                result.Source == (observation ? ConfirmationSource.Observation : ConfirmationSource.Response) &&
                DeploymentIntents.Encode(intent with { Target = result.Projection }) == payload;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException) { return false; }
    }
    private DeploymentKernel Kernel(InternalActionContext context)
    {
        var config = new KubernetesProviderConfigurationCodec().DecodeTyped(context.Configuration.PayloadJson);
        var binding = new KubernetesBinding(context.Provider.Environment.ToString(), context.Provider.Instance.ToString(),
            context.Provider.Configuration.Value, context.Provider.Implementation.Value);
        var connection = new KubernetesConnection(binding, config.Endpoint, config.ServerCaPem, config.BearerToken, config.ClientCertificate, config.ClientPrivateKey);
        return fixtureKernel?.Invoke(connection) ?? new(connection, secrets);
    }
    private static InternalActionResult Result(KernelResult result) => new(result.Outcome switch
    {
        KernelOutcome.Confirmed => InternalActionOutcome.Confirmed,
        KernelOutcome.Uncertain => InternalActionOutcome.Uncertain,
        KernelOutcome.ObservationUnavailable => InternalActionOutcome.Unavailable,
        KernelOutcome.NotConfirmed => InternalActionOutcome.NotConfirmed,
        _ => InternalActionOutcome.Refused
    }, result.Code, result.Receipt is null ? null : DeploymentIntents.Encode(result.Receipt));
    public async ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token)
    {
        if (!Validate(context, payload)) return new(InternalActionOutcome.Refused, "intent-invalid");
        return Result(await Kernel(context).ExecuteAsync(DeploymentIntents.Decode(payload), token));
    }
    public async ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token)
    {
        if (!Validate(context, payload)) return new(InternalActionOutcome.Refused, "intent-invalid");
        return Result(await Kernel(context).ObserveAsync(DeploymentIntents.Decode(payload), token));
    }
}
