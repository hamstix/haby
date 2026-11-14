using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal enum InternalActionFault { BeforeMarkerCommit, AfterMarkerCommit, AfterExternalResult, BeforeCompletionCommit, AfterCompletionCommit }
internal sealed record InternalActionView(DurableActionIntentRow Intent, DurableOperationRow Operation,
    IReadOnlyList<DurableOperationAttemptRow> Attempts, IReadOnlyList<DurableActionObservationRow> Observations, OperationOutputRow? Output);

/// <summary>Internal-only composition over the common journal. Never registered with Host/RPC.
/// Each catalog transaction ends before adapter execution or observation begins.</summary>
internal sealed partial class InternalDurableActions(
    IDbContextFactory<ManifestCatalogDbContext> contexts, IEnumerable<IInternalActionHandler> registrations,
    IEnumerable<IProviderConfigurationCodec> codecs, IEnumerable<IExternalOperationOutputContract> resourceOutputs,
    DurableActionAdmission resourceAdmission, DurableOperationSerialization serialization, TimeProvider clock,
    Action<InternalActionFault>? fault = null)
{
    private readonly IInternalActionHandler[] handlers = ValidateRegistrations(registrations);
    private readonly IProviderConfigurationCodec[] typedCodecs = codecs.ToArray();
    private static IInternalActionHandler[] ValidateRegistrations(IEnumerable<IInternalActionHandler> values)
    {
        var result = values.ToArray();
        if (result.Any(x => x.Contract is null || new[] { x.Contract.Subject, x.Contract.ProviderKind, x.Contract.Configuration,
            x.Contract.Implementation, x.Contract.Action, x.Contract.InputFormat, x.Contract.OutputContract }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 128)) ||
            result.GroupBy(x => x.Contract).Any(x => x.Count() != 1)) throw new InvalidOperationException("Ambiguous internal action registration.");
        return result;
    }
    private DateTimeOffset Now()
    {
        var at = clock.GetUtcNow(); return at.AddTicks(-(at.Ticks % TimeSpan.TicksPerMicrosecond));
    }
    internal async ValueTask<DurableOperationKey> AcceptAsync(DurableChangeSetCommit accepted, ProviderInput subject,
        string action, string request, CancellationToken token)
    {
        _ = OperationId.Create(request);
        var key = new DurableOperationKey(accepted.DeploymentId, accepted.Generation, OperationId.Create("effect-" + request));
        return await serialization.RunAsync(key, async () =>
        {
            // Metadata and exact handler checks precede Resource material preflight.
            await using (var preflight = await contexts.CreateDbContextAsync(token))
            {
                var source = await InternalGenerationAuthority.RequireAsync(preflight, accepted, token);
                var candidate = await ContextAsync(preflight, source, key, subject, action, Guid.CreateVersion7(), Guid.CreateVersion7(), token);
                var proposed = candidate.Handler.Prepare(candidate.Context);
                if (!candidate.Handler.Validate(candidate.Context, proposed)) throw new InvalidDataException("Action preparation refused.");
                if (await DurableAggregateCompletion.ReadAsync(preflight, source, token, true) is null)
                {
                    var metadata = await DurableAggregateCompletion.InspectMetadataAsync(preflight, source.DeploymentId, source.Generation,
                        resourceOutputs, typedCodecs, token, resourceAdmission, internalResourceStage: true);
                    var prepared = await DurableAggregateCompletion.PrepareAsync(preflight, metadata, token)
                        ?? throw new InvalidDataException("Resource-stage evidence unavailable.");
                    await using var tx = await preflight.Database.BeginTransactionAsync(token);
                    await DurableAggregateCompletion.LockAsync(preflight, source.DeploymentId, token);
                    source = await InternalGenerationAuthority.RequireAsync(preflight, accepted, token);
                    if (!await DurableAggregateCompletion.TryAdvanceAsync(preflight, source.DeploymentId, source.Generation, token,
                        resourceOutputs, resourceAdmission, typedCodecs, prepared, internalResourceStage: true)) throw new InvalidDataException("Resource-stage proof refused.");
                    token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None);
                }
            }
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var deployment = Guid.Parse(key.DeploymentId.ToString());
            await DurableAggregateCompletion.LockAsync(db, deployment, token);
            var generation = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
            var proof = await DurableAggregateCompletion.ReadAsync(db, generation, token, true)
                ?? throw new InvalidDataException("Resource-stage proof unavailable.");
            var old = await db.ActionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value && x.RequestId == request, token);
            if (old is not null)
            {
                if (Subject(old) != subject || old.ActionContract != action || old.OperationId != key.OperationId.Value)
                    throw new InvalidDataException("Conflicting immutable action request.");
                _ = await LoadAsync(db, key, token);
                return key;
            }
            var marker = Guid.CreateVersion7(); var managed = Guid.CreateVersion7();
            var exact = await ContextAsync(db, generation, key, subject, action, marker, managed, token);
            var payload = exact.Handler.Prepare(exact.Context);
            if (payload.Length is 0 or > 1_048_576 || System.Text.Encoding.UTF8.GetByteCount(payload) > 1_048_576 || !exact.Handler.Validate(exact.Context, payload)) throw new InvalidDataException("Action intent refused.");
            var contract = exact.Handler.Contract;
            var at = Now();
            db.DurableOperations.Add(new()
            {
                DeploymentId = deployment,
                Generation = key.Generation.Value,
                OperationId = key.OperationId.Value,
                CreatedAt = at,
                State = nameof(DurableOperationState.Pending)
            });
            db.ActionIntents.Add(new()
            {
                DeploymentId = deployment,
                Generation = key.Generation.Value,
                OperationId = key.OperationId.Value,
                RequestId = request,
                Marker = marker,
                ManagedIdentity = managed,
                FormatVersion = 1,
                SubjectKind = subject is ProviderInput.Workload ? "Workload" : "Resource",
                Component = (subject as ProviderInput.Workload)?.Component.Value ?? "",
                LocalId = subject switch { ProviderInput.Workload w => w.Id.Value, ProviderInput.Resource r => r.Id.Value, _ => throw new InvalidDataException() },
                SubjectContract = contract.Subject,
                ProviderKind = contract.ProviderKind,
                ConfigurationContract = contract.Configuration,
                Implementation = contract.Implementation,
                ActionContract = contract.Action,
                InputFormat = contract.InputFormat,
                OutputContract = contract.OutputContract,
                ProviderId = Guid.Parse(exact.Context.Provider.Instance.ToString()),
                ConfigurationRevision = exact.Context.Provider.Configuration.Value,
                Payload = payload,
                ObservationRecovery = exact.Handler.ObservationRecovery,
                CheckpointId = proof.Id,
                InputFingerprint = generation.Fingerprint,
                PlacementId = exact.Context.Provider.Placement is { } placement ? InternalPlacementInput.Decode(placement).Id : null,
                CreatedAt = at
            });
            await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested(); await transaction.CommitAsync(CancellationToken.None);
            return key;
        }, token);
    }

    internal ValueTask<InternalActionResult> ExecuteAsync(DurableOperationKey key, CancellationToken token) =>
        serialization.RunAsync(key, () => ExecuteCoreAsync(key, false, token), token);
    internal ValueTask<InternalActionResult> RecoverByObservationAsync(DurableOperationKey key, CancellationToken token) =>
        serialization.RunAsync(key, () => ExecuteCoreAsync(key, true, token), token);

    private async ValueTask<InternalActionResult> ExecuteCoreAsync(DurableOperationKey key, bool observationOnly, CancellationToken token)
    {
        Loaded loaded;
        await using (var db = await contexts.CreateDbContextAsync(token)) loaded = await LoadAsync(db, key, token);
        if (loaded.Operation.State == nameof(DurableOperationState.Succeeded)) return await TerminalAsync(key, loaded, token);
        var uncertain = loaded.Operation.State is nameof(DurableOperationState.Dispatching) or nameof(DurableOperationState.NeedsAttention);
        if (!uncertain && observationOnly) return new(InternalActionOutcome.NotConfirmed, "not-dispatched");
        if (uncertain)
        {
            if (!loaded.Handler.ObservationRecovery) return new(InternalActionOutcome.NotConfirmed, "manual-recovery-required");
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                await using var tx = await db.Database.BeginTransactionAsync(token);
                await DurableAggregateCompletion.LockAsync(db, loaded.Intent.DeploymentId, token);
                loaded = await LoadAsync(db, key, token);
                if (loaded.Operation.State == nameof(DurableOperationState.Succeeded)) return await TerminalAsync(db, loaded, token);
                if (loaded.Operation.State == nameof(DurableOperationState.Dispatching))
                    _ = await DurableOperationTransitions.RecoverAsync(db, key, Now(), token);
                token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None);
            }
            // No catalog connection is retained over the API read.
            var observed = await loaded.Handler.ObserveAsync(loaded.Context, loaded.Intent.Payload, token);
            return await PublishAsync(key, loaded, observed, true, token);
        }
        if (loaded.Operation.State is not (nameof(DurableOperationState.Pending) or nameof(DurableOperationState.Preparing))) return new(InternalActionOutcome.Refused, "terminal-failure");
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, loaded.Intent.DeploymentId, token);
            loaded = await LoadAsync(db, key, token);
            var begun = await DurableOperationTransitions.BeginAsync(db, key, Now(), token)
                ?? throw new InvalidDataException("Action attempt refused.");
            token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None);
            loaded = loaded with { Operation = begun };
        }
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, loaded.Intent.DeploymentId, token);
            var exact = await LoadAsync(db, key, token);
            if (exact.Intent.Payload != loaded.Intent.Payload || !await DurableOperationTransitions.MarkerAsync(db, key,
                loaded.Operation.CurrentAttemptId!.Value, Now(), token)) throw new InvalidDataException("Dispatch barrier refused.");
            fault?.Invoke(InternalActionFault.BeforeMarkerCommit);
            token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None);
        }
        fault?.Invoke(InternalActionFault.AfterMarkerCommit);
        var result = await loaded.Handler.ExecuteAsync(loaded.Context, loaded.Intent.Payload, token);
        fault?.Invoke(InternalActionFault.AfterExternalResult);
        if (result.Outcome == InternalActionOutcome.Confirmed) return await PublishAsync(key, loaded, result, false, token);
        // Even a pre-send or positively rejected kernel result cannot reopen this durable barrier.
        // Keeping it uncertain is conservative and permits observation, never another write.
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, loaded.Intent.DeploymentId, token);
            _ = await LoadAsync(db, key, token);
            _ = await DurableOperationTransitions.RecoverAsync(db, key, Now(), token);
            await tx.CommitAsync(CancellationToken.None);
        }
        return result with { Receipt = null };
    }

    private async ValueTask<InternalActionResult> PublishAsync(DurableOperationKey key, Loaded dispatched, InternalActionResult result, bool observation, CancellationToken token)
    {
        if (result.Code.Length is 0 or > 128 || result.Code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')))
            throw new InvalidDataException("Invalid bounded action outcome.");
        if (observation && result.Outcome is not (InternalActionOutcome.Confirmed or InternalActionOutcome.Unavailable or InternalActionOutcome.NotConfirmed or InternalActionOutcome.Refused))
            throw new InvalidDataException("Invalid observation outcome.");
        var confirmed = result.Outcome == InternalActionOutcome.Confirmed;
        if (confirmed && (result.Receipt is null || result.Receipt.Length > 1_048_576 || System.Text.Encoding.UTF8.GetByteCount(result.Receipt) > 1_048_576 ||
            !dispatched.Handler.ValidateReceipt(dispatched.Context, dispatched.Intent.Payload, result.Receipt, observation))) throw new InvalidDataException("Action receipt refused.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await DurableAggregateCompletion.LockAsync(db, dispatched.Intent.DeploymentId, token);
        var loaded = await LoadAsync(db, key, token);
        if (loaded.Operation.State == nameof(DurableOperationState.Succeeded)) return await TerminalAsync(db, loaded, token);
        if (loaded.Operation.CurrentAttemptId != dispatched.Operation.CurrentAttemptId || loaded.Intent.Payload != dispatched.Intent.Payload)
            throw new InvalidDataException("Stale action result.");
        var attempt = loaded.Operation.CurrentAttemptId ?? throw new InvalidDataException("Missing dispatched attempt.");
        var at = Now();
        if (observation)
        {
            if (loaded.Operation.State != nameof(DurableOperationState.NeedsAttention) ||
                !await db.DurableOperationAttempts.AsNoTracking().AnyAsync(x => x.AttemptId == attempt && x.DeploymentId == loaded.Intent.DeploymentId &&
                    x.Generation == key.Generation.Value && x.OperationId == key.OperationId.Value && x.Outcome == nameof(DurableAttemptOutcome.Uncertain), token))
                throw new InvalidDataException("Observation has no uncertain dispatch authority.");
            var ordinal = (await db.ActionObservations.Where(x => x.DeploymentId == loaded.Intent.DeploymentId && x.Generation == key.Generation.Value &&
                x.OperationId == key.OperationId.Value).MaxAsync(x => (long?)x.Ordinal, token) ?? 0) + 1;
            db.ActionObservations.Add(new()
            {
                Id = Guid.CreateVersion7(),
                DeploymentId = loaded.Intent.DeploymentId,
                Generation = key.Generation.Value,
                OperationId = key.OperationId.Value,
                AttemptId = attempt,
                Ordinal = ordinal,
                At = at,
                Confirmed = confirmed,
                FormatVersion = 1,
                Outcome = result.Outcome.ToString(),
                Code = result.Code,
                Receipt = confirmed ? result.Receipt : null
            });
        }
        if (confirmed)
        {
            if (!loaded.Handler.ValidateReceipt(loaded.Context, loaded.Intent.Payload, result.Receipt!, observation) ||
                !await DurableOperationTransitions.ConfirmAsync(db, key, attempt, at, observation, token)) throw new InvalidDataException("Action completion refused.");
            db.OperationOutputs.Add(new()
            {
                DeploymentId = loaded.Intent.DeploymentId,
                Generation = key.Generation.Value,
                OperationId = key.OperationId.Value,
                Contract = loaded.Intent.OutputContract,
                Key = "receipt",
                Payload = result.Receipt!
            });
        }
        await db.SaveChangesAsync(token); fault?.Invoke(InternalActionFault.BeforeCompletionCommit);
        token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None);
        fault?.Invoke(InternalActionFault.AfterCompletionCommit);
        return confirmed ? new(InternalActionOutcome.Confirmed, "confirmed", result.Receipt) : result with { Receipt = null };
    }

    private async ValueTask<InternalActionResult> TerminalAsync(DurableOperationKey key, Loaded loaded, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token); return await TerminalAsync(db, loaded, token);
    }
    private static async ValueTask<InternalActionResult> TerminalAsync(ManifestCatalogDbContext db, Loaded loaded, CancellationToken token)
    {
        var output = await db.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == loaded.Intent.DeploymentId &&
            x.Generation == loaded.Intent.Generation && x.OperationId == loaded.Intent.OperationId, token);
        var observation = await db.ActionObservations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == loaded.Intent.DeploymentId &&
            x.Generation == loaded.Intent.Generation && x.OperationId == loaded.Intent.OperationId && x.Confirmed, token);
        var attempt = await db.DurableOperationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == loaded.Operation.CurrentAttemptId, token);
        if (output is null || output.Contract != loaded.Intent.OutputContract || output.Key != "receipt" ||
            attempt is null || attempt.DeploymentId != loaded.Intent.DeploymentId || attempt.Generation != loaded.Intent.Generation || attempt.OperationId != loaded.Intent.OperationId ||
            attempt.AttemptNumber != loaded.Operation.AttemptNumber || attempt.DispatchedAt is null || attempt.FinishedAt is null ||
            (observation is null ? attempt.Outcome != nameof(DurableAttemptOutcome.Succeeded) :
                attempt.Outcome != nameof(DurableAttemptOutcome.Uncertain) || observation.AttemptId != attempt.AttemptId || observation.Receipt != output.Payload) ||
            !loaded.Handler.ValidateReceipt(loaded.Context, loaded.Intent.Payload, output.Payload, observation is not null)) throw new InvalidDataException("Terminal action evidence unavailable.");
        return new(InternalActionOutcome.Confirmed, "confirmed", output.Payload);
    }
    internal async ValueTask<InternalActionView?> HistoryAsync(DurableOperationKey key, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        var intent = await db.ActionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value && x.OperationId == key.OperationId.Value, token);
        if (intent is null) return null;
        return new(intent, await DurableOperationTransitions.Operation(db, key).AsNoTracking().SingleAsync(token),
            await db.DurableOperationAttempts.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value && x.OperationId == key.OperationId.Value).OrderBy(x => x.AttemptNumber).ToArrayAsync(token),
            await db.ActionObservations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value && x.OperationId == key.OperationId.Value).OrderBy(x => x.Ordinal).ToArrayAsync(token),
            await db.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value && x.OperationId == key.OperationId.Value, token));
    }
}
