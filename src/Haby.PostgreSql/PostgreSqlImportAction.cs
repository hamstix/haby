using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

public interface IPostgreSqlImportFaultHooks
{
    ValueTask AfterMarkerAsync(int confirmedMarkers, CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterVerificationAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterReuseVerificationAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterAcceptanceAsync(CancellationToken token) => ValueTask.CompletedTask;
}
public sealed class NullPostgreSqlImportFaultHooks : IPostgreSqlImportFaultHooks;

/// <summary>Both import nodes coordinate one read-only verification and one atomic catalog acceptance.
/// An interrupted dispatched import reuses its exact open attempts; Provision remains non-replayable.</summary>
public sealed class PostgreSqlImportAction(ResourceTypeId type, IDurableOperationJournal journal, IExternalOperationOutputs outputs,
    IResourceObservationAcceptance acceptance, PostgreSqlImportVerifier verifier, IProviderConfigurationStore providers,
    IPostgreSqlImportFaultHooks hooks, TimeProvider clock) : IReadOnlyResourceImportHandler
{
    public DurableActionExecutionKey Key => type == PostgreSqlDatabaseContract.Id ? PostgreSqlImportContracts.Database : PostgreSqlImportContracts.Login;
    public bool RequiresExternalOutput => true;
    public ExternalOperationOutputIdentity OutputIdentity => type == PostgreSqlDatabaseContract.Id
        ? new(PostgreSqlDatabaseOutputContract.Contract, PostgreSqlDatabaseOutputContract.Key)
        : new(PostgreSqlApplicationConnectionContract.Contract, PostgreSqlApplicationConnectionContract.Key);
    public bool Supports(DurableOperationNode node) => node.Action == Key && PostgreSqlImportContracts.Decode(node) is not null;

    public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        var accepted = await outputs.FindAcceptedAsync(operation.Key, cancellationToken);
        if (accepted is null || PostgreSqlImportAdmission.Prepare(accepted.Spec, accepted.Inputs, accepted.Node.ResourceId) is not { } input ||
            PostgreSqlImportContracts.Decode(accepted.Node) != input)
            return new(DurableExecutionOutcome.InputUnavailable, operation);
        if (await acceptance.FindAsync(operation.Key, cancellationToken) is not null) return await Observed(DurableExecutionOutcome.Succeeded, operation.Key);
        var group = accepted.Graph.ObservationGroups.SingleOrDefault(x => x.Operations.Contains(accepted.Node.Id));
        var members = group is null ? [] : accepted.Graph.Nodes.Where(x => group.Operations.Contains(x.Id)).ToArray();
        if (group?.Mode != ResourceAcquisitionMode.ImportUseAndRetain || members.Length != 2 || members.Any(x => PostgreSqlImportContracts.Decode(x) != input))
            return new(DurableExecutionOutcome.InputUnavailable, operation);
        var attempts = new Dictionary<OperationId, Guid>();
        var markers = 0;
        foreach (var node in members)
        {
            var key = operation.Key with { OperationId = node.Id };
            var current = await journal.FindAsync(key, cancellationToken);
            if (current is null) return new(DurableExecutionOutcome.InputUnavailable, operation);
            Guid attempt;
            if (current.State == DurableOperationState.Dispatching && current.AttemptId is { } existing && current.DispatchedAt is not null)
            {
                var history = await journal.FindAttemptsAsync(key, cancellationToken);
                if (!history.Any(x => x.AttemptId == existing && x.AttemptNumber == current.AttemptNumber && x.DispatchedAt == current.DispatchedAt &&
                    x.FinishedAt is null && x.Outcome is null)) return new(DurableExecutionOutcome.InputUnavailable, current);
                attempt = existing;
            }
            else
            {
                var begin = await journal.TryBeginAttemptAsync(key, clock.GetUtcNow(), cancellationToken);
                if (begin is not DurableBeginAttemptResult.Started started) return new(DurableExecutionOutcome.InputUnavailable, current);
                attempt = started.AttemptId;
                if (await journal.TryCommitDispatchMarkerAsync(key, attempt, clock.GetUtcNow(), cancellationToken) is not DurableMarkerResult.Committed)
                    return await Observed(DurableExecutionOutcome.DispatchUncertain, key);
            }
            attempts.Add(node.Id, attempt);
            await hooks.AfterMarkerAsync(++markers, cancellationToken);
        }
        var verified = await verifier.VerifyAsync(accepted, cancellationToken);
        if (verified.Status != PostgreSqlImportVerificationStatus.Confirmed || verified.Observation is null)
        {
            if (verified.Status == PostgreSqlImportVerificationStatus.Conflict)
            {
                await ConflictAsync(operation.Key, attempts);
                return await Observed(DurableExecutionOutcome.Failed, operation.Key);
            }
            return await Observed(verified.Status == PostgreSqlImportVerificationStatus.Connectivity
                ? DurableExecutionOutcome.RetryableFailure : DurableExecutionOutcome.InputUnavailable, operation.Key);
        }
        await hooks.AfterVerificationAsync(cancellationToken);
        var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(accepted.Inputs.Environment,
            accepted.Provider.Instance, accepted.Provider.Configuration, cancellationToken))?.Configuration;
        if (configuration is null) return await Observed(DurableExecutionOutcome.InputUnavailable, operation.Key);
        var confirmations = members.Select(node => new ObservationOperationConfirmation(operation.Key with { OperationId = node.Id }, attempts[node.Id],
            node.Action == PostgreSqlImportContracts.Database
                ? PostgreSqlDatabaseOutputContract.Encode(new(input.Pair.DatabaseName, input.Pair.DatabaseOid, input.Pair.PublicSchemaOid))
                : PostgreSqlApplicationConnectionContract.Encode(new(configuration.Host, configuration.Port, input.Pair.DatabaseName, input.Pair.Schema,
                    input.Pair.LoginName, PostgreSqlImportContracts.Reference(input))))).ToArray();
        var result = await acceptance.CompleteAsync(new(confirmations,
            [new(new(accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId, ManifestLocalId.Create(input.Pair.LoginResource)),
                new(input.Staged, PostgreSqlImportContracts.Reference(input)))],
            PostgreSqlImportOutputContract.Reservations(accepted.Inputs, input.Pair), PostgreSqlImportObservation.Encode(verified.Observation)),
            clock.GetUtcNow(), cancellationToken);
        if (result.IsSuccess)
        {
            await hooks.AfterAcceptanceAsync(cancellationToken);
            return await Observed(DurableExecutionOutcome.Succeeded, operation.Key);
        }
        if (result.Status == ResourceObservationAcceptanceStatus.Conflict)
        {
            await ConflictAsync(operation.Key, attempts);
            return await Observed(DurableExecutionOutcome.Failed, operation.Key);
        }
        return await Observed(result.Status == ResourceObservationAcceptanceStatus.StorageUnavailable
            ? DurableExecutionOutcome.RetryableFailure : DurableExecutionOutcome.InputUnavailable, operation.Key);
    }

    private async Task ConflictAsync(DurableOperationKey key, IReadOnlyDictionary<OperationId, Guid> attempts)
    {
        foreach (var attempt in attempts)
            await journal.TryFinalizeFailureAsync(key with { OperationId = attempt.Key }, attempt.Value, DurableOperationState.Failed,
                DurableOperationReason.Conflict, clock.GetUtcNow(), CancellationToken.None);
    }
    private async ValueTask<DurableExecutionResult> Observed(DurableExecutionOutcome outcome, DurableOperationKey key) =>
        new(outcome, await journal.FindAsync(key, CancellationToken.None));
}

public static class PostgreSqlImportServices
{
    public static IServiceCollection AddPostgreSqlExistingPairImport(this IServiceCollection services)
    {
        services.TryAddSingleton<PostgreSqlImportVerifier>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResourceImportPreparer, PostgreSqlImportPreparer>());
        services.TryAddSingleton<PrepareResourceImport>();
        services.TryAddSingleton<IPostgreSqlImportFaultHooks, NullPostgreSqlImportFaultHooks>();
        foreach (var type in new[] { PostgreSqlDatabaseContract.Id, PostgreSqlApplicationLoginContract.Id })
        {
            services.AddSingleton<IDurableActionAdmissionHandler>(new PostgreSqlImportAdmission(type));
            services.AddSingleton<IDurableResourceReuseVerifier>(p => new PostgreSqlImportReuseVerifier(type,
                p.GetRequiredService<PostgreSqlImportVerifier>(), p.GetRequiredService<IPostgreSqlImportFaultHooks>()));
            services.AddSingleton<IDurableActionHandler>(p => new PostgreSqlImportAction(type, p.GetRequiredService<IDurableOperationJournal>(),
                p.GetRequiredService<IExternalOperationOutputs>(), p.GetRequiredService<IResourceObservationAcceptance>(), p.GetRequiredService<PostgreSqlImportVerifier>(),
                p.GetRequiredService<IProviderConfigurationStore>(), p.GetRequiredService<IPostgreSqlImportFaultHooks>(), p.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IExternalOperationOutputContract>(p => new PostgreSqlImportOutputContract(type,
                p.GetRequiredService<PostgreSqlProviderConfigurationCodec>(), p.GetRequiredService<IApplicationCredentialReader>()));
        }
        return services;
    }
}
