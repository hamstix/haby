using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

/// <summary>Opt-in login effect. Full graphs require explicit final-target composition; Host remains unchanged.</summary>
public sealed class PostgreSqlApplicationLoginAction(IDurableOperationJournal journal, IExternalOperationOutputs outputs,
    IGeneratedCredentialOperations generated, IProviderConfigurationStore providers, PostgreSqlProviderConfigurationCodec codec,
    IProviderResourceReservations names, PostgreSqlApplicationLoginTarget target, IPostgreSqlLoginFaultHooks hooks,
    TimeProvider clock, PostgreSqlApplicationAccessTarget? access = null) : IDurableActionHandler
{
    public const string Format = "haby.postgresql.application-login-create/v1";
    public const string RoleReservationNamespace = "postgresql.role";
    public static DurableActionExecutionKey RegistrationKey { get; } = new(
        PostgreSqlDatabaseCreateAction.RegistrationKey.Admission with { ResourceContract = PostgreSqlApplicationLoginContract.Id },
        OperationContractId.Create("postgresql.application-login.create/v1alpha1"));
    public DurableActionExecutionKey Key => RegistrationKey;
    public bool Supports(DurableOperationNode node) => Decode(node) is not null;
    internal static PostgreSqlLoginCreateInput? Decode(DurableOperationNode node)
    {
        if (node.Action != RegistrationKey || node.Input.Format != Format) return null;
        try
        {
            var input = JsonSerializer.Deserialize(node.Input.Json, PostgreSqlPairJsonContext.Default.PostgreSqlLoginCreateInput);
            if (input is null || input.Owner is null || input.DatabaseSource is null || input.PasswordSource is null ||
                input.DatabaseName is null || !PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(input.DatabaseName)) return null;
            var owner = new ManagedResourceKey(EnvironmentRecordId.Parse(input.Owner.Environment),
                ApplicationRecordId.Parse(input.Owner.Application), ManifestLocalId.Create(input.Owner.Resource));
            return input.RoleName == PostgreSqlPairResourceAdmission.RoleName(owner) && node.Id.Value == "app-login-create" &&
                node.ResourceId == owner.ResourceLocalId && node.References.Environment == owner.EnvironmentRecordId &&
                input.DatabaseSource == new OperationOutputRefDocument("db-create", PostgreSqlDatabaseOutputContract.Contract, "database") &&
                input.PasswordSource == new OperationOutputRefDocument("app-login-material", GeneratedCredentialContracts.OutputContract, "password") &&
                node.Dependencies.Select(d => d.Value).SequenceEqual(new[] { "app-login-material", "db-create", "db-restrict" }) &&
                node.OutputReferences.SequenceEqual(new[] { input.PasswordSource.ToReference(), input.DatabaseSource.ToReference() }) ? input : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException) { return null; }
    }

    public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        var key = operation.Key;
        PostgreSqlLoginTargetInput resolved;
        try
        {
            var accepted = await outputs.FindAcceptedAsync(key, cancellationToken);
            if (accepted is null) return new(DurableExecutionOutcome.InputUnavailable, operation);
            if (accepted.Graph.Nodes.Count != 4 && (accepted.Graph.Nodes.Count != 5 || access is null)) return new(DurableExecutionOutcome.HandlerUnavailable, operation);
            var input = Decode(accepted.Node);
            var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(accepted.Inputs.Environment,
                accepted.Inputs.ProviderInstance, accepted.Inputs.ConfigurationRevision, cancellationToken))?.Configuration;
            if (input is null || configuration is null || !PostgreSqlPairExecution.IsSupported(accepted, codec.Encode(configuration)))
                return new(DurableExecutionOutcome.InputUnavailable, operation);
            var progress = await journal.ProgressAsync(key.DeploymentId, key.Generation, cancellationToken);
            if (progress is null || accepted.Node.Dependencies.Any(d => !progress.Nodes.Any(n => n.Operation.Key.OperationId == d &&
                n.Operation.State == DurableOperationState.Succeeded))) return new(DurableExecutionOutcome.Blocked, operation);
            var database = await outputs.ConsumeAsync(key, input.DatabaseSource.ToReference(), cancellationToken);
            var evidence = database is null ? null : PostgreSqlDatabaseOutputContract.Decode(database);
            var password = await generated.ConsumeAsync(key, input.PasswordSource.ToReference(), cancellationToken);
            if (evidence is null || evidence.DatabaseName != input.DatabaseName || password is null)
                return new(DurableExecutionOutcome.InputUnavailable, operation);
            var owner = new ManagedResourceKey(accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId, accepted.Node.ResourceId);
            resolved = new(new(accepted.Inputs.Environment, accepted.Inputs.ProviderInstance, accepted.Inputs.ConfigurationRevision,
                accepted.Inputs.ImplementationId, input.DatabaseName), evidence, configuration.AdministratorUserName,
                owner, input.RoleName, password.Reference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(DurableExecutionOutcome.InputUnavailable, operation); }

        var begin = await journal.TryBeginAttemptAsync(key, clock.GetUtcNow(), cancellationToken);
        if (begin is not DurableBeginAttemptResult.Started started) return await ObserveAsync(key);
        var attempt = started.AttemptId;
        try
        {
            // Local acceptance is durable independently of attempts and external permissions.
            if (!await names.TryReserveAsync(new(resolved.Provider.ProviderInstance, resolved.Owner.EnvironmentRecordId,
                resolved.Owner.ApplicationRecordId, resolved.Owner.ResourceLocalId, RoleReservationNamespace, resolved.RoleName), cancellationToken))
            {
                await journal.TryFinalizeFailureAsync(key, attempt, DurableOperationState.Failed, DurableOperationReason.Conflict, clock.GetUtcNow(), cancellationToken);
                return await ObserveAsync(key);
            }
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
            if (result.Evidence is { } evidence && result.Outcome == PostgreSqlLoginOutcome.Confirmed)
            {
                await hooks.BeforeFinalizationAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (await outputs.CompleteAsync(key, attempt, PostgreSqlRoleEvidenceContract.Encode(evidence), clock.GetUtcNow(), CancellationToken.None))
                    return await ObserveAsync(key);
            }
            else if (result.Outcome is PostgreSqlLoginOutcome.Rejected or PostgreSqlLoginOutcome.Conflict)
            {
                await journal.TryFinalizeFailureAsync(key, attempt, DurableOperationState.Failed,
                    result.Outcome == PostgreSqlLoginOutcome.Conflict ? DurableOperationReason.Conflict : DurableOperationReason.ServerRejected,
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

public static class PostgreSqlApplicationLoginServices
{
    public static IServiceCollection AddPostgreSqlApplicationLogin(this IServiceCollection services)
    {
        services.TryAddSingleton<IPostgreSqlLoginFaultHooks, NullPostgreSqlLoginFaultHooks>();
        services.TryAddSingleton<PostgreSqlApplicationLoginTarget>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableActionHandler, PostgreSqlApplicationLoginAction>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalOperationOutputContract, PostgreSqlRoleEvidenceContract>());
        return services;
    }
}
