using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliRecoveryPreview(CliOperation Operation, string TargetContract, string? TargetName,
    string? UncertainAttemptId);
internal sealed record CliRecoveryReceipt(string DecisionId, CliOperationKey Key, string AttemptId, string Reason,
    string TechnicalActor, string DecidedAtUtc);
internal sealed record CliRecoveryData(CliRecoveryPreview? Preview, CliRecoveryReceipt? Receipt);

internal static class RecoveryPresentation
{
    internal static string Outcome(RecoveryStatus status) => status switch
    {
        RecoveryStatus.Found => "found",
        RecoveryStatus.Assigned => "assigned",
        RecoveryStatus.AlreadyAssigned => "alreadyAssigned",
        RecoveryStatus.Rejected => "rejected",
        RecoveryStatus.TargetAbsent => "targetAbsent",
        RecoveryStatus.TargetUnavailable => "targetUnavailable",
        RecoveryStatus.OperationNotFound => "operationNotFound",
        RecoveryStatus.InvalidRequest => "invalidRequest",
        RecoveryStatus.UnsupportedRecoveryAction => "unsupportedRecoveryAction",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static int Exit(RecoveryReply reply)
    {
        _ = Outcome(reply.Status);
        if (reply.Status == RecoveryStatus.Found && reply.Preview?.Operation is null ||
            reply.Status is RecoveryStatus.Assigned or RecoveryStatus.AlreadyAssigned && reply.Receipt is null)
            throw new InvalidDataException("Incomplete recovery result.");
        return reply.Status is RecoveryStatus.Found or RecoveryStatus.Assigned or RecoveryStatus.AlreadyAssigned ? 0 : 1;
    }

    internal static CliRecoveryData Data(RecoveryReply reply)
    {
        var preview = reply.Preview;
        var receipt = reply.Receipt;
        return new(preview is null ? null : new(
            OperationPresentation.Data(new OperationReply { Status = OperationStatus.Found, Operation = preview.Operation }).Operation!,
            preview.TargetContract, preview.HasTargetName ? preview.TargetName : null,
            preview.HasUncertainAttemptId ? preview.UncertainAttemptId : null),
            receipt is null ? null : new(receipt.DecisionId,
                receipt.Key is { Action: OperationAction.Create } key ? new(key.DeploymentId, key.Generation, key.ResourceId, "create")
                    : throw new InvalidDataException("Unsupported operation identity."),
                receipt.AttemptId, receipt.Reason == RecoveryReason.OperatorAcceptedExistingDatabase ? "operatorAcceptedExistingDatabase"
                    : throw new InvalidDataException("Unknown recovery reason."), receipt.TechnicalActor, receipt.DecidedAtUtc));
    }
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintRecovery(string command, RecoveryReply reply)
    {
        var exit = RecoveryPresentation.Exit(reply);
        Write(command, RecoveryPresentation.Outcome(reply.Status), RecoveryPresentation.Data(reply), NoDiagnostics,
            CliOutputJsonContext.Default.CliEnvelopeCliRecoveryData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal int PrintRecovery(string command, RecoveryReply reply)
    {
        var exit = RecoveryPresentation.Exit(reply);
        var data = RecoveryPresentation.Data(reply);
        _output.WriteLine(RecoveryPresentation.Outcome(reply.Status));
        if (data.Preview is { } p)
        {
            var o = p.Operation;
            _output.WriteLine($"Operation: {o.Key.DeploymentId} generation {o.Key.Generation}; Resource: {o.Key.ResourceId}; action: {o.Key.Action}");
            _output.WriteLine($"Environment record: {o.EnvironmentRecordId}; Application: {o.ApplicationRecordId}");
            _output.WriteLine($"Pinned Provider: {o.ProviderInstanceId} revision {o.ConfigurationRevision}; implementation: {o.ImplementationId}");
            _output.WriteLine($"Target contract: {p.TargetContract}; target: {p.TargetName ?? "(unavailable)"}");
            _output.WriteLine($"State: {o.State}; reason: {o.Reason ?? "(none)"}; uncertain attempt: {p.UncertainAttemptId ?? "(none)"}");
            _output.WriteLine("Assignment accepts provenance responsibility; existence alone does not prove ownership. No Create is dispatched.");
        }
        if (data.Receipt is { } r)
            _output.WriteLine($"Decision: {r.DecisionId}; attempt: {r.AttemptId}; reason: {r.Reason}; technical actor: {r.TechnicalActor}; decided UTC: {r.DecidedAtUtc}");
        return exit;
    }
}
