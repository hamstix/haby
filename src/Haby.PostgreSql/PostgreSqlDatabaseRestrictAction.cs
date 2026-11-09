using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

/// <summary>Opt-in exact restrict effect. Production pair admission remains unavailable.</summary>
public sealed class PostgreSqlDatabaseRestrictAction(IDurableOperationJournal journal, IExternalOperationOutputs outputs,
    IProviderConfigurationStore providers, PostgreSqlDatabaseRestrictTarget target, IPostgreSqlRestrictFaultHooks hooks,
    TimeProvider clock, PostgreSqlProviderConfigurationCodec codec, PostgreSqlApplicationAccessTarget? access = null) : IDurableActionHandler
{
    public const string Format = "haby.postgresql.database-restrict/v1";
    public static DurableActionExecutionKey RegistrationKey { get; } = new(PostgreSqlDatabaseCreateAction.RegistrationKey.Admission,
        OperationContractId.Create("postgresql.database.restrict/v1alpha1"));
    public DurableActionExecutionKey Key => RegistrationKey;
    public bool Supports(DurableOperationNode node) => Decode(node) is not null;
    internal static PostgreSqlDatabaseRestrictInput? Decode(DurableOperationNode node)
    {
        if (node.Action != RegistrationKey || node.Input.Format != Format) return null;
        try
        {
            var input = JsonSerializer.Deserialize(node.Input.Json, PostgreSqlPairJsonContext.Default.PostgreSqlDatabaseRestrictInput);
            return input?.DatabaseName is not null && PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(input.DatabaseName) &&
                input.DatabaseSource is not null && input.DatabaseSource.ProducerOperationId == node.ResourceId.Value + "-create" &&
                input.DatabaseSource.OutputContract == PostgreSqlDatabaseOutputContract.Contract && input.DatabaseSource.OutputKey == PostgreSqlDatabaseOutputContract.Key &&
                !string.IsNullOrWhiteSpace(input.ExpectedOwnerUserName) && node.OutputReferences.SequenceEqual([input.DatabaseSource.ToReference()]) &&
                node.Dependencies.SequenceEqual([input.DatabaseSource.ToReference().ProducerOperationId]) ? input : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { return null; }
    }

    public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        var key = operation.Key;
        // Always rederive trusted identities, graph and manifest from committed state.
        PostgreSqlRestrictTargetInput? resolved;
        try
        {
            var accepted = await outputs.FindAcceptedAsync(key, cancellationToken);
            if (accepted is null) return new(DurableExecutionOutcome.InputUnavailable, operation);
            // Retain the earlier real-effect fixtures. Complete pairs require explicit
            // final-target composition and the exact accepted five-node graph.
            if (accepted.Graph.Nodes.Count is not (2 or 4) && (accepted.Graph.Nodes.Count != 5 || access is null)) return new(DurableExecutionOutcome.HandlerUnavailable, operation);
            var input = Decode(accepted.Node);
            if (input is null) return new(DurableExecutionOutcome.InputUnavailable, operation);
            var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(accepted.Inputs.Environment,
                accepted.Inputs.ProviderInstance, accepted.Inputs.ConfigurationRevision, cancellationToken))?.Configuration;
            if (configuration is null || configuration.ImplementationId != accepted.Inputs.ImplementationId ||
                configuration.AdministratorUserName != input.ExpectedOwnerUserName ||
                !(accepted.Graph.Nodes.Count == 2 ? ValidGraph(accepted, input) : PostgreSqlPairExecution.IsSupported(accepted, codec.Encode(configuration)))) return new(DurableExecutionOutcome.InputUnavailable, operation);
            var output = await outputs.ConsumeAsync(key, input.DatabaseSource.ToReference(), cancellationToken);
            var evidence = output is null ? null : PostgreSqlDatabaseOutputContract.Decode(output);
            if (evidence is null || evidence.DatabaseName != input.DatabaseName) return new(DurableExecutionOutcome.InputUnavailable, operation);
            resolved = new(new(accepted.Inputs.Environment, accepted.Inputs.ProviderInstance,
                accepted.Inputs.ConfigurationRevision, accepted.Inputs.ImplementationId, input.DatabaseName), evidence, input.ExpectedOwnerUserName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(DurableExecutionOutcome.InputUnavailable, operation); }

        var begin = await journal.TryBeginAttemptAsync(key, clock.GetUtcNow(), cancellationToken);
        if (begin is not DurableBeginAttemptResult.Started started) return await ObserveAsync(key);
        var attempt = started.AttemptId;
        try
        {
            var check = await target.CheckAsync(resolved, cancellationToken);
            if (check != PostgreSqlRestrictOutcome.Confirmed)
            {
                var reason = check switch
                {
                    PostgreSqlRestrictOutcome.Conflict => DurableOperationReason.Conflict,
                    PostgreSqlRestrictOutcome.SecretUnavailable => DurableOperationReason.SecretUnavailable,
                    PostgreSqlRestrictOutcome.Connectivity => DurableOperationReason.Connectivity,
                    _ => DurableOperationReason.InputUnavailable
                };
                if (check == PostgreSqlRestrictOutcome.Conflict)
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
            if (result == PostgreSqlRestrictOutcome.Confirmed)
            {
                await hooks.BeforeFinalizationAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (await journal.TryRecordSuccessAsync(key, attempt, clock.GetUtcNow(), CancellationToken.None)) return await ObserveAsync(key);
            }
            else if (result is PostgreSqlRestrictOutcome.Rejected or PostgreSqlRestrictOutcome.Conflict)
            {
                await journal.TryFinalizeFailureAsync(key, attempt, DurableOperationState.Failed,
                    result == PostgreSqlRestrictOutcome.Conflict ? DurableOperationReason.Conflict : DurableOperationReason.ServerRejected,
                    clock.GetUtcNow(), CancellationToken.None);
                return await ObserveAsync(key);
            }
        }
        catch (Exception) { /* All post-marker interruptions retain uncertainty. */ }
        try
        {
            await journal.TryFinalizeFailureAsync(key, attempt, DurableOperationState.NeedsAttention,
                DurableOperationReason.UncertainAfterDispatch, clock.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception) { /* A retained Dispatching marker is equally non-replayable. */ }
        return await ObserveAsync(key);
    }

    private static bool ValidGraph(AcceptedOperation accepted, PostgreSqlDatabaseRestrictInput input)
    {
        var node = accepted.Node;
        var producer = accepted.Graph.Nodes.SingleOrDefault(n => n.Id.Value == "db-create");
        if (producer is null || producer.Action != PostgreSqlDatabaseCreateAction.RegistrationKey || producer.ResourceId != node.ResourceId ||
            producer.References != node.References || producer.Dependencies.Count != 0 || producer.OutputReferences.Count != 0 ||
            producer.Input.Format != PostgreSqlDatabaseCreateAction.Format || node.Id.Value != "db-restrict" ||
            accepted.Spec.Resources.Count != 1 || accepted.Spec.Components.Count != 0 ||
            !accepted.Spec.Resources.TryGetValue(node.ResourceId, out var resource) ||
            PostgreSqlDatabaseCreateValidation.Name(resource) != input.DatabaseName) return false;
        return JsonSerializer.Deserialize(producer.Input.Json, PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput)?.DatabaseName == input.DatabaseName;
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

public static class PostgreSqlRestrictServiceCollectionExtensions
{
    /// <summary>Opt-in effect only; does not activate pair admission or supply missing actions.</summary>
    public static IServiceCollection AddPostgreSqlDatabaseRestrict(this IServiceCollection services)
    {
        services.TryAddSingleton<IPostgreSqlRestrictFaultHooks, NullPostgreSqlRestrictFaultHooks>();
        services.TryAddSingleton<PostgreSqlDatabaseRestrictTarget>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableActionHandler, PostgreSqlDatabaseRestrictAction>());
        return services;
    }
}
