namespace Haby.Application;

/// <summary>Explicit bounded namespace snapshots; pagination never establishes absence.</summary>
internal sealed record RecipientObservationOptions(int MaximumObjects = 2048, int MaximumResponseBytes = 4194304, int MaximumCalls = 4096)
{
    internal void Validate()
    {
        if (MaximumObjects is < 1 or > 16384 || MaximumResponseBytes is < 1024 or > 16777216 || MaximumCalls is < 1 or > 8192)
            throw new ArgumentException("Invalid recipient observation bounds.");
    }
}
internal sealed record RecipientObservation(bool Absent, string Code, DurableActionPayload? Evidence = null);
internal interface IInternalRecipientObserver
{
    int MaximumObservationCalls { get; }
    ValueTask<RecipientObservation> ObserveRecipientsAsync(InternalActionContext context, string intent, string receipt,
        RecipientObservationOptions options, CancellationToken token);
    bool ValidateRecipients(string intent, string receipt, RecipientObservation observation);
}
