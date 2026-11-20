using Haby.Domain;

namespace Haby.Application;

public enum ApplicationCredentialProvenance { Generated, Imported }
public sealed record ConfirmedApplicationCredential(ManagedResourceKey Owner, SecretRef Reference,
    ApplicationCredentialProvenance Provenance);
public sealed record ProtectedCredentialReference(Guid Id, SecretRef Reference);
public enum ProtectedCredentialStageStatus { Staged, Existing, Conflict, InvalidInput, StorageUnavailable }
public sealed record ProtectedCredentialStageResult(ProtectedCredentialStageStatus Status,
    ProtectedCredentialReference? Reference = null);

/// <summary>Trusted internal intake, not an authorization or comparison endpoint. Material is a
/// separate argument and never enters serializable intent. Request identity and owner are immutable.</summary>
public interface IProtectedApplicationCredentialIntake
{
    ValueTask<ProtectedCredentialStageResult> StageAsync(Guid requestId, ManagedResourceKey owner,
        SecretMaterial material, CancellationToken cancellationToken);
    /// <summary>Private verification access; an unconfirmed reference grants no consumer delivery.</summary>
    ValueTask<SecretMaterial?> ResolveStagedAsync(ProtectedCredentialReference reference,
        ManagedResourceKey owner, CancellationToken cancellationToken);
}

/// <summary>Confirmed exact Resource credential, independently of how its material was acquired.</summary>
public interface IApplicationCredentialReader
{
    ValueTask<ConfirmedApplicationCredential?> FindAsync(ManagedResourceKey owner, SecretRef reference,
        CancellationToken cancellationToken);
}

public sealed record ImportOperationConfirmation(DurableOperationKey Key, Guid AttemptId, ExternalOperationOutput Output);
public sealed record ImportCredentialConfirmation(ManagedResourceKey Owner, ProtectedCredentialReference Staged);
public sealed record ResourceImportAcceptance(IReadOnlyList<ImportOperationConfirmation> Operations,
    IReadOnlyList<ImportCredentialConfirmation> Credentials, IReadOnlyList<ProviderResourceReservation> Reservations,
    DurableActionPayload Evidence);
public sealed record ResourceImportReceipt(Guid Id, DurableActionPayload Evidence,
    IReadOnlyList<ImportOperationConfirmation> Operations);
public enum ResourceImportAcceptanceStatus { Accepted, Existing, Conflict, InputUnavailable, StorageUnavailable }
public sealed record ResourceImportAcceptanceResult(ResourceImportAcceptanceStatus Status)
{
    public bool IsSuccess => Status is ResourceImportAcceptanceStatus.Accepted or ResourceImportAcceptanceStatus.Existing;
}

/// <summary>One catalog transaction confirms the entire accepted import graph, credentials,
/// reservations, outputs and receipt. External verification precedes this boundary.</summary>
public interface IResourceImportAcceptance
{
    ValueTask<ResourceImportAcceptanceResult> CompleteAsync(ResourceImportAcceptance acceptance, DateTimeOffset at,
        CancellationToken cancellationToken);
    ValueTask<ResourceImportReceipt?> FindAsync(DurableOperationKey operation, CancellationToken cancellationToken);
}
