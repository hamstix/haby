using Haby.Domain;

namespace Haby.Application;

/// <summary>Direct original-source provenance. No recursive generation-chain lookup.</summary>
public sealed record ResourceReuse(ManifestLocalId Resource, Guid ManagementId,
    long SourceGeneration, string EvidenceFingerprint);

public sealed record ResourceReuseBaseline(ApplicationDeploymentId Deployment, long Generation, string Fingerprint);

/// <summary>Pure, exact adapter registration; common layers do not interpret Resource properties.</summary>
public interface IDurableResourceReusePolicy
{
    bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target,
        ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration);
}
