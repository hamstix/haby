using Haby.Domain;

namespace Haby.Application;

/// <summary>One PostgreSQL-specific manual recovery decision. This is not a general Provider contract.</summary>
public enum PostgreSqlRecoveryReason { OperatorAcceptedExistingDatabase }

public sealed record PostgreSqlDatabaseRecoveryPreview(DurableOperationKey Key,
    EnvironmentRecordId Environment, ApplicationRecordId Application, string? DatabaseName,
    ProviderInstanceId ProviderInstance, ProviderConfigurationRevisionNumber ConfigurationRevision,
    string ImplementationId, Guid? UncertainAttemptId, DurableOperationState State,
    DurableOperationReason? Reason)
{
    /// <summary>The single read used for this preview, so adapters cannot mix attempt/state snapshots.</summary>
    public required DurableOperation Snapshot { get; init; }
}

/// <summary>The caller must affirmatively accept provenance responsibility for this exact attempt.</summary>
public sealed record AssignExistingPostgreSqlDatabase(DurableOperationKey Key,
    EnvironmentRecordId Environment, Guid ExpectedAttemptId, bool Acknowledged,
    PostgreSqlRecoveryReason Reason, string Note, string TechnicalActor)
{
    public override string ToString() => nameof(AssignExistingPostgreSqlDatabase);
}

public enum PostgreSqlDatabaseAssignmentOutcome
{
    Assigned,
    AlreadyAssigned,
    Rejected,
    DatabaseAbsent,
    TargetUnavailable
}

/// <summary>A value-free receipt. The operator note is available only through the protected audit query.</summary>
public sealed record PostgreSqlDatabaseRecoveryReceipt(Guid DecisionId, DurableOperationKey Key,
    Guid AttemptId, PostgreSqlRecoveryReason Reason, string TechnicalActor, DateTimeOffset DecidedAt, long? AppliedGenerationAtDecision);

public sealed record PostgreSqlDatabaseAssignmentResult(PostgreSqlDatabaseAssignmentOutcome Outcome,
    PostgreSqlDatabaseRecoveryReceipt? Decision);

/// <summary>Protected audit record. Callers must not log or render Note in ordinary diagnostics.</summary>
public sealed record PostgreSqlDatabaseRecoveryAudit(PostgreSqlDatabaseRecoveryReceipt Receipt,
    bool Acknowledged, string Note)
{
    public override string ToString() => nameof(PostgreSqlDatabaseRecoveryAudit);
}

/// <summary>Single-instance manual recovery boundary. Operator adapters delegate validation and decisions here.</summary>
public interface IPostgreSqlDatabaseRecovery
{
    ValueTask<PostgreSqlDatabaseRecoveryPreview?> InspectAsync(DurableOperationKey key,
        CancellationToken cancellationToken);
    ValueTask<PostgreSqlDatabaseAssignmentResult> AssignExistingDatabaseAsync(
        AssignExistingPostgreSqlDatabase command, CancellationToken cancellationToken);
}

/// <summary>Internal durable compare-and-transition and protected audit access.</summary>
public interface IPostgreSqlDatabaseRecoveryJournal
{
    ValueTask<PostgreSqlDatabaseRecoveryAudit?> FindDecisionAsync(DurableOperationKey key,
        Guid attemptId, CancellationToken cancellationToken);
    ValueTask<PostgreSqlDatabaseAssignmentResult> TryAssignAsync(AssignExistingPostgreSqlDatabase command,
        DateTimeOffset at, CancellationToken cancellationToken);
}
