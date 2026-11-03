using Haby.Domain;

namespace Haby.Application;

/// <summary>Non-secret, adapter-encoded evidence. Concrete fields remain adapter-owned.</summary>
public sealed record ExternalOperationOutput(string Contract, string Key, string Payload);

/// <summary>Explicit registration for an external producer requiring atomic evidence publication.</summary>
public interface IExternalOperationOutputContract
{
    DurableActionExecutionKey Producer { get; }
    bool Validate(DurableOperationNode node, ExternalOperationOutput output);
    /// <summary>Optional adapter-owned validation against the exact accepted context and pinned inputs.</summary>
    ValueTask<bool> ValidateAcceptedAsync(DurableOperationKey key, AcceptedOperation accepted,
        ExternalOperationOutput output, CancellationToken cancellationToken) => new(Validate(accepted.Node, output));
}

public sealed record AcceptedOperation(DurablePlanningInputs Inputs, DurableOperationGraph Graph,
    DurableOperationNode Node, NormalizedManifestSpec Spec);

/// <summary>Trusted catalog boundary; no current aliases, inferred evidence or external calls.</summary>
public interface IExternalOperationOutputs
{
    ValueTask<AcceptedOperation?> FindAcceptedAsync(DurableOperationKey key, CancellationToken cancellationToken);
    ValueTask<bool> CompleteAsync(DurableOperationKey producer, Guid attempt, ExternalOperationOutput output,
        DateTimeOffset at, CancellationToken cancellationToken);
    ValueTask<ExternalOperationOutput?> ConsumeAsync(DurableOperationKey consumer, OperationOutputRef selector,
        CancellationToken cancellationToken);
}
