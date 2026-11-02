using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Contracts;
using Haby.PostgreSql;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override Task<RecoveryReply> InspectOperationRecovery(OperationRequest request, ServerCallContext context) =>
        RecoveryCall(request, null, context);

    public override Task<RecoveryReply> AssignExistingOperation(AssignExistingOperationRequest request,
        ServerCallContext context) => RecoveryCall(request.Key, request, context);

    private async Task<RecoveryReply> RecoveryCall(OperationRequest? request,
        AssignExistingOperationRequest? assignment, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (request is null) return new() { Status = RecoveryStatus.InvalidRequest };
        var validation = OperationKey(request, out var key);
        if (validation != OperationStatus.Unspecified)
            return new()
            {
                Status = validation == OperationStatus.InvalidRequest
                ? RecoveryStatus.InvalidRequest : RecoveryStatus.UnsupportedRecoveryAction
            };
        return await OperationCall(async () =>
        {
            try { return await recovery.RecoverAsync(key!, assignment, context.CancellationToken); }
            catch (ArgumentException) { return new RecoveryReply { Status = RecoveryStatus.InvalidRequest }; }
        }, context);
    }

    internal static DurableOperationSummary RecoveryOperationSummary(DurableOperation operation) => Summary(operation);
}

/// <summary>Explicit compile-time dispatch for the alpha surface; no alias resolution or Provider fallback.</summary>
public sealed class OperationRecoveryAdapter(IDurableOperationQuery operations,
    PostgreSqlOperationRecoveryAdapter postgreSql)
{
    public async ValueTask<RecoveryReply> RecoverAsync(DurableOperationKey key,
        AssignExistingOperationRequest? assignment, CancellationToken cancellationToken)
    {
        var operation = await operations.FindAsync(key, cancellationToken);
        if (operation is null) return new() { Status = RecoveryStatus.OperationNotFound };
        if (operation.Node.Action != PostgreSqlDatabaseCreateAction.RegistrationKey ||
            operation.ImplementationId != PostgreSqlDatabaseCheckpointCapability.ImplementationId)
            return new() { Status = RecoveryStatus.UnsupportedRecoveryAction };
        return assignment is null
            ? await postgreSql.InspectAsync(operation, cancellationToken)
            : await postgreSql.AssignAsync(key, assignment, cancellationToken);
    }
}

/// <summary>Only this adapter knows PostgreSQL recovery semantics. The application owns every decision.</summary>
public sealed class PostgreSqlOperationRecoveryAdapter(IPostgreSqlDatabaseRecovery recovery,
    IPostgreSqlDatabaseRecoveryJournal decisions)
{
    public async ValueTask<bool> HasReceiptAsync(DurableOperation operation, CancellationToken cancellationToken) =>
        operation.State == DurableOperationState.Succeeded && operation.AttemptId is { } attempt &&
        await decisions.FindDecisionAsync(operation.Key, attempt, cancellationToken) is not null;

    public async ValueTask<RecoveryReply> InspectAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        var preview = await recovery.InspectAsync(operation.Key, cancellationToken);
        if (preview is null) return new() { Status = RecoveryStatus.OperationNotFound };
        var reply = new RecoveryReply
        {
            Status = RecoveryStatus.Found,
            Preview = new RecoveryPreview
            {
                Operation = ManifestOperatorService.RecoveryOperationSummary(preview.Snapshot with { Reason = preview.Reason }),
                TargetContract = PostgreSqlDatabaseContract.Id.Value
            }
        };
        if (preview.DatabaseName is { } name) reply.Preview.TargetName = name;
        if (preview.UncertainAttemptId is { } uncertain) reply.Preview.UncertainAttemptId = uncertain.ToString("D");
        // Project only the immutable value-free receipt. The protected note has no wire field.
        if (preview.Snapshot.AttemptId is { } attempt &&
            await decisions.FindDecisionAsync(operation.Key, attempt, cancellationToken) is { } audit)
            reply.Receipt = Receipt(audit.Receipt);
        return reply;
    }

    public async ValueTask<RecoveryReply> AssignAsync(DurableOperationKey key,
        AssignExistingOperationRequest request, CancellationToken cancellationToken)
    {
        if (request.Reason != RecoveryReason.OperatorAcceptedExistingDatabase ||
            !Guid.TryParseExact(request.ExpectedAttemptId, "D", out var attempt) || attempt == Guid.Empty)
            return new() { Status = RecoveryStatus.InvalidRequest };
        var result = await recovery.AssignExistingDatabaseAsync(new AssignExistingPostgreSqlDatabase(key,
            EnvironmentRecordId.Parse(request.EnvironmentRecordId), attempt, request.Acknowledged,
            PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, request.Note, OperatorHostApplication.TechnicalActorId),
            cancellationToken);
        return new()
        {
            Status = result.Outcome switch
            {
                PostgreSqlDatabaseAssignmentOutcome.Assigned => RecoveryStatus.Assigned,
                PostgreSqlDatabaseAssignmentOutcome.AlreadyAssigned => RecoveryStatus.AlreadyAssigned,
                PostgreSqlDatabaseAssignmentOutcome.Rejected => RecoveryStatus.Rejected,
                PostgreSqlDatabaseAssignmentOutcome.DatabaseAbsent => RecoveryStatus.TargetAbsent,
                PostgreSqlDatabaseAssignmentOutcome.TargetUnavailable => RecoveryStatus.TargetUnavailable,
                _ => throw new InvalidOperationException("Unknown recovery outcome.")
            },
            Receipt = result.Decision is { } receipt ? Receipt(receipt) : null
        };
    }

    private static RecoveryReceipt Receipt(PostgreSqlDatabaseRecoveryReceipt receipt)
    {
        var result = new RecoveryReceipt
        {
            DecisionId = receipt.DecisionId.ToString("D"),
            Key = new OperationRequest
            {
                DeploymentId = receipt.Key.DeploymentId.ToString(),
                Generation = receipt.Key.Generation.Value,
                OperationId = receipt.Key.OperationId.Value
            },
            AttemptId = receipt.AttemptId.ToString("D"),
            Reason = RecoveryReason.OperatorAcceptedExistingDatabase,
            TechnicalActor = receipt.TechnicalActor,
            DecidedAtUtc = receipt.DecidedAt.ToUniversalTime().ToString("O")
        };
        if (receipt.AppliedGenerationAtDecision is { } applied) result.AppliedGenerationAtDecision = applied;
        return result;
    }
}
