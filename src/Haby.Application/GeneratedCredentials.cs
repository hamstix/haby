using Haby.Domain;

namespace Haby.Application;

public enum GeneratedCredentialOutcome
{
    Created, Existing, InvalidOwner, ProfileConflict, MaterialUnavailable, StorageUnavailable
}

/// <summary>Ordinary results contain only an exact immutable reference, never material.</summary>
public sealed record GeneratedCredentialResult(GeneratedCredentialOutcome Outcome, SecretRef? Reference = null);

/// <summary>
/// Trusted internal application-code boundary, not public authorization. Requires existing catalog
/// Environment/Application IDs and a valid Resource declaration ID, without provisioning prerequisites.
/// Retains unused material; missing or inconsistent material never permits regeneration.
/// </summary>
public interface IGeneratedCredentialSlots
{
    ValueTask<GeneratedCredentialResult> GetOrCreateAsync(GeneratedCredentialSlot slot, string generationProfile,
        CancellationToken cancellationToken);
}
