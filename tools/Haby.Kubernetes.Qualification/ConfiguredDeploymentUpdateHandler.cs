using Haby.Application;
using Haby.Domain;

// Delegate every capability unchanged; observe returned results before the caller's fault hook.
internal sealed class ConfiguredDeploymentUpdateHandler(IInternalActionHandler inner,
    Action<ConfiguredDeploymentUpdateDiagnostics.Stage, InternalActionResult> record) : IInternalActionHandler, IInternalReadinessHandler, IInternalRecipientObserver
{
    public int ObservationSeconds(string intent, string receipt) => ((IInternalReadinessHandler)inner).ObservationSeconds(intent, receipt);
    public bool ValidateObservation(string intent, string receipt, RolloutObservation observation) => ((IInternalReadinessHandler)inner).ValidateObservation(intent, receipt, observation);
    public ValueTask<RolloutObservation> ReadinessAsync(InternalActionContext context, string intent, string receipt, CancellationToken token) =>
        ((IInternalReadinessHandler)inner).ReadinessAsync(context, intent, receipt, token);
    public ValueTask<RecipientObservation> ObserveRecipientsAsync(InternalActionContext context, string intent, string receipt, RecipientObservationOptions options, CancellationToken token) =>
        ((IInternalRecipientObserver)inner).ObserveRecipientsAsync(context, intent, receipt, options, token);
    public int MaximumObservationCalls => ((IInternalRecipientObserver)inner).MaximumObservationCalls;
    public bool ValidateRecipients(string intent, string receipt, RecipientObservation observation) => ((IInternalRecipientObserver)inner).ValidateRecipients(intent, receipt, observation);
    public InternalActionContract Contract => inner.Contract;
    public bool ObservationRecovery => inner.ObservationRecovery;
    public bool RequiresPreparedConfiguration => inner.RequiresPreparedConfiguration;
    public bool EstablishesIdentity => inner.EstablishesIdentity;
    public bool RequiresRuntimePublication => inner.RequiresRuntimePublication;
    public bool UpdatesObject => inner.UpdatesObject;
    public InternalManagedTarget? ManagedTarget(InternalActionContext context) => inner.ManagedTarget(context);
    public InternalObjectConfirmation? ObjectConfirmation(InternalActionContext context, string payload, string receipt) => inner.ObjectConfirmation(context, payload, receipt);
    public InternalIdentityConfirmation? IdentityConfirmation(InternalActionContext context, string payload, string receipt) => inner.IdentityConfirmation(context, payload, receipt);
    public IReadOnlyList<ManifestLocalId> PreparationDocuments(InternalActionContext context) => inner.PreparationDocuments(context);
    public string Prepare(InternalActionContext context) => inner.Prepare(context);
    public bool Validate(InternalActionContext context, string payload) => inner.Validate(context, payload);
    public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation) => inner.ValidateReceipt(context, payload, receipt, observation);
    public async ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token)
    {
        var result = await inner.ExecuteAsync(context, payload, token);
        record(ConfiguredDeploymentUpdateDiagnostics.Stage.DelegateExecute, result);
        return result;
    }
    public async ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token)
    {
        var result = await inner.ObserveAsync(context, payload, token);
        record(ConfiguredDeploymentUpdateDiagnostics.Stage.DelegateObserve, result);
        return result;
    }
}
