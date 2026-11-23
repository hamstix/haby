using Haby.Domain;

namespace Haby.Application;

public sealed record ObservationOperationConfirmation(DurableOperationKey Key, Guid AttemptId, ExternalOperationOutput Output);
public sealed record ImportedCredentialConfirmation(ManagedResourceKey Owner, ProtectedCredentialReference Staged);
public sealed record ResourceObservationAcceptance(IReadOnlyList<ObservationOperationConfirmation> Operations,
    IReadOnlyList<ImportedCredentialConfirmation> Credentials, IReadOnlyList<ProviderResourceReservation> Reservations,
    DurableActionPayload Evidence);
public sealed record ResourceObservationReceipt(Guid Id, DurableActionPayload Evidence,
    IReadOnlyList<ObservationOperationConfirmation> Operations);
public enum ResourceObservationAcceptanceStatus { Accepted, Existing, Conflict, InputUnavailable, StorageUnavailable }
public sealed record ResourceObservationAcceptanceResult(ResourceObservationAcceptanceStatus Status)
{
    public bool IsSuccess => Status is ResourceObservationAcceptanceStatus.Accepted or ResourceObservationAcceptanceStatus.Existing;
}

/// <summary>One catalog transaction confirms one frozen observational group. Import also confirms
/// its protected credentials and exclusive reservations, together with ordinary outputs and receipt. External verification precedes this boundary.</summary>
public interface IResourceObservationAcceptance
{
    ValueTask<ResourceObservationAcceptanceResult> CompleteAsync(ResourceObservationAcceptance acceptance, DateTimeOffset at,
        CancellationToken cancellationToken);
    ValueTask<ResourceObservationReceipt?> FindAsync(DurableOperationKey operation, CancellationToken cancellationToken);
}
