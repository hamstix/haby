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
