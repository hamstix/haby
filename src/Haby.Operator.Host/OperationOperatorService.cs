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
            Status = result.Outcome switch
            {
                DurableCreateExecutionOutcome.Succeeded => OperationStatus.Succeeded,
                DurableCreateExecutionOutcome.Failed => OperationStatus.Failed,
                DurableCreateExecutionOutcome.RetryableFailure => OperationStatus.RetryableFailure,
                DurableCreateExecutionOutcome.NeedsAttention => OperationStatus.NeedsAttention,
                DurableCreateExecutionOutcome.OperationNotFound => OperationStatus.OperationNotFound,
                DurableCreateExecutionOutcome.DispatchUncertain => OperationStatus.DispatchUncertain,
                _ => throw new InvalidOperationException("Unknown execution outcome.")
            },
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
                Outcome = attempt.Outcome switch
                {
                    null => OperationAttemptOutcome.Unspecified,
                    DurableCreateAttemptOutcome.Succeeded => OperationAttemptOutcome.Succeeded,
                    DurableCreateAttemptOutcome.Conflict => OperationAttemptOutcome.Conflict,
                    DurableCreateAttemptOutcome.ServerRejected => OperationAttemptOutcome.ServerRejected,
                    DurableCreateAttemptOutcome.Uncertain => OperationAttemptOutcome.Uncertain,
                    DurableCreateAttemptOutcome.CanceledBeforeDispatch => OperationAttemptOutcome.CanceledBeforeDispatch,
                    DurableCreateAttemptOutcome.AbandonedBeforeDispatch => OperationAttemptOutcome.AbandonedBeforeDispatch,
                    DurableCreateAttemptOutcome.FailedBeforeDispatch => OperationAttemptOutcome.FailedBeforeDispatch,
                    _ => throw new InvalidOperationException("Unknown attempt outcome.")
                }
            };
            if (attempt.DispatchedAt is { } dispatched) item.DispatchedAtUtc = Utc(dispatched);
            if (attempt.FinishedAt is { } finished) item.FinishedAtUtc = Utc(finished);
            reply.Attempts.Add(item);
        }
        return reply;
    }

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

    private static OperationStatus OperationKey(OperationRequest request, out DurableCreateOperationKey? key)
    {
        key = null;
        try
        {
            key = new DurableCreateOperationKey(ApplicationDeploymentId.Parse(request.DeploymentId),
                DeploymentGeneration.Create(request.Generation), ManifestLocalId.Create(request.ResourceId),
                DurableOperationAction.Create);
        }
        catch (ArgumentException) { return OperationStatus.InvalidRequest; }
        if (request.Action == OperationAction.Unspecified) return OperationStatus.InvalidRequest;
        if (request.Action != OperationAction.Create) return OperationStatus.UnsupportedAction;
        if (request.Generation != 1) return OperationStatus.UnsupportedShape;
        return OperationStatus.Unspecified;
    }

    private static DurableOperationSummary Summary(DurableCreateOperation value)
    {
        var summary = new DurableOperationSummary
        {
            Key = new OperationRequest
            {
                DeploymentId = value.Key.DeploymentId.ToString(),
                Generation = value.Key.Generation.Value,
                ResourceId = value.Key.ResourceId.Value,
                Action = OperationAction.Create
            },
            State = value.State switch
            {
                DurableCreateOperationState.Pending => OperationState.Pending,
                DurableCreateOperationState.Preparing => OperationState.Preparing,
                DurableCreateOperationState.Dispatching => OperationState.Dispatching,
                DurableCreateOperationState.Succeeded => OperationState.Succeeded,
                DurableCreateOperationState.Failed => OperationState.Failed,
                DurableCreateOperationState.NeedsAttention => OperationState.NeedsAttention,
                _ => throw new InvalidOperationException("Unknown operation state.")
            },
            Reason = value.Reason switch
            {
                null => OperationReason.Unspecified,
                DurableCreateOperationReason.ProviderInputUnavailable => OperationReason.ProviderInputUnavailable,
                DurableCreateOperationReason.SecretUnavailable => OperationReason.SecretUnavailable,
                DurableCreateOperationReason.UnsupportedShape => OperationReason.UnsupportedShape,
                DurableCreateOperationReason.DatabaseNameInvalid => OperationReason.DatabaseNameInvalid,
                DurableCreateOperationReason.Connectivity => OperationReason.Connectivity,
                DurableCreateOperationReason.Conflict => OperationReason.Conflict,
                DurableCreateOperationReason.ServerRejected => OperationReason.ServerRejected,
                DurableCreateOperationReason.UncertainAfterDispatch => OperationReason.UncertainAfterDispatch,
                _ => throw new InvalidOperationException("Unknown operation reason.")
            },
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
