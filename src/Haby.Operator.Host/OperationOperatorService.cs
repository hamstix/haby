using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<OperationReply> ExecuteOperation(OperationRequest request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var validation = OperationKey(request, out var key);
        if (validation != OperationStatus.Unspecified) return new OperationReply { Status = validation };
        // The singleton executor alone owns serialization, the dispatch barrier and retry decisions.
        var result = await OperationCall(() => executor.ExecuteAsync(key!, context.CancellationToken), context);
        return new OperationReply
        {
            Status = ExecutionStatus(result.Outcome),
            Operation = result.Operation is { } operation ? Summary(operation) : null
        };
    }

    public override async Task<OperationReply> GetOperation(OperationRequest request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var validation = OperationKey(request, out var key);
        if (validation != OperationStatus.Unspecified) return new OperationReply { Status = validation };
        var view = await OperationCall(() => executor.ObserveAsync(key!, context.CancellationToken), context);
        if (view is null) return new OperationReply { Status = OperationStatus.OperationNotFound };
        var reply = new OperationReply { Status = OperationStatus.Found, Operation = Summary(view.Operation) };
        foreach (var attempt in view.Attempts)
        {
            var item = new OperationAttempt
            {
                AttemptId = attempt.AttemptId.ToString("D"),
                AttemptNumber = attempt.AttemptNumber,
                StartedAtUtc = Utc(attempt.StartedAt),
                Outcome = attempt.Outcome is { } outcome ? AttemptOutcome(outcome) : OperationAttemptOutcome.Unspecified
            };
            if (attempt.DispatchedAt is { } dispatched) item.DispatchedAtUtc = Utc(dispatched);
            if (attempt.FinishedAt is { } finished) item.FinishedAtUtc = Utc(finished);
            reply.Attempts.Add(item);
        }
        return reply;
    }

    private static OperationAttemptOutcome AttemptOutcome(DurableAttemptOutcome outcome) => outcome switch
    {
        DurableAttemptOutcome.Succeeded => OperationAttemptOutcome.Succeeded,
        DurableAttemptOutcome.Conflict => OperationAttemptOutcome.Conflict,
        DurableAttemptOutcome.ServerRejected => OperationAttemptOutcome.ServerRejected,
        DurableAttemptOutcome.Uncertain => OperationAttemptOutcome.Uncertain,
        DurableAttemptOutcome.CanceledBeforeDispatch => OperationAttemptOutcome.CanceledBeforeDispatch,
        DurableAttemptOutcome.AbandonedBeforeDispatch => OperationAttemptOutcome.AbandonedBeforeDispatch,
        DurableAttemptOutcome.FailedBeforeDispatch => OperationAttemptOutcome.FailedBeforeDispatch,
        _ => throw new InvalidOperationException("Unknown attempt outcome.")
    };

    private static OperationStatus ExecutionStatus(DurableExecutionOutcome outcome) => outcome switch
    {
        DurableExecutionOutcome.Blocked => OperationStatus.Blocked,
        DurableExecutionOutcome.InputUnavailable => OperationStatus.InputUnavailable,
        DurableExecutionOutcome.HandlerUnavailable => OperationStatus.HandlerUnavailable,
        DurableExecutionOutcome.Succeeded => OperationStatus.Succeeded,
        DurableExecutionOutcome.Failed => OperationStatus.Failed,
        DurableExecutionOutcome.RetryableFailure => OperationStatus.RetryableFailure,
        DurableExecutionOutcome.NeedsAttention => OperationStatus.NeedsAttention,
        DurableExecutionOutcome.OperationNotFound => OperationStatus.OperationNotFound,
        DurableExecutionOutcome.DispatchUncertain => OperationStatus.DispatchUncertain,
        _ => throw new InvalidOperationException("Unknown execution outcome.")
    };

    private static async ValueTask<T> OperationCall<T>(Func<ValueTask<T>> call, ServerCallContext context)
    {
        try { return await call(); }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A journal/transport failure says nothing about whether the external effect occurred.
            // Do not let the gRPC framework log a raw driver exception from credential-dependent work.
            throw new RpcException(new Status(StatusCode.Internal,
                "Registry operation unavailable. Inspect the durable operation before retrying."));
        }
    }

    private static OperationStatus OperationKey(OperationRequest request, out DurableOperationKey? key)
    {
        key = null;
        try
        {
            key = new DurableOperationKey(ApplicationDeploymentId.Parse(request.DeploymentId),
                DeploymentGeneration.Create(request.Generation), OperationId.Create(request.OperationId));
        }
        catch (ArgumentException) { return OperationStatus.InvalidRequest; }
        if (request.Generation != 1) return OperationStatus.UnsupportedShape;
        return OperationStatus.Unspecified;
    }

    private static DurableOperationSummary Summary(DurableOperation value)
    {
        var summary = new DurableOperationSummary
        {
            Key = new OperationRequest
            {
                DeploymentId = value.Key.DeploymentId.ToString(),
                Generation = value.Key.Generation.Value,
                OperationId = value.Key.OperationId.Value
            },
            State = value.State switch
            {
                DurableOperationState.Pending => OperationState.Pending,
                DurableOperationState.Preparing => OperationState.Preparing,
                DurableOperationState.Dispatching => OperationState.Dispatching,
                DurableOperationState.Succeeded => OperationState.Succeeded,
                DurableOperationState.Failed => OperationState.Failed,
                DurableOperationState.NeedsAttention => OperationState.NeedsAttention,
                _ => throw new InvalidOperationException("Unknown operation state.")
            },
            Reason = value.Reason switch
            {
                null => OperationReason.Unspecified,
                DurableOperationReason.InputInvalid => OperationReason.InputInvalid,
                DurableOperationReason.InputUnavailable => OperationReason.InputUnavailable,
                DurableOperationReason.HandlerUnavailable => OperationReason.HandlerUnavailable,
                DurableOperationReason.SecretUnavailable => OperationReason.SecretUnavailable,
                DurableOperationReason.Connectivity => OperationReason.Connectivity,
                DurableOperationReason.Conflict => OperationReason.Conflict,
                DurableOperationReason.ServerRejected => OperationReason.ServerRejected,
                DurableOperationReason.UncertainAfterDispatch => OperationReason.UncertainAfterDispatch,
                _ => throw new InvalidOperationException("Unknown operation reason.")
            },
            ResourceId = value.Node.ResourceId.Value,
            ActionContract = value.Node.Action.ActionContract.Value,
            EnvironmentRecordId = value.Environment.ToString(),
            ApplicationRecordId = value.Application.ToString(),
            Reference = Reference(value.Revision),
            ProviderInstanceId = value.ProviderInstance.ToString(),
            ConfigurationRevision = value.ConfigurationRevision.Value,
            ImplementationId = value.ImplementationId,
            CreatedAtUtc = Utc(value.CreatedAt),
            AttemptNumber = value.AttemptNumber
        };
        if (value.UpdatedAt is { } updated) summary.UpdatedAtUtc = Utc(updated);
        if (value.AttemptId is { } attempt) summary.AttemptId = attempt.ToString("D");
        if (value.DispatchedAt is { } dispatched) summary.DispatchedAtUtc = Utc(dispatched);
        if (value.FinalizedAt is { } finalized) summary.FinalizedAtUtc = Utc(finalized);
        return summary;
    }
}
