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

    internal static DurableOperationSummary RecoveryOperationSummary(DurableCreateOperation operation) => Summary(operation);
}

/// <summary>Explicit compile-time dispatch for the alpha surface; no alias resolution or Provider fallback.</summary>
public sealed class OperationRecoveryAdapter(IDurableCreateOperationQuery operations,
    IManifestRevisionQuery manifests, PostgreSqlOperationRecoveryAdapter postgreSql)
{
    public async ValueTask<RecoveryReply> RecoverAsync(DurableCreateOperationKey key,
        AssignExistingOperationRequest? assignment, CancellationToken cancellationToken)
    {
        var operation = await operations.FindAsync(key, cancellationToken);
        if (operation is null) return new() { Status = RecoveryStatus.OperationNotFound };
        if (key.Action != DurableOperationAction.Create ||
            operation.ImplementationId != PostgreSqlDatabaseCheckpointCapability.ImplementationId)
            return new() { Status = RecoveryStatus.UnsupportedRecoveryAction };
        // An immutable recovery receipt already establishes this adapter's contract for the key.
        // Preserve application idempotency even if the pinned manifest is now unavailable.
        if (await postgreSql.HasReceiptAsync(operation, cancellationToken))
            return assignment is null ? await postgreSql.InspectAsync(operation, cancellationToken)
                : await postgreSql.AssignAsync(key, assignment, cancellationToken);
        ManifestRevision? revision;
        try { revision = await manifests.FindAsync(operation.Revision, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new() { Status = RecoveryStatus.TargetUnavailable }; }
        if (revision is null) return new() { Status = RecoveryStatus.TargetUnavailable };
        if (!revision.Declaration.Spec.Resources.TryGetValue(key.ResourceId, out var resource) ||
            resource.Type != PostgreSqlDatabaseContract.Id)
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
    public async ValueTask<bool> HasReceiptAsync(DurableCreateOperation operation, CancellationToken cancellationToken) =>
        operation.State == DurableCreateOperationState.Succeeded && operation.AttemptId is { } attempt &&
        await decisions.FindDecisionAsync(operation.Key, attempt, cancellationToken) is not null;

    public async ValueTask<RecoveryReply> InspectAsync(DurableCreateOperation operation, CancellationToken cancellationToken)
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

    public async ValueTask<RecoveryReply> AssignAsync(DurableCreateOperationKey key,
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

    private static RecoveryReceipt Receipt(PostgreSqlDatabaseRecoveryReceipt receipt) => new()
    {
        DecisionId = receipt.DecisionId.ToString("D"),
        Key = new OperationRequest
        {
            DeploymentId = receipt.Key.DeploymentId.ToString(),
            Generation = receipt.Key.Generation.Value,
            ResourceId = receipt.Key.ResourceId.Value,
            Action = OperationAction.Create
        },
        AttemptId = receipt.AttemptId.ToString("D"),
        Reason = RecoveryReason.OperatorAcceptedExistingDatabase,
        TechnicalActor = receipt.TechnicalActor,
        DecidedAtUtc = receipt.DecidedAt.ToUniversalTime().ToString("O")
    };
}
