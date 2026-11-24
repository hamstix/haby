using Haby.Domain;

namespace Haby.Application;

public enum SharedCredentialUse { SystemAccess, ApplicationDelivery }

/// <summary>Exact immutable scope. A SecretRef alone never authorizes a use.</summary>
public sealed record SharedCredentialScope(EnvironmentRecordId Environment, ProviderInstanceId Provider,
    ProviderConfigurationRevisionNumber Revision, string Slot, SharedCredentialUse Use, SecretRef Reference);
public sealed record SharedCredentialBinding(SharedCredentialScope Scope, string IssuedBy, DateTimeOffset IssuedAt,
    string? RevokedBy, DateTimeOffset? RevokedAt)
{
    public IReadOnlyList<SharedCredentialDependency> Dependencies { get; init; } = [];
}
/// <summary>Only immutable relations from actually created documents; no material or unmaterialized projections.</summary>
public sealed record SharedCredentialDependency(ConfigurationDocumentRevisionId Document, ConfigurationDocumentTarget Target,
    ApplicationRecordId Application, ManifestLocalId Resource, Guid Association, string Projection,
    ConfigurationFormatterId Formatter, ManifestLocalId FormatterImplementation, bool ActiveAssociation);
public enum SharedCredentialRevocationOutcome { Revoked, AlreadyRevoked, NotFound, InvalidRequest }

/// <summary>Trusted operator administration. No material, verifier or reactivation operation.</summary>
public interface ISharedCredentialAdministration
{
    ValueTask<IReadOnlyList<SharedCredentialBinding>> InspectAsync(EnvironmentRecordId environment,
        ProviderInstanceId provider, ProviderConfigurationRevisionNumber revision, CancellationToken token);
    ValueTask<SharedCredentialRevocationOutcome> RevokeAsync(SharedCredentialScope scope, string actor, CancellationToken token);
}

/// <summary>Adapter-only SystemAccess. Application delivery uses its separate document/association authorization pipeline.</summary>
public interface ISharedCredentialSystemAccess
{
    ValueTask<SecretMaterial?> ResolveAsync(SharedCredentialScope scope, int maximumBytes, CancellationToken token);
}
