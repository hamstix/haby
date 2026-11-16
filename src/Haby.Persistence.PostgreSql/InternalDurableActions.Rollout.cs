using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    private sealed record ObservationCall(SubmittedSet Set, GenerationObservationRecordRow Request, RolloutResult? Replay, int Offset, bool PublicWait = false, CancellationToken LifecycleToken = default);
    private sealed record ReadReservation(Guid? Id, bool Expired, bool Recovery, string? Refusal = null);
    private static DurableOperationKey RolloutKey(DurableChangeSetCommit accepted) => new(accepted.DeploymentId, accepted.Generation, OperationId.Create("rollout"));
    internal async ValueTask<RolloutResult> ObserveRolloutAsync(DurableChangeSetCommit accepted, string request,
        RolloutOptions options, CancellationToken token)
    {
        options.Validate(); _ = OperationId.Create(request);
        var started = clock.GetTimestamp();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMilliseconds(options.RequestMilliseconds));
        return await serialization.RunAsync(RolloutKey(accepted), () => ObserveRolloutCoreAsync(accepted, request, options, started, token, bounded.Token), bounded.Token);
    }
    // Trusted public composition supplies the original total-budget timestamp and token.
    internal ValueTask<RolloutResult> ObserveOperatorRolloutAsync(DurableChangeSetCommit accepted, string request, string actor,
        RolloutOptions options, long started, TimeSpan totalBudget, CancellationToken callerToken, CancellationToken token,
        BatchMemberObservation? batch = null) =>
        serialization.RunAsync(RolloutKey(accepted), () => ObserveRolloutCoreAsync(accepted, request, options, started,
            callerToken, token, actor, true, totalBudget, batch), token);

    private async ValueTask<ObservationCall> BeginObservationCallAsync(DurableChangeSetCommit accepted, string request,
        RolloutOptions? options, RolloutResume[]? resumes, string actor, CancellationToken token, bool publicRequest = false, BatchReadBudget? batch = null)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await DurableAggregateCompletion.LockAsync(db, Guid.Parse(accepted.DeploymentId.ToString()), token);
        var source = await InternalGenerationAuthority.RequireReadAsync(db, accepted, token);
        var targets = Targets(source);
        var members = resumes ?? targets.Select(x => new RolloutResume(x, Guid.Empty)).ToArray();
        var input = RolloutJson.Encode(new RolloutRequest(resumes is null ? "Observe" : "Resume", actor, options, members));
        if (Encoding.UTF8.GetByteCount(input) > 131072) throw new InvalidDataException("Observation request too large.");
        var previous = await db.ObservationRecords.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Kind == "Request" && x.Request == request, token);
        if (previous is not null && await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == source.DeploymentId && x.AppliedGeneration >= source.Generation, token))
        {
            if (previous.InputJson != input) throw publicRequest ? new RolloutRequestConflictException() : new InvalidDataException("Conflicting observation idempotency request.");
            var completed = await InitialRetirementCompletion.RequireProofAsync(db, source, token);
            var historical = previous.Payload is not null ? RolloutJson.Decode(previous.Payload) : new RolloutResult(false, completed.Submitted,
                completed.SavedReady, RolloutStop.Interrupted, targets.Select(x => new RolloutMember(x, null, null, RolloutBudget.Active,
                    new(RolloutOutcome.NotObserved, "not-observed"))).ToArray(), Retirement: completed.Id, AppliedGeneration: source.Generation);
            if (historical.Submitted is { } submitted && submitted != completed.Submitted || historical.SavedReady is { } ready && ready != completed.SavedReady ||
                historical.Applied && (historical.Retirement != completed.Id || historical.AppliedGeneration != source.Generation))
                throw new InvalidDataException("Historical completion reference mismatch.");
            return new(new(source, targets, [], historical.Submitted), previous, historical with { Replayed = true }, 0);
        }
        var set = await CaptureSubmittedAsync(db, source, token, completedRead: true);
        if (previous is not null)
        {
            if (previous.InputJson != input) throw publicRequest ? new RolloutRequestConflictException() : new InvalidDataException("Conflicting observation idempotency request.");
            var saved = await SavedReadyAsync(db, set, token);
            var replay = previous.Payload is null ? Result(set, saved?.Id, RolloutStop.Interrupted, new Dictionary<string, RolloutObservation>()) : RolloutJson.Decode(previous.Payload);
            return new(set, previous, replay with { Replayed = true }, 0); // No duplicate network work, including interrupted reservations.
        }
        var calls = await db.ObservationRecords.CountAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.Kind == "Request", token);
        var row = new GenerationObservationRecordRow
        {
            Id = Guid.CreateVersion7(),
            DeploymentId = source.DeploymentId,
            Generation = source.Generation,
            Request = request,
            Kind = "Request",
            InputJson = input,
            ReservedAt = Now()
        };
        db.ObservationRecords.Add(row);
        await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested(); await CommitRolloutAsync(tx, token, batch);
        return new(set, row, null, calls % set.Targets.Length, publicRequest, token);
    }
    private RolloutResult Result(SubmittedSet set, Guid? ready, RolloutStop stop, IReadOnlyDictionary<string, RolloutObservation> observations)
    {
        var now = Now();
        var members = set.Targets.Select(subject =>
        {
            var member = set.Members.SingleOrDefault(x => x.Subject == subject);
            return new RolloutMember(subject, member?.Attempt.Id, member?.Attempt.Deadline,
                member is not null && RolloutDeadlines.Expired(now, member.Attempt.Deadline, member.Expired) ? RolloutBudget.Expired : RolloutBudget.Active,
                observations.TryGetValue(subject, out var value) ? value : new(member is null ? RolloutOutcome.NotSubmitted : RolloutOutcome.NotObserved,
                    member is null ? "not-submitted" : "not-observed"));
        }).ToArray();
        return new(set.Checkpoint is not null && members.All(x => x.Observation.Outcome == RolloutOutcome.Ready), set.Checkpoint, ready, stop, members);
    }
    private async ValueTask<RolloutResult> ObserveRolloutCoreAsync(DurableChangeSetCommit accepted, string request,
        RolloutOptions options, long started, CancellationToken callerToken, CancellationToken callToken,
        string actor = "observer", bool publicRequest = false, TimeSpan? totalBudget = null, BatchMemberObservation? batch = null)
    {
        callToken.ThrowIfCancellationRequested();
        var requestBudget = totalBudget ?? TimeSpan.FromMilliseconds(options.RequestMilliseconds);
        if (clock.GetElapsedTime(started) >= requestBudget) throw new OperationCanceledException(callToken);
        batch?.Start();
        var call = await BeginObservationCallAsync(accepted, request, options, null, actor, callToken, publicRequest, batch?.Budget);
        if (call.Replay is not null) return call.Replay;
        var set = call.Set;
        var observations = new Dictionary<string, RolloutObservation>(StringComparer.Ordinal);
        var reads = 0; var stop = RolloutStop.CallLimit;
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var expiredMembers = set.Members.Where(x => x.Expired).Select(x => x.Subject).ToHashSet(StringComparer.Ordinal);
        var recoveryConsumed = new HashSet<string>(StringComparer.Ordinal);
        var budgetStarted = clock.GetTimestamp();
        var remainingBudgets = set.Members.ToDictionary(x => x.Subject, x => x.Attempt.Deadline - Now(), StringComparer.Ordinal);
        var ordered = set.Members.OrderBy(x => (Array.IndexOf(set.Targets, x.Subject) - call.Offset + set.Targets.Length) % set.Targets.Length).ToArray();
        for (var round = 1; round <= options.MaximumRounds; round++)
        {
            var samples = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var member in ordered)
            {
                if (finished.Contains(member.Subject)) continue;
                if (reads >= options.MaximumCalls) break;
                if (callToken.IsCancellationRequested) break;
                var remainingCall = requestBudget - clock.GetElapsedTime(started);
                if (remainingCall <= TimeSpan.Zero) break;
                var monotonicRemaining = remainingBudgets[member.Subject] - clock.GetElapsedTime(budgetStarted);
                var knownExpired = expiredMembers.Contains(member.Subject);
                var remainingRollout = member.Attempt.Deadline - Now();
                if (remainingRollout > monotonicRemaining) remainingRollout = monotonicRemaining;
                var readLimit = TimeSpan.FromMilliseconds(options.ReadTimeoutMilliseconds);
                if (readLimit > remainingCall) readLimit = remainingCall;
                var activeLimit = remainingRollout < readLimit ? remainingRollout : readLimit;
                var readStarted = clock.GetTimestamp();
                // Both timers include reservation time. Recovery uses the existing read cap, never a restarted timer.
                using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(callToken); readBudget.CancelAfter(readLimit);
                using var activeBudget = CancellationTokenSource.CreateLinkedTokenSource(readBudget.Token);
                activeBudget.CancelAfter(activeLimit > TimeSpan.Zero ? activeLimit : TimeSpan.FromMilliseconds(1));
                var reservation = await ReserveReadAsync(call, member, round, knownExpired, recoveryConsumed.Contains(member.Subject),
                    monotonicRemaining > TimeSpan.Zero, callToken, publicRequest, batch?.Budget);
                if (reservation.Expired) expiredMembers.Add(member.Subject);
                if (reservation.Recovery) recoveryConsumed.Add(member.Subject);
                if (reservation.Id is null)
                {
                    if (reservation.Refusal is { } refusal) observations[member.Subject] = new(RolloutOutcome.Refused, refusal);
                    else if (!observations.ContainsKey(member.Subject)) observations[member.Subject] = new(RolloutOutcome.Unavailable, "clock-budget-elapsed");
                    finished.Add(member.Subject); continue;
                }
                var readToken = reservation.Recovery ? readBudget.Token : activeBudget.Token;
                RolloutObservation observed;
                reads++;
                try
                {
                    readToken.ThrowIfCancellationRequested();
                    if (!reservation.Recovery && !RolloutDeadlines.CanReadActive(clock, readStarted, activeLimit, member.Attempt.Deadline))
                        throw new OperationCanceledException(readToken);
                    var handler = (IInternalReadinessHandler)member.Loaded.Handler;
                    observed = batch is null
                        ? await handler.ReadinessAsync(member.Loaded.Context, member.Loaded.Intent.Payload, member.Receipt, readToken)
                        : await handler.ReadinessAsync(member.Loaded.Context, member.Loaded.Intent.Payload, member.Receipt,
                            batch.Budget.Admission(ct => ReauthorizeReadAsync(call, member, ct), () =>
                            {
                                if (clock.GetElapsedTime(readStarted) >= readLimit) readBudget.Cancel();
                                if (!reservation.Recovery && !RolloutDeadlines.CanReadActive(clock, readStarted, activeLimit, member.Attempt.Deadline)) activeBudget.Cancel();
                                readToken.ThrowIfCancellationRequested();
                            }), readToken);
                    if (!((IInternalReadinessHandler)member.Loaded.Handler).ValidateObservation(member.Loaded.Intent.Payload, member.Receipt, observed))
                        throw new InvalidDataException("Invalid adapter readiness evidence.");
                }
                catch (OperationCanceledException)
                {
                    observed = new(RolloutOutcome.Unavailable, callToken.IsCancellationRequested ? "call-stopped" : "read-timeout", true);
                }
                catch (RolloutReadRefusedException) { observed = new(RolloutOutcome.Refused, "runtime-authority-unavailable"); }
                catch (RolloutBatchExhaustedException)
                {
                    return Result(set, null, RolloutStop.CallLimit, observations) with { Ready = false };
                }
                // Single-generation closure has a finite grace; batch closure retains the original deadline.
                using var publish = ClosureBudget();
                var publication = await PublishReadAsync(call, member, reservation.Id.Value, observed, samples, observations, publish.Token, batch?.Budget);
                if (publication.Expired) expiredMembers.Add(member.Subject);
                if (publication.Completed is not null) return publication.Completed;
                var current = publication.Current;
                observations[member.Subject] = current ? observed : new(RolloutOutcome.Refused, "attempt-superseded");
                if (current) samples[member.Subject] = reservation.Id.Value;
                if (recoveryConsumed.Contains(member.Subject) || !current || observed.Code == "status-invalid" || observed.Outcome is RolloutOutcome.Conflict or RolloutOutcome.Refused ||
                    observed.Outcome == RolloutOutcome.Unavailable && !observed.Temporary) finished.Add(member.Subject);
            }
            if (callToken.IsCancellationRequested || clock.GetElapsedTime(started) >= requestBudget)
            { stop = callerToken.IsCancellationRequested ? RolloutStop.Canceled : RolloutStop.RequestDeadline; break; }
            if (reads >= options.MaximumCalls || round == options.MaximumRounds) break;
            if (ordered.All(x => finished.Contains(x.Subject))) { stop = RolloutStop.Complete; break; }
            // Ready members must participate again in a complete fresh round; historical cross-call assembly is forbidden.
            try { await Task.Delay(TimeSpan.FromMilliseconds(options.PollMilliseconds), clock, callToken); }
            catch (OperationCanceledException) { stop = callerToken.IsCancellationRequested ? RolloutStop.Canceled : RolloutStop.RequestDeadline; break; }
        }
        using var finalBudget = ClosureBudget();
        return await FinishObservationCallAsync(call, observations, stop, finalBudget.Token, batch?.Budget);

        CancellationTokenSource ClosureBudget() => batch is null
            ? new CancellationTokenSource(TimeSpan.FromSeconds(10)) : CancellationTokenSource.CreateLinkedTokenSource(callToken);
    }
    private static async Task CommitRolloutAsync(IDbContextTransaction transaction, CancellationToken token, BatchReadBudget? batch)
    {
        if (batch is not null)
        {
            token.ThrowIfCancellationRequested();
            if (batch.Remaining <= TimeSpan.Zero) throw new OperationCanceledException(token);
        }
        // Cancellation after dispatch does not establish rollback. The caller retains interrupted/unknown history.
        await transaction.CommitAsync(batch is null ? CancellationToken.None : token);
    }
    private async ValueTask ReauthorizeReadAsync(ObservationCall call, SubmittedMember member, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await DurableAggregateCompletion.LockAsync(db, call.Set.Source.DeploymentId, token);
        var latest = await db.ObservationAttempts.AsNoTracking().Where(x => x.DeploymentId == call.Set.Source.DeploymentId &&
            x.Generation == call.Set.Source.Generation && x.Subject == member.Subject).OrderByDescending(x => x.Ordinal).FirstAsync(token);
        if (latest.Id != member.Attempt.Id) throw new RolloutReadRefusedException();
        try { await RequireCurrentRuntimeAsync(db, member.Loaded.Context, token); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { throw new RolloutReadRefusedException(); }
        token.ThrowIfCancellationRequested();
        // Read-only transaction: disposal releases its lock before the admission hook invokes the API.
    }
    private async Task<ReadReservation> ReserveReadAsync(ObservationCall call, SubmittedMember member, int round,
        bool knownExpired, bool recoveryConsumed, bool activeAllowed, CancellationToken token, bool publicRequest = false, BatchReadBudget? batch = null)
    {
        await using var db = await contexts.CreateDbContextAsync(token); await using var tx = await db.Database.BeginTransactionAsync(token);
        await DurableAggregateCompletion.LockAsync(db, call.Set.Source.DeploymentId, token);
        var latest = await db.ObservationAttempts.AsNoTracking().Where(x => x.DeploymentId == call.Set.Source.DeploymentId &&
            x.Generation == call.Set.Source.Generation && x.Subject == member.Subject).OrderByDescending(x => x.Ordinal).FirstAsync(token);
        if (latest.Id != member.Attempt.Id)
        {
            if (publicRequest) return new(null, false, false, "attempt-superseded");
            throw new InvalidDataException("Observation attempt superseded before read.");
        }
        if (publicRequest)
        {
            try { await RequireCurrentRuntimeAsync(db, member.Loaded.Context, token); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            { return new(null, false, false, "runtime-authority-unavailable"); }
        }
        token.ThrowIfCancellationRequested();
        var expired = await ExpireAsync(db, member.Attempt, call.Request.Request, token) || knownExpired;
        if (expired && recoveryConsumed || !expired && !activeAllowed)
        {
            await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested(); await CommitRolloutAsync(tx, token, batch);
            return new(null, expired, false);
        }
        var row = new GenerationObservationRecordRow
        {
            Id = Guid.CreateVersion7(),
            DeploymentId = call.Set.Source.DeploymentId,
            Generation = call.Set.Source.Generation,
            AttemptId = member.Attempt.Id,
            Subject = member.Subject,
            Request = call.Request.Request,
            Kind = "Read",
            Ordinal = round,
            ReservedAt = Now(),
            InputJson = expired ? "recovery" : "active"
        };
        db.ObservationRecords.Add(row); await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested(); await CommitRolloutAsync(tx, token, batch);
        return new(row.Id, expired, expired);
    }
    private async Task<bool> ExpireAsync(ManifestCatalogDbContext db, GenerationObservationAttemptRow attempt, string request, CancellationToken token)
    {
        if (db.ObservationRecords.Local.Any(x => x.AttemptId == attempt.Id && x.Kind == "Expired") ||
            await db.ObservationRecords.AnyAsync(x => x.AttemptId == attempt.Id && x.Kind == "Expired", token)) return true;
        if (Now() < attempt.Deadline) return false;
        db.ObservationRecords.Add(new()
        {
            Id = Guid.CreateVersion7(),
            DeploymentId = attempt.DeploymentId,
            Generation = attempt.Generation,
            Subject = attempt.Subject,
            AttemptId = attempt.Id,
            Request = request,
            Kind = "Expired",
            ReservedAt = attempt.Deadline,
            FinishedAt = Now(),
            Payload = "expired"
        });
        return true;
    }
    private async Task<(bool Current, bool Expired, RolloutResult? Completed)> PublishReadAsync(ObservationCall call, SubmittedMember member, Guid reservation,
        RolloutObservation observation, Dictionary<string, Guid> samples, Dictionary<string, RolloutObservation> observations, CancellationToken token, BatchReadBudget? batch = null)
    {
        var payload = RolloutJson.Encode(observation);
        if (Encoding.UTF8.GetByteCount(payload) > 16384) throw new InvalidDataException("Observation exceeds bound.");
        await using var db = await contexts.CreateDbContextAsync(token); await using var tx = await db.Database.BeginTransactionAsync(token);
        await DurableAggregateCompletion.LockAsync(db, call.Set.Source.DeploymentId, token);
        var expired = await ExpireAsync(db, member.Attempt, call.Request.Request, token);
        if (await db.ObservationRecords.Where(x => x.Id == reservation && x.AttemptId == member.Attempt.Id && x.FinishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FinishedAt, Now()).SetProperty(x => x.Payload, payload), token) != 1)
            throw new InvalidDataException("Stale read reservation.");
        var latest = await db.ObservationAttempts.AsNoTracking().Where(x => x.DeploymentId == call.Set.Source.DeploymentId &&
            x.Generation == call.Set.Source.Generation && x.Subject == member.Subject).OrderByDescending(x => x.Ordinal).FirstAsync(token);
        RolloutResult? completed = null;
        if (latest.Id == member.Attempt.Id)
        {
            samples[member.Subject] = reservation; observations[member.Subject] = observation;
            completed = await FinishRoundAsync(db, call, samples, observations, token);
        }
        await db.SaveChangesAsync(token);
        if (completed?.Applied == true) call.LifecycleToken.ThrowIfCancellationRequested();
        await CommitRolloutAsync(tx, token, batch); return (latest.Id == member.Attempt.Id, expired, completed);
    }
    private async Task<RolloutResult?> FinishRoundAsync(ManifestCatalogDbContext db, ObservationCall call, Dictionary<string, Guid> samples,
        Dictionary<string, RolloutObservation> observations, CancellationToken token)
    {
        if (call.Set.Checkpoint is null || samples.Count != call.Set.Targets.Length || observations.Count != call.Set.Targets.Length ||
            observations.Values.Any(x => x.Outcome != RolloutOutcome.Ready)) return null;
        var current = await CaptureSubmittedAsync(db, call.Set.Source, token, completedRead: true);
        if (current.Members.Any(x => call.Set.Members.Single(old => old.Subject == x.Subject).Attempt.Id != x.Attempt.Id)) return null;
        var saved = await SavedReadyAsync(db, current, token);
        var proof = new ReadyProof(1, "RolloutReady", current.Source.DeploymentId, current.Source.Generation, current.Source.Fingerprint,
            current.Checkpoint!.Value, current.Members.Select(x => new ReadyMember(x.Origin, x.Attempt.Id, samples[x.Subject])).ToArray());
        var id = saved?.Id ?? await CheckpointAsync(db, current.Source, "RolloutReady", RolloutJson.Encode(proof), token);
        fault?.Invoke(InternalActionFault.BeforeReadinessCheckpoint);
        var result = Result(current, id, RolloutStop.Complete, observations);
        await db.SaveChangesAsync(token);
        if (call.PublicWait)
        {
            var transaction = db.Database.CurrentTransaction!;
            await transaction.CreateSavepointAsync("initial_completion", token);
            try { result = await CompleteInitialAsync(db, call, current, proof, result, token); }
            catch (InvalidDataException)
            {
                await transaction.RollbackToSavepointAsync("initial_completion", token);
                foreach (var entry in db.ChangeTracker.Entries<GenerationStageCheckpointRow>().Where(x => x.Entity.Stage == InitialRetirementCompletion.Stage).ToArray()) entry.State = EntityState.Detached;
                result = result with { CompletionReason = "prerequisite-incomplete" };
            }
            await transaction.ReleaseSavepointAsync("initial_completion", token);
        }
        await CloseRequestAsync(db, call.Request, result, token);
        return result; // The caller commits last read closure, complete proof and request result atomically.
    }
    private async Task<RolloutResult> FinishObservationCallAsync(ObservationCall call, Dictionary<string, RolloutObservation> observations, RolloutStop stop, CancellationToken token, BatchReadBudget? batch = null)
    {
        await using var db = await contexts.CreateDbContextAsync(token); await using var tx = await db.Database.BeginTransactionAsync(token);
        await DurableAggregateCompletion.LockAsync(db, call.Set.Source.DeploymentId, token);
        var current = await CaptureSubmittedAsync(db, call.Set.Source, token, completedRead: true);
        foreach (var member in current.Members)
        {
            await ExpireAsync(db, member.Attempt, call.Request.Request, token);
            if (call.Set.Members.SingleOrDefault(x => x.Subject == member.Subject)?.Attempt.Id != member.Attempt.Id)
                observations[member.Subject] = new(RolloutOutcome.Refused, "attempt-superseded");
        }
        var saved = await SavedReadyAsync(db, current, token);
        var result = Result(current, saved?.Id, stop, observations) with { Ready = false };
        await CloseRequestAsync(db, call.Request, result, token); await db.SaveChangesAsync(token); await CommitRolloutAsync(tx, token, batch); return result;
    }
    private async Task CloseRequestAsync(ManifestCatalogDbContext db, GenerationObservationRecordRow request, RolloutResult result, CancellationToken token)
    {
        var payload = RolloutJson.Encode(result);
        if (Encoding.UTF8.GetByteCount(payload) > 2097152) throw new InvalidDataException("Observation response too large.");
        if (await db.ObservationRecords.Where(x => x.Id == request.Id && x.Kind == "Request" && x.FinishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FinishedAt, Now()).SetProperty(x => x.Payload, payload), token) != 1)
            throw new InvalidDataException("Observation request already closed.");
    }
    internal ValueTask<RolloutResult> ResumeRolloutAsync(DurableChangeSetCommit accepted, string request, string actor,
        IReadOnlyList<RolloutResume> members, CancellationToken token) => ResumeRolloutAsync(accepted, request, actor, members, token, false);

    internal ValueTask<RolloutResult> ResumeOperatorRolloutAsync(DurableChangeSetCommit accepted, string request, string actor,
        IReadOnlyList<RolloutResume> members, long started, TimeSpan totalBudget, CancellationToken token) =>
        ResumeRolloutAsync(accepted, request, actor, members, token, true, started, totalBudget);

    private ValueTask<RolloutResult> ResumeRolloutAsync(DurableChangeSetCommit accepted, string request, string actor,
        IReadOnlyList<RolloutResume> members, CancellationToken token, bool publicRequest, long started = 0, TimeSpan totalBudget = default)
    {
        _ = OperationId.Create(request);
        if (string.IsNullOrWhiteSpace(actor) || actor.Any(char.IsControl) || Encoding.UTF8.GetByteCount(actor) > 256 || members.Count is < 1 or > 512 ||
            members.Select(x => x.Subject).Distinct(StringComparer.Ordinal).Count() != members.Count) throw new ArgumentException("Invalid resume request.");
        return serialization.RunAsync(RolloutKey(accepted), async () =>
        {
            // Reserve and append resume authority atomically; never call an executor or the external API.
            await using var db = await contexts.CreateDbContextAsync(token); await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, Guid.Parse(accepted.DeploymentId.ToString()), token);
            if (publicRequest && clock.GetElapsedTime(started) >= totalBudget) throw new OperationCanceledException(token);
            var set = await CaptureSubmittedAsync(db, await InternalGenerationAuthority.RequireReadAsync(db, accepted, token), token, completedRead: true);
            var input = RolloutJson.Encode(new RolloutRequest("Resume", actor, null, members.OrderBy(x => x.Subject, StringComparer.Ordinal).ToArray()));
            var old = await db.ObservationRecords.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == set.Source.DeploymentId &&
                x.Generation == set.Source.Generation && x.Kind == "Request" && x.Request == request, token);
            if (old is not null)
            {
                if (old.InputJson != input || old.Payload is null) throw publicRequest ? new RolloutRequestConflictException() : new InvalidDataException("Conflicting resume request.");
                return RolloutJson.Decode(old.Payload) with { Replayed = true };
            }
            try { _ = await InternalGenerationAuthority.RequireAsync(db, accepted, token); }
            catch (InvalidDataException) when (publicRequest) { throw new RolloutPermissionException(); }
            if (Encoding.UTF8.GetByteCount(input) > 131072) throw new ArgumentException("Resume request too large.");
            if (publicRequest)
            {
                try
                {
                    foreach (var member in set.Members.Where(x => members.Any(m => m.Subject == x.Subject)))
                        await RequireCurrentRuntimeAsync(db, member.Loaded.Context, token);
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
                { throw new RolloutPermissionException(); }
            }
            var next = set.Members.ToDictionary(x => x.Subject, StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!next.TryGetValue(member.Subject, out var original) || original.Attempt.Id != member.ExpectedAttempt ||
                    !RolloutDeadlines.Expired(Now(), original.Attempt.Deadline, original.Expired))
                    throw publicRequest ? new RolloutRequestConflictException() : new InvalidDataException("Resume requires the current expired submitted attempt.");
                await ExpireAsync(db, original.Attempt, request, token);
                var attempt = NewObservationAttempt(set.Source, member.Subject, original.Loaded.Intent.OperationId, RolloutJson.Encode(original.Origin),
                    checked(original.Attempt.Ordinal + 1), original.Attempt.Id, Now(), original.Attempt.BudgetSeconds, actor);
                db.ObservationAttempts.Add(attempt); next[member.Subject] = original with { Attempt = attempt, Expired = false };
            }
            var saved = await SavedReadyAsync(db, set, token);
            var result = Result(set with { Members = set.Targets.Where(next.ContainsKey).Select(x => next[x]).ToArray() }, saved?.Id,
                RolloutStop.Complete, new Dictionary<string, RolloutObservation>());
            var payload = RolloutJson.Encode(result);
            if (Encoding.UTF8.GetByteCount(payload) > 2097152) throw new InvalidDataException("Observation response too large.");
            db.ObservationRecords.Add(new()
            {
                Id = Guid.CreateVersion7(),
                DeploymentId = set.Source.DeploymentId,
                Generation = set.Source.Generation,
                Kind = "Request",
                Request = request,
                InputJson = input,
                ReservedAt = Now(),
                FinishedAt = Now(),
                Payload = payload
            });
            await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None); return result;
        }, token);
    }
}
