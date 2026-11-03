using System.Text.RegularExpressions;
using Haby.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

/// <summary>Manual assignment of one uncertain PostgreSQL Create; no SQL Create is reachable here.</summary>
public sealed class PostgreSqlDatabaseRecovery(IDurableOperationJournal operations,
    IPostgreSqlDatabaseRecoveryJournal decisions,
    IPostgreSqlDatabaseCreateTarget target, TimeProvider clock, DurableOperationSerialization serialization) : IPostgreSqlDatabaseRecovery
{
    private static readonly Regex CredentialPattern = new(
        @"(?i)(password|passwd|pwd|secret|token|api[_-]?key|connection[_-]?string)\s*[:=]|postgres(?:ql)?://|-----BEGIN |(?:host|username|user id)\s*=.+;",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly PostgreSqlDurableCreateInputResolver resolver = new();

    public async ValueTask<PostgreSqlDatabaseRecoveryPreview?> InspectAsync(DurableOperationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var operation = await operations.FindAsync(key, cancellationToken);
        if (operation is null) return null;
        var shape = await resolver.ResolveAsync(key, operation, cancellationToken);
        if (!await IsStandaloneAsync(operation, cancellationToken))
            shape = (null, DurableOperationReason.InputUnavailable, false);
        Guid? uncertainAttempt = null;
        if (shape.Input is not null && operation.State == DurableOperationState.NeedsAttention && operation.AttemptId is { } attemptId)
        {
            var attempts = await operations.FindAttemptsAsync(key, cancellationToken);
            if (attempts.Count(attempt => attempt.AttemptId == attemptId &&
                    attempt.AttemptNumber == operation.AttemptNumber && attempt.DispatchedAt is not null &&
                    attempt.FinishedAt is not null && attempt.Outcome == DurableAttemptOutcome.Uncertain) == 1)
                uncertainAttempt = attemptId;
        }
        var reason = shape.Reason ?? operation.Reason;
        if (operation.State == DurableOperationState.NeedsAttention && uncertainAttempt is null)
            reason = DurableOperationReason.InputUnavailable;
        return new PostgreSqlDatabaseRecoveryPreview(key, operation.Environment, operation.Application,
            shape.Input?.DatabaseName, operation.ProviderInstance, operation.ConfigurationRevision,
            operation.ImplementationId, uncertainAttempt, operation.State, reason)
        { Snapshot = operation };
    }

    public ValueTask<PostgreSqlDatabaseAssignmentResult> AssignExistingDatabaseAsync(
        AssignExistingPostgreSqlDatabase command, CancellationToken cancellationToken) =>
        serialization.RunAsync(command.Key, () => AssignCoreAsync(command, cancellationToken), cancellationToken);

    private async ValueTask<PostgreSqlDatabaseAssignmentResult> AssignCoreAsync(
        AssignExistingPostgreSqlDatabase command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        var operation = await operations.FindAsync(command.Key, cancellationToken);
        if (operation is null || operation.Environment != command.Environment)
            return Rejected();
        if (!await IsStandaloneAsync(operation, cancellationToken)) return Rejected();

        var shape = await resolver.ResolveAsync(command.Key, operation, cancellationToken);
        if (shape.Input is null) return Rejected();

        // A lost response is a read of the original immutable decision. It must not recheck the
        // external database: its present condition could have changed since the accepted decision.
        var existing = await decisions.FindDecisionAsync(command.Key, command.ExpectedAttemptId,
            cancellationToken);
        if (existing is not null)
            return new PostgreSqlDatabaseAssignmentResult(PostgreSqlDatabaseAssignmentOutcome.AlreadyAssigned,
                existing.Receipt);

        if (operation.State != DurableOperationState.NeedsAttention ||
            operation.Reason != DurableOperationReason.UncertainAfterDispatch ||
            operation.AttemptId != command.ExpectedAttemptId || operation.DispatchedAt is null ||
            operation.FinalizedAt is null)
            return Rejected();
        var attempts = await operations.FindAttemptsAsync(command.Key, cancellationToken);
        if (attempts.Count(attempt => attempt.AttemptId == command.ExpectedAttemptId &&
                attempt.AttemptNumber == operation.AttemptNumber && attempt.DispatchedAt is not null &&
                attempt.FinishedAt is not null && attempt.Outcome == DurableAttemptOutcome.Uncertain) != 1)
            return Rejected();

        PostgreSqlDatabaseAbsenceOutcome absence;
        try { absence = await target.CheckDatabaseAbsentAsync(shape.Input, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(PostgreSqlDatabaseAssignmentOutcome.TargetUnavailable, null); }
        if (absence == PostgreSqlDatabaseAbsenceOutcome.Absent)
            return new(PostgreSqlDatabaseAssignmentOutcome.DatabaseAbsent, null);
        if (absence != PostgreSqlDatabaseAbsenceOutcome.Present)
            return new(PostgreSqlDatabaseAssignmentOutcome.TargetUnavailable, null);

        return await decisions.TryAssignAsync(command, clock.GetUtcNow(), cancellationToken);
    }

    private async ValueTask<bool> IsStandaloneAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        // Progress reads the immutable accepted graph, not the current manifest or alias.
        // No-output manual assignment cannot satisfy a dependent evidence consumer.
        var progress = await operations.ProgressAsync(operation.Key.DeploymentId, operation.Key.Generation, cancellationToken);
        return progress?.Nodes is [var only] && only.Operation.Key == operation.Key &&
            only.Operation.Node.Action == PostgreSqlDatabaseCreateAction.RegistrationKey &&
            only.Operation.Node.Dependencies.Count == 0 && only.Operation.Node.OutputReferences.Count == 0;
    }

    private static PostgreSqlDatabaseAssignmentResult Rejected() =>
        new(PostgreSqlDatabaseAssignmentOutcome.Rejected, null);

    private static void Validate(AssignExistingPostgreSqlDatabase command)
    {
        ArgumentNullException.ThrowIfNull(command.Key);
        if (command.ExpectedAttemptId == Guid.Empty || !command.Acknowledged ||
            command.Reason != PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase ||
            !SafeText(command.Note, 512) || CredentialPattern.IsMatch(command.Note) ||
            !SafeText(command.TechnicalActor, 256) || CredentialPattern.IsMatch(command.TechnicalActor))
            throw new ArgumentException("Recovery assignment request is invalid.", nameof(command));
    }

    private static bool SafeText(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && !string.IsNullOrWhiteSpace(value) &&
        !value.Any(character => char.IsControl(character) ||
            character is '\u061c' or '\u200e' or '\u200f' or '\u202a' or '\u202b' or '\u202c' or
                '\u202d' or '\u202e' or '\u2066' or '\u2067' or '\u2068' or '\u2069' or
                '\u206a' or '\u206b' or '\u206c' or '\u206d' or '\u206e' or '\u206f');
}

public static class PostgreSqlDatabaseRecoveryServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDatabaseRecovery(this IServiceCollection services)
    {
        services.AddPostgreSqlDatabaseCreateTarget();
        services.TryAddSingleton<IPostgreSqlDatabaseRecovery, PostgreSqlDatabaseRecovery>();
        return services;
    }
}
