using Haby.Domain;

namespace Haby.Application;

public enum SharedCredentialUse { SystemAccess, ApplicationDelivery }

/// <summary>Exact immutable scope. A SecretRef alone never authorizes a use.</summary>
public sealed record SharedCredentialScope(EnvironmentRecordId Environment, ProviderInstanceId Provider,
    ProviderConfigurationRevisionNumber Revision, string Slot, SharedCredentialUse Use, SecretRef Reference);
public sealed record SharedCredentialBinding(SharedCredentialScope Scope, string IssuedBy, DateTimeOffset IssuedAt,
    string? RevokedBy, DateTimeOffset? RevokedAt);
public enum SharedCredentialRevocationOutcome { Revoked, AlreadyRevoked, NotFound, InvalidRequest }

/// <summary>Trusted operator administration. No material, verifier or reactivation operation.</summary>
public interface ISharedCredentialAdministration
{
    ValueTask<IReadOnlyList<SharedCredentialBinding>> InspectAsync(EnvironmentRecordId environment,
        ProviderInstanceId provider, ProviderConfigurationRevisionNumber revision, CancellationToken token);
    ValueTask<SharedCredentialRevocationOutcome> RevokeAsync(SharedCredentialScope scope, string actor, CancellationToken token);
}

/// <summary>Adapter-only SystemAccess. Application delivery remains closed until its own authorization pipeline exists.</summary>
public interface ISharedCredentialSystemAccess
{
    ValueTask<SecretMaterial?> ResolveAsync(SharedCredentialScope scope, int maximumBytes, CancellationToken token);
}
