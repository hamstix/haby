using Haby.Domain;

namespace Haby.Application;

/// <summary>The exact identity of one pilot Resource action in an accepted generation.</summary>
public enum DurableOperationAction { Create }

public sealed record DurableCreateOperationKey(ApplicationDeploymentId DeploymentId,
    DeploymentGeneration Generation, ManifestLocalId ResourceId, DurableOperationAction Action);

/// <summary>Pending means no attempt has crossed the dispatch barrier.</summary>
public enum DurableCreateOperationState { Pending }

/// <summary>Non-secret operation intent. Provider inputs come from the immutable generation.</summary>
public sealed record DurableCreateOperation(DurableCreateOperationKey Key,
    DurableCreateOperationState State, ProviderInstanceId ProviderInstance,
    ProviderConfigurationRevisionNumber ConfigurationRevision, string ImplementationId,
    DateTimeOffset CreatedAt);

/// <summary>Read-only pilot operation query; it never resolves a current Provider alias.</summary>
public interface IDurableCreateOperationQuery
{
    ValueTask<DurableCreateOperation?> FindAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken);
}
