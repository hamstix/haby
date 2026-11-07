using ApplicationId = Haby.Domain.ApplicationId;
using Haby.Domain;

namespace Haby.Application;

public sealed record ConfigurationDocumentTarget(ApplicationDeploymentId Deployment, DeploymentGeneration Generation,
    ManifestLocalId Component, ManifestLocalId Document);

public sealed record ConfigurationDocumentSelection(EnvironmentId Environment, ApplicationId Application,
    ManifestLocalId Component, ManifestLocalId Document, DeploymentGeneration? Generation);

public enum ConfigurationDocumentStatus { Created, Existing, Found, NotFound, InvalidRequest, NotApplied, InputUnavailable, IntegrityConflict, LimitExceeded }

public sealed record ConfigurationDocumentMetadata(ConfigurationDocumentRevisionId Id, ConfigurationDocumentTarget Target,
    EnvironmentId Environment, ApplicationId Application, ManifestRevisionRef Revision, DeploymentPlanId Plan,
    string FileName, string Format, string Serializer, string Fingerprint, string FrozenInputFingerprint,
    string Actor, DateTimeOffset CreatedAt);

/// <summary>Protected internal artifact; never serialize this object to an operator response.</summary>
public sealed record ConfigurationDocumentRevision(ConfigurationDocumentMetadata Metadata, string FrozenInputs,
    string OutputAssociations, ConfigurationValue Tree)
{
    public override string ToString() => "[Protected configuration revision]";
}

public sealed record ConfigurationDocumentResult(ConfigurationDocumentStatus Status,
    ConfigurationDocumentRevision? Revision = null, ConfigurationDocumentTarget? Target = null);

/// <summary>Selection resolves exactly once. Subsequent operations accept only the resolved identity.</summary>
public interface IConfigurationDocumentStore
{
    ValueTask<ConfigurationDocumentResult> SelectAsync(ConfigurationDocumentSelection selection, CancellationToken token);
    ValueTask<ConfigurationDocumentResult> ReadAsync(ConfigurationDocumentTarget target, CancellationToken token);
    ValueTask<ConfigurationDocumentResult> MaterializeAsync(ConfigurationDocumentTarget target, string actor, CancellationToken token);
}
