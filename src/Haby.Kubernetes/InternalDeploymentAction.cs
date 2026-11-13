using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;

namespace Haby.Kubernetes;

/// <summary>Internal durable CREATE adapter. UPDATE needs a future genuinely accepted prior
/// Workload result and is deliberately absent from this registration.</summary>
internal sealed class InternalDeploymentAction(ISecretStore secrets,
    Func<KubernetesConnection, DeploymentKernel>? fixtureKernel = null) : IInternalActionHandler
{
    internal const string Create = "haby.kubernetes.deployment-create/v1";
    public InternalActionContract Contract { get; } = new("kubernetes.workload/v1alpha1", KubernetesProviderConfigurationCodec.Kind,
        KubernetesProviderConfigurationCodec.Shape, KubernetesProviderConfigurationCodec.Implementation, Create,
        "haby.kubernetes.deployment-intent/v1", "haby.kubernetes.deployment-receipt/v1");
    public bool ObservationRecovery => true;
    public string Prepare(InternalActionContext context)
    {
        if (context.Subject is not ProviderInput.Workload subject || context.Placement is null) throw new InvalidDataException("Deployment subject unavailable.");
        var placement = KubernetesPlacementCodec.ToTyped(context.Placement);
        var revision = new PlacementRevisionInput(context.Provider.Environment, context.Provider.Alias, context.Provider.Instance,
            context.Provider.Configuration, context.Provider.Implementation, placement);
        var declaration = context.Inputs.Configuration!.Manifest.Spec.Components[subject.Component].Workloads[subject.Id];
        var scope = declaration.PlacementRef is { } selector ? new PlacementScope(context.Inputs.Environment, context.Provider.Alias, Selector: selector) :
            new PlacementScope(context.Inputs.Environment, context.Provider.Alias, context.Inputs.Providers.Declaration.ApplicationId, subject.Component);
        var effective = KubernetesWorkloadPlanner.Resolve(context.Inputs.Providers.Declaration, ApplicationVersion.Create(context.ApplicationVersion),
            context.Inputs.Providers, new PlacementInputs([revision], [new(scope, placement.Reference)]));
        var workload = effective.Single(x => x.Input == subject);
        return DeploymentIntents.Encode(DeploymentIntents.Prepare(workload, Guid.Parse(context.Key.DeploymentId.ToString()),
            context.ManagedIdentity, context.Key.Generation.Value, context.Marker));
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
