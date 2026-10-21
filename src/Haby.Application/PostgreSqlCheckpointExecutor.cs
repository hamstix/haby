using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

public enum CheckpointExecutionState { Pending, Running, Succeeded, Failed, NeedsAttention }
public enum CheckpointExecutionFailure { None, UnsupportedShape, PinnedRevisionMissing, ConfigurationUnavailable, Conflict, PreDispatchFailure, UncertainOutcome }

/// <summary>Local observation only. It is not a durable operation contract.</summary>
public sealed record CheckpointExecutionStatus(CheckpointExecutionState State, CheckpointExecutionFailure Failure);

public enum CheckpointProvisionOutcome { Created, Conflict, ConfigurationUnavailable, PreDispatchFailure, UncertainOutcome }

/// <summary>The only external effect boundary needed for this checkpoint.</summary>
public interface ICheckpointDatabaseProvisioner
{
    ValueTask<CheckpointProvisionOutcome> CreateAsync(ProviderResolution exactProvider, string databaseName,
        CancellationToken cancellationToken);
}

/// <summary>Executes exactly one supported Resource after its desired-state generation was committed.</summary>
public sealed class PostgreSqlCheckpointExecutor(InMemoryDesiredStateStore desired, IManifestRevisionReader revisions,
    ICheckpointDatabaseProvisioner provisioner)
{
    private readonly object _gate = new();
    private readonly Dictionary<(ApplicationDeploymentId, DeploymentGeneration), CheckpointExecutionStatus> _statuses = [];

    public CheckpointExecutionStatus GetStatus(ApplicationDeploymentGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (!desired.Generations.Contains(generation))
            throw new ArgumentException("The generation has not been committed.", nameof(generation));
        lock (_gate) return _statuses.GetValueOrDefault((generation.DeploymentId, generation.Generation)) ??
            new(CheckpointExecutionState.Pending, CheckpointExecutionFailure.None);
    }

    public async ValueTask<CheckpointExecutionStatus> ExecuteAsync(ApplicationDeploymentGeneration generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (!desired.Generations.Contains(generation))
            throw new ArgumentException("The generation has not been committed.", nameof(generation));
        var key = (generation.DeploymentId, generation.Generation);
        lock (_gate)
        {
            var prior = _statuses.GetValueOrDefault(key);
            if (prior?.State is CheckpointExecutionState.Running or CheckpointExecutionState.Succeeded or
                CheckpointExecutionState.NeedsAttention) return prior;
            _statuses[key] = new(CheckpointExecutionState.Running, CheckpointExecutionFailure.None);
        }

        CheckpointExecutionStatus result;
        var dispatchEntered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = revisions.Find(generation.ManifestRevisionRef);
            if (revision is null)
                result = Failed(CheckpointExecutionFailure.PinnedRevisionMissing);
            else if (generation.Presence != DesiredPresence.Present ||
                     revision.Declaration.Spec.Resources.Count != 1 ||
                     revision.Declaration.Spec.Components.Count != 0 ||
                     revision.Declaration.Spec.Exports.Count != 0 ||
                     revision.Declaration.Spec.Parameters.Count != 0)
                result = Failed(CheckpointExecutionFailure.UnsupportedShape);
            else
            {
                var resource = revision.Declaration.Spec.Resources.Single().Value;
                if (resource.Type != PostgreSqlDatabaseContract.Id ||
                    resource.Properties.Members.Count != 1 ||
                    !resource.Properties.Members.TryGetValue("name", out var value) ||
                    value is not ConfigurationString name ||
                    generation.Inputs.Providers.Count != 1 ||
                    generation.Inputs.Providers[0].Alias != resource.ProviderRef)
                    result = Failed(CheckpointExecutionFailure.UnsupportedShape);
                else
                {
                    dispatchEntered = true;
                    var outcome = await provisioner.CreateAsync(generation.Inputs.Providers[0].Resolution,
                        name.Value, cancellationToken);
                    result = outcome switch
                    {
                        CheckpointProvisionOutcome.Created => new(CheckpointExecutionState.Succeeded, CheckpointExecutionFailure.None),
                        CheckpointProvisionOutcome.Conflict => Failed(CheckpointExecutionFailure.Conflict),
                        CheckpointProvisionOutcome.ConfigurationUnavailable => Failed(CheckpointExecutionFailure.ConfigurationUnavailable),
                        CheckpointProvisionOutcome.PreDispatchFailure => Failed(CheckpointExecutionFailure.PreDispatchFailure),
                        _ => new(CheckpointExecutionState.NeedsAttention, CheckpointExecutionFailure.UncertainOutcome)
                    };
                    if (result.State == CheckpointExecutionState.Succeeded && !desired.TryMarkApplied(generation))
                        result = new(CheckpointExecutionState.NeedsAttention, CheckpointExecutionFailure.UncertainOutcome);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate) _statuses[key] = dispatchEntered
                ? new(CheckpointExecutionState.NeedsAttention, CheckpointExecutionFailure.UncertainOutcome)
                : Failed(CheckpointExecutionFailure.PreDispatchFailure);
            throw new OperationCanceledException("Checkpoint execution canceled.", cancellationToken);
        }
        catch
        {
            result = dispatchEntered
                ? new(CheckpointExecutionState.NeedsAttention, CheckpointExecutionFailure.UncertainOutcome)
                : Failed(CheckpointExecutionFailure.PreDispatchFailure);
        }
        lock (_gate) _statuses[key] = result;
        return result;
    }

    private static CheckpointExecutionStatus Failed(CheckpointExecutionFailure failure) =>
        new(CheckpointExecutionState.Failed, failure);
}
