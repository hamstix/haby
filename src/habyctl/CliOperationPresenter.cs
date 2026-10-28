using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliOperationKey(string DeploymentId, long Generation, string ResourceId, string Action);
internal sealed record CliOperation(CliOperationKey Key, string State, string? Reason,
    string EnvironmentRecordId, string ApplicationRecordId, CliReference Reference, string ProviderInstanceId,
    long ConfigurationRevision, string ImplementationId, string CreatedAtUtc, string? UpdatedAtUtc,
    string? AttemptId, long AttemptNumber, string? DispatchedAtUtc, string? FinalizedAtUtc);
internal sealed record CliOperationAttempt(string AttemptId, long AttemptNumber, string StartedAtUtc,
    string? DispatchedAtUtc, string? FinishedAtUtc, string? Outcome);
internal sealed record CliOperationData(CliOperation? Operation, CliOperationAttempt[] Attempts);

internal static class OperationPresentation
{
    internal static string Outcome(OperationStatus value) => value switch
    {
        OperationStatus.Found => "found",
        OperationStatus.Succeeded => "succeeded",
        OperationStatus.Failed => "failed",
        OperationStatus.RetryableFailure => "retryableFailure",
        OperationStatus.NeedsAttention => "needsAttention",
        OperationStatus.OperationNotFound => "operationNotFound",
        OperationStatus.DispatchUncertain => "dispatchUncertain",
        OperationStatus.InvalidRequest => "invalidRequest",
        OperationStatus.UnsupportedAction => "unsupportedAction",
        OperationStatus.UnsupportedShape => "unsupportedShape",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static string State(OperationState value) => value switch
    {
        OperationState.Pending => "pending",
        OperationState.Preparing => "preparing",
        OperationState.Dispatching => "dispatching",
        OperationState.Succeeded => "succeeded",
        OperationState.Failed => "failed",
        OperationState.NeedsAttention => "needsAttention",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static string? Reason(OperationReason value) => value switch
    {
        OperationReason.Unspecified => null,
        OperationReason.ProviderInputUnavailable => "providerInputUnavailable",
        OperationReason.SecretUnavailable => "secretUnavailable",
        OperationReason.UnsupportedShape => "unsupportedShape",
        OperationReason.DatabaseNameInvalid => "databaseNameInvalid",
        OperationReason.Connectivity => "connectivity",
        OperationReason.Conflict => "conflict",
        OperationReason.ServerRejected => "serverRejected",
        OperationReason.UncertainAfterDispatch => "uncertainAfterDispatch",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static string? AttemptOutcome(OperationAttemptOutcome value) => value switch
    {
        OperationAttemptOutcome.Unspecified => null,
        OperationAttemptOutcome.Succeeded => "succeeded",
        OperationAttemptOutcome.Conflict => "conflict",
        OperationAttemptOutcome.ServerRejected => "serverRejected",
        OperationAttemptOutcome.Uncertain => "uncertain",
        OperationAttemptOutcome.CanceledBeforeDispatch => "canceledBeforeDispatch",
        OperationAttemptOutcome.AbandonedBeforeDispatch => "abandonedBeforeDispatch",
        OperationAttemptOutcome.FailedBeforeDispatch => "failedBeforeDispatch",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static int Exit(OperationReply reply)
    {
        _ = Outcome(reply.Status);
        if (reply.Status is OperationStatus.Found or OperationStatus.Succeeded or OperationStatus.Failed
                or OperationStatus.RetryableFailure or OperationStatus.NeedsAttention && reply.Operation is null)
            throw new InvalidDataException("Missing operation snapshot.");
        return reply.Status is OperationStatus.Found or OperationStatus.Succeeded ? CliApplication.Success : CliApplication.Rejected;
    }

    internal static CliOperationData Data(OperationReply reply) => new(
        reply.Operation is not { } o ? null : new(
            o.Key is { Action: OperationAction.Create } k ? new(k.DeploymentId, k.Generation, k.ResourceId, "create")
                : throw new InvalidDataException("Unsupported operation identity."),
            State(o.State), Reason(o.Reason), o.EnvironmentRecordId, o.ApplicationRecordId,
            o.Reference is { } r ? new(r.ApplicationRecordId, r.RevisionNumber) : throw new InvalidDataException("Missing revision."),
            o.ProviderInstanceId, o.ConfigurationRevision, o.ImplementationId, o.CreatedAtUtc,
            o.HasUpdatedAtUtc ? o.UpdatedAtUtc : null, o.HasAttemptId ? o.AttemptId : null, o.AttemptNumber,
            o.HasDispatchedAtUtc ? o.DispatchedAtUtc : null, o.HasFinalizedAtUtc ? o.FinalizedAtUtc : null),
        [.. reply.Attempts.Select(a => new CliOperationAttempt(a.AttemptId, a.AttemptNumber, a.StartedAtUtc,
            a.HasDispatchedAtUtc ? a.DispatchedAtUtc : null, a.HasFinishedAtUtc ? a.FinishedAtUtc : null, AttemptOutcome(a.Outcome)))]);
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintOperation(string command, OperationReply reply)
    {
        var exit = OperationPresentation.Exit(reply);
        Write(command, OperationPresentation.Outcome(reply.Status), OperationPresentation.Data(reply), NoDiagnostics,
            CliOutputJsonContext.Default.CliEnvelopeCliOperationData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal int PrintOperation(string command, OperationReply reply)
    {
        var exit = OperationPresentation.Exit(reply);
        var data = OperationPresentation.Data(reply);
        _output.WriteLine(OperationPresentation.Outcome(reply.Status));
        if (data.Operation is { } o)
        {
            _output.WriteLine($"Operation: {o.Key.DeploymentId} generation {o.Key.Generation}; Resource: {o.Key.ResourceId}; action: {o.Key.Action}");
            _output.WriteLine($"State: {o.State}; reason: {o.Reason ?? "(none)"}");
            _output.WriteLine($"Environment record: {o.EnvironmentRecordId}; Application: {o.ApplicationRecordId}");
            _output.WriteLine($"Revision: {o.Reference.ApplicationRecordId} {o.Reference.RevisionNumber}");
            _output.WriteLine($"Pinned Provider: {o.ProviderInstanceId} revision {o.ConfigurationRevision}; implementation: {o.ImplementationId}");
            _output.WriteLine($"Created UTC: {o.CreatedAtUtc}; updated UTC: {o.UpdatedAtUtc ?? "(none)"}");
            _output.WriteLine($"Attempt: {o.AttemptId ?? "(none)"}; number: {o.AttemptNumber}; dispatched UTC: {o.DispatchedAtUtc ?? "(none)"}; finalized UTC: {o.FinalizedAtUtc ?? "(none)"}");
        }
        foreach (var a in data.Attempts)
            _output.WriteLine($"Attempt {a.AttemptNumber}: {a.AttemptId}; started UTC: {a.StartedAtUtc}; dispatched UTC: {a.DispatchedAtUtc ?? "(none)"}; finished UTC: {a.FinishedAtUtc ?? "(none)"}; outcome: {a.Outcome ?? "(unfinished)"}");
        if (reply.Status is OperationStatus.DispatchUncertain or OperationStatus.NeedsAttention ||
            reply.Operation?.State is OperationState.Dispatching or OperationState.NeedsAttention)
            _output.WriteLine("Outcome uncertain. A separate explicit manual recovery workflow is required; do not blindly retry.");
        return exit;
    }
}
