using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

/// <summary>Exact final owner/access effect for the accepted pair.</summary>
public sealed class PostgreSqlApplicationAccessAction(IDurableOperationJournal journal, IExternalOperationOutputs outputs,
    IGeneratedCredentialOperations generated, IProviderConfigurationStore providers, PostgreSqlProviderConfigurationCodec codec,
    PostgreSqlApplicationAccessTarget target, IPostgreSqlAccessFaultHooks hooks,
    TimeProvider clock) : IDurableActionHandler
{
    public const string Format = "haby.postgresql.application-login-configure-access/v1";
    public static DurableActionExecutionKey RegistrationKey { get; } = new(
        PostgreSqlDatabaseCreateAction.RegistrationKey.Admission with { ResourceContract = PostgreSqlApplicationLoginContract.Id },
        OperationContractId.Create("postgresql.application-login.configure-access/v1alpha1"));
    public bool RequiresExternalOutput => true;
    public ExternalOperationOutputIdentity OutputIdentity => new(PostgreSqlApplicationConnectionContract.Contract, PostgreSqlApplicationConnectionContract.Key);
    public DurableActionExecutionKey Key => RegistrationKey;
    public bool Supports(DurableOperationNode node) => Decode(node) is not null;
    internal static PostgreSqlLoginAccessInput? Decode(DurableOperationNode node)
    {
        if (node.Action != RegistrationKey || node.Input.Format != Format || node.Id.Value != node.ResourceId.Value + "-access") return null;
        try
        {
            var input = JsonSerializer.Deserialize(node.Input.Json, PostgreSqlPairJsonContext.Default.PostgreSqlLoginAccessInput);
            return input is not null && input.DatabaseName is not null && PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(input.DatabaseName) &&
                PostgreSqlApplicationLoginContract.ValidSchema(input.Schema) && input.Policy == PostgreSqlApplicationLoginContract.Policy &&
                input.DatabaseSource is not null && input.DatabaseSource.OutputContract == PostgreSqlDatabaseOutputContract.Contract && input.DatabaseSource.OutputKey == "database" &&
                input.DatabaseSource.ProducerOperationId?.EndsWith("-create", StringComparison.Ordinal) == true &&
                input.RoleSource == new OperationOutputRefDocument(node.ResourceId.Value + "-create", PostgreSqlRoleEvidenceContract.Contract, "role") &&
                input.PasswordSource == new OperationOutputRefDocument(node.ResourceId.Value + "-material", GeneratedCredentialContracts.OutputContract, "password") &&
                node.Dependencies.Select(d => d.Value).SequenceEqual(new[] { input.RoleSource.ProducerOperationId, input.PasswordSource.ProducerOperationId,
                    input.DatabaseSource.ProducerOperationId, input.DatabaseSource.ProducerOperationId[..^7] + "-restrict" }.Order(StringComparer.Ordinal)) &&
                node.OutputReferences.SequenceEqual(new[] { input.RoleSource.ToReference(), input.PasswordSource.ToReference(), input.DatabaseSource.ToReference() }
                    .OrderBy(o => o.ProducerOperationId.Value, StringComparer.Ordinal)) ? input : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException) { return null; }
    }

    public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        var key = operation.Key;
        PostgreSqlAccessTargetInput resolved;
        PostgreSqlApplicationConnection connectionOutput;
        try
        {
            var accepted = await outputs.FindAcceptedAsync(key, cancellationToken);
            if (accepted is null) return new(DurableExecutionOutcome.InputUnavailable, operation);
            var input = Decode(accepted.Node);
            var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(accepted.Inputs.Environment,
                accepted.Provider.Instance, accepted.Provider.Configuration, cancellationToken))?.Configuration;
            if (input is null || configuration is null || !PostgreSqlPairExecution.IsSupported(accepted, codec.Encode(configuration)))
                return new(DurableExecutionOutcome.InputUnavailable, operation);
            var progress = await journal.ProgressAsync(key.DeploymentId, key.Generation, cancellationToken);
            if (progress is null || accepted.Node.Dependencies.Any(d => !progress.Nodes.Any(n => n.Operation.Key.OperationId == d &&
                n.Operation.State == DurableOperationState.Succeeded))) return new(DurableExecutionOutcome.Blocked, operation);
            var restricted = progress.Nodes.Single(n => n.Operation.Key.OperationId.Value == input.DatabaseSource.ProducerOperationId[..^7] + "-restrict").Operation;
            var restrictAttempts = await journal.FindAttemptsAsync(restricted.Key, cancellationToken);
            if (!restrictAttempts.Any(a => a.AttemptId == restricted.AttemptId && a.AttemptNumber == restricted.AttemptNumber &&
                a.DispatchedAt is not null && a.FinishedAt is not null && a.Outcome == DurableAttemptOutcome.Succeeded))
                return new(DurableExecutionOutcome.InputUnavailable, operation);
            var database = await outputs.ConsumeAsync(key, input.DatabaseSource.ToReference(), cancellationToken);
            var evidence = database is null ? null : PostgreSqlDatabaseOutputContract.Decode(database);
            var password = await generated.ConsumeAsync(key, input.PasswordSource.ToReference(), cancellationToken);
            if (evidence is null || evidence.DatabaseName != input.DatabaseName || password is null)
                return new(DurableExecutionOutcome.InputUnavailable, operation);
            var roleOutput = await outputs.ConsumeAsync(key, input.RoleSource.ToReference(), cancellationToken);
            var role = roleOutput is null ? null : PostgreSqlRoleEvidenceContract.Decode(roleOutput);
            var owner = new ManagedResourceKey(accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId, accepted.Node.ResourceId);
            if (role is null || role.RoleName != PostgreSqlResourceNaming.Role(owner, accepted.Provider.ResourceNaming().Policy))
                return new(DurableExecutionOutcome.InputUnavailable, operation);
            resolved = new(new(new(accepted.Inputs.Environment, accepted.Provider.Instance, accepted.Provider.Configuration,
                accepted.Provider.Implementation.Value, input.DatabaseName), evidence, configuration.AdministratorUserName,
                owner, role.RoleName, password.Reference)
            { Naming = accepted.Provider.ResourceNaming().Policy }, role, input.Schema);
            connectionOutput = new(configuration.Host, configuration.Port, input.DatabaseName, input.Schema, role.RoleName, password.Reference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(DurableExecutionOutcome.InputUnavailable, operation); }

        var begin = await journal.TryBeginAttemptAsync(key, clock.GetUtcNow(), cancellationToken);
        if (begin is not DurableBeginAttemptResult.Started started) return await ObserveAsync(key);
        var attempt = started.AttemptId;
        try
        {
            var check = await target.CheckAsync(resolved, cancellationToken);
            if (check != PostgreSqlLoginOutcome.Confirmed)
            {
                var reason = check switch
                {
                    PostgreSqlLoginOutcome.Conflict => DurableOperationReason.Conflict,
                    PostgreSqlLoginOutcome.SecretUnavailable => DurableOperationReason.SecretUnavailable,
                    PostgreSqlLoginOutcome.Connectivity => DurableOperationReason.Connectivity,
                    _ => DurableOperationReason.InputUnavailable
                };
                if (check == PostgreSqlLoginOutcome.Conflict)
                    await journal.TryFinalizeFailureAsync(key, attempt, DurableOperationState.Failed, reason, clock.GetUtcNow(), cancellationToken);
                else await journal.TryCloseAttemptBeforeDispatchAsync(key, attempt, DurableAttemptOutcome.FailedBeforeDispatch, reason, clock.GetUtcNow(), cancellationToken);
                return await ObserveAsync(key);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await journal.TryCloseAttemptBeforeDispatchAsync(key, attempt, DurableAttemptOutcome.CanceledBeforeDispatch, null, clock.GetUtcNow(), CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            await journal.TryCloseAttemptBeforeDispatchAsync(key, attempt, DurableAttemptOutcome.FailedBeforeDispatch,
                DurableOperationReason.InputUnavailable, clock.GetUtcNow(), CancellationToken.None);
            return await ObserveAsync(key);
        }
        try
        {
            if (await journal.TryCommitDispatchMarkerAsync(key, attempt, clock.GetUtcNow(), cancellationToken) is not DurableMarkerResult.Committed)
                return new(DurableExecutionOutcome.DispatchUncertain, await journal.FindAsync(key, CancellationToken.None));
        }
        catch (Exception) { return new(DurableExecutionOutcome.DispatchUncertain, await journal.FindAsync(key, CancellationToken.None)); }
        try
        {
            await hooks.AfterMarkerAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await target.DispatchAsync(resolved, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == PostgreSqlLoginOutcome.Confirmed)
            {
                await hooks.BeforeFinalizationAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (await outputs.CompleteAsync(key, attempt, PostgreSqlApplicationConnectionContract.Encode(connectionOutput), clock.GetUtcNow(), CancellationToken.None))
                    return await ObserveAsync(key);
            }
            else if (result is PostgreSqlLoginOutcome.Rejected or PostgreSqlLoginOutcome.Conflict)
            {
                await journal.TryFinalizeFailureAsync(key, attempt, DurableOperationState.Failed,
                    result == PostgreSqlLoginOutcome.Conflict ? DurableOperationReason.Conflict : DurableOperationReason.ServerRejected,
                    clock.GetUtcNow(), CancellationToken.None);
                return await ObserveAsync(key);
            }
        }
        catch (Exception) { /* Never expose driver details or replay after the marker. */ }
        try
        {
            await journal.TryFinalizeFailureAsync(key, attempt, DurableOperationState.NeedsAttention,
                DurableOperationReason.UncertainAfterDispatch, clock.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception) { /* Dispatching remains non-replayable. */ }
        return await ObserveAsync(key);
    }

    private async ValueTask<DurableExecutionResult> ObserveAsync(DurableOperationKey key)
    {
        var operation = await journal.FindAsync(key, CancellationToken.None);
        return operation is null ? DurableExecutionResult.NotFound() : new(operation.State switch
        {
            DurableOperationState.Succeeded => DurableExecutionOutcome.Succeeded,
            DurableOperationState.Failed => DurableExecutionOutcome.Failed,
            DurableOperationState.NeedsAttention => DurableExecutionOutcome.NeedsAttention,
            DurableOperationState.Preparing => DurableExecutionOutcome.RetryableFailure,
            _ => DurableExecutionOutcome.DispatchUncertain
        }, operation);
    }
}

public static class PostgreSqlApplicationAccessServices
{
    /// <summary>Explicit final real effect and output registration.</summary>
    public static IServiceCollection AddPostgreSqlApplicationAccess(this IServiceCollection services)
    {
        services.TryAddSingleton<IPostgreSqlAccessFaultHooks, NullPostgreSqlAccessFaultHooks>();
        services.TryAddSingleton<PostgreSqlApplicationAccessTarget>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableActionHandler, PostgreSqlApplicationAccessAction>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalOperationOutputContract, PostgreSqlApplicationConnectionContract>());
        return services;
    }
}
