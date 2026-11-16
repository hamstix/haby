using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    private sealed record SubmittedMember(string Subject, Loaded Loaded, string Receipt, RolloutOrigin Origin,
        GenerationObservationAttemptRow Attempt, bool Expired = false);
    private sealed record SubmittedSet(DeploymentGenerationRow Source, string[] Targets, SubmittedMember[] Members, Guid? Checkpoint);
    private static string Logical(ProviderInput.Workload value) => "Workload/" + value.Component.Value + "/" + value.Id.Value;
    private static string[] Targets(DeploymentGenerationRow source)
    {
        var inputs = source.Inputs();
        var targets = inputs.Configuration!.Manifest.Spec.Components.SelectMany(c => c.Value.Workloads.Keys.Select(w =>
            Logical(new(c.Key, w)))).Order(StringComparer.Ordinal).ToArray();
        // Retain the accepted configuration-preparation recipient bound; never silently truncate a set.
        if (targets.Length is 0 or > 512 || !targets.SequenceEqual(inputs.Providers.Entries.Keys.OfType<ProviderInput.Workload>()
            .Select(Logical).Order(StringComparer.Ordinal))) throw new InvalidDataException("Unsupported observation target set.");
        return targets;
    }
    private Task<SubmittedSet> CaptureSubmittedAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token) =>
        InspectSubmittedAsync(db, source, true, token);

    private async Task<SubmittedSet> InspectSubmittedAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, bool persist, CancellationToken token)
    {
        source = await InternalGenerationAuthority.RequireAsync(db, source, token);
        var targets = Targets(source); var confirmed = new List<SubmittedMember>();
        var intents = await db.ActionIntents.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.SubjectKind == "Workload").ToArrayAsync(token);
        foreach (var subject in targets)
        {
            if (intents.Any(x => Logical((ProviderInput.Workload)Subject(x)) == subject && !handlers.Any(h =>
                h.Contract == new InternalActionContract(x.SubjectContract, x.ProviderKind, x.ConfigurationContract,
                    x.Implementation, x.ActionContract, x.InputFormat, x.OutputContract))))
                throw new InvalidDataException("Exact submitted action handler unavailable.");
            var candidates = intents.Where(x => Logical((ProviderInput.Workload)Subject(x)) == subject && handlers.Any(h =>
                h is IInternalReadinessHandler && h.Contract == new InternalActionContract(x.SubjectContract, x.ProviderKind, x.ConfigurationContract,
                    x.Implementation, x.ActionContract, x.InputFormat, x.OutputContract))).ToArray();
            if (candidates.Length == 0) continue;
            if (candidates.Length != 1) throw new InvalidDataException("Ambiguous submitted action.");
            var candidate = candidates[0];
            var loaded = await LoadAsync(db, new(ApplicationDeploymentId.Parse(source.DeploymentId.ToString()),
                DeploymentGeneration.Create(source.Generation), OperationId.Create(candidate.OperationId)), token);
            if (loaded.Operation.State != nameof(DurableOperationState.Succeeded)) continue;
            var result = await TerminalAsync(db, loaded, token); // Catalog-only proof; no terminal replay I/O.
            var observation = await db.ActionObservations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
                x.Generation == source.Generation && x.OperationId == candidate.OperationId && x.Confirmed, token);
            var origin = new RolloutOrigin(subject, candidate.ManagedIdentity, candidate.OperationId, loaded.Operation.CurrentAttemptId!.Value,
                observation?.Id, candidate.OutputContract, ResourcesReadyCheckpointCodec.Hash(result.Receipt!));
            var handler = (IInternalReadinessHandler)loaded.Handler;
            var seconds = handler.ObservationSeconds(candidate.Payload, result.Receipt!);
            if (seconds is < 1 or > 86400 || loaded.Operation.FinalizedAt is null) throw new InvalidDataException("Invalid submitted observation budget.");
            var initial = await db.ObservationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
                x.Generation == source.Generation && x.Subject == subject && x.Ordinal == 1, token);
            var originJson = RolloutJson.Encode(origin);
            if (initial is null)
            {
                initial = NewObservationAttempt(source, subject, candidate.OperationId, originJson, 1, null,
                    loaded.Operation.FinalizedAt.Value, seconds, source.ActorId);
                if (persist) db.ObservationAttempts.Add(initial);
            }
            ValidateAttempt(initial, source, candidate.OperationId, originJson, seconds);
            if (initial.StartedAt != loaded.Operation.FinalizedAt || initial.Previous is not null) throw new InvalidDataException("Initial deadline anchor mismatch.");
            var latest = await db.ObservationAttempts.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation &&
                x.Subject == subject).OrderByDescending(x => x.Ordinal).FirstOrDefaultAsync(token) ?? initial;
            ValidateAttempt(latest, source, candidate.OperationId, originJson, seconds);
            var expiry = db.ObservationRecords.Local.SingleOrDefault(x => x.AttemptId == latest.Id && x.Kind == "Expired") ??
                await db.ObservationRecords.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == latest.Id && x.Kind == "Expired", token);
            if (expiry is not null && (expiry.FinishedAt is null || expiry.Payload != "expired" || expiry.Subject != subject ||
                expiry.DeploymentId != source.DeploymentId || expiry.Generation != source.Generation || expiry.ReservedAt != latest.Deadline))
                throw new InvalidDataException("Retained observation expiry mismatch.");
            confirmed.Add(new(subject, loaded, result.Receipt!, origin, latest, expiry is not null));
        }
        Guid? checkpoint = null;
        if (confirmed.Count == targets.Length)
        {
            var evidence = RolloutJson.Encode(new SubmittedProof(1, "WorkloadsSubmitted", source.DeploymentId, source.Generation,
                source.Fingerprint, confirmed.Select(x => x.Origin).ToArray()));
            if (persist) checkpoint = await CheckpointAsync(db, source, "WorkloadsSubmitted", evidence, token);
            else
            {
                var saved = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
                    x.Generation == source.Generation && x.Stage == "WorkloadsSubmitted", token);
                if (saved is not null)
                {
                    if (saved.FormatVersion != 1 || saved.InputFingerprint != source.Fingerprint || saved.EvidenceJson != evidence ||
                        saved.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(evidence)) throw new InvalidDataException("Submission proof conflict.");
                    checkpoint = saved.Id;
                }
            }
        }
        return new(source, targets, confirmed.ToArray(), checkpoint);
    }
    private static GenerationObservationAttemptRow NewObservationAttempt(DeploymentGenerationRow source, string subject, string operation,
        string origin, int ordinal, Guid? previous, DateTimeOffset start, int seconds, string actor) => new()
        {
            Id = Guid.CreateVersion7(),
            DeploymentId = source.DeploymentId,
            Generation = source.Generation,
            Subject = subject,
            OperationId = operation,
            OriginJson = origin,
            Ordinal = ordinal,
            Previous = previous,
            StartedAt = start,
            BudgetSeconds = seconds,
            Deadline = start.AddSeconds(seconds),
            Actor = actor
        };
    private static void ValidateAttempt(GenerationObservationAttemptRow attempt, DeploymentGenerationRow source, string operation, string origin, int seconds)
    {
        if (attempt.Id == Guid.Empty || attempt.DeploymentId != source.DeploymentId || attempt.Generation != source.Generation || attempt.OperationId != operation ||
            attempt.OriginJson != origin || attempt.Ordinal < 1 || attempt.BudgetSeconds != seconds || attempt.Deadline != attempt.StartedAt.AddSeconds(seconds))
            throw new InvalidDataException("Immutable observation authority mismatch.");
    }
    private async Task<Guid> CheckpointAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, string stage, string evidence, CancellationToken token)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(evidence) > 2097152) throw new InvalidDataException("Checkpoint evidence bound exceeded.");
        var saved = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == stage, token);
        if (saved is not null)
        {
            if (saved.FormatVersion != 1 || saved.InputFingerprint != source.Fingerprint || saved.EvidenceJson != evidence ||
                saved.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(evidence)) throw new InvalidDataException("Checkpoint origin conflict.");
            return saved.Id;
        }
        var row = new GenerationStageCheckpointRow
        {
            Id = Guid.CreateVersion7(),
            DeploymentId = source.DeploymentId,
            Generation = source.Generation,
            Stage = stage,
            FormatVersion = 1,
            InputFingerprint = source.Fingerprint,
            EvidenceJson = evidence,
            EvidenceFingerprint = ResourcesReadyCheckpointCodec.Hash(evidence),
            ConfirmedAt = Now()
        };
        db.GenerationStageCheckpoints.Add(row); return row.Id;
    }
    private static async Task<GenerationStageCheckpointRow?> SavedReadyAsync(ManifestCatalogDbContext db, SubmittedSet set, CancellationToken token)
    {
        var saved = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == set.Source.DeploymentId &&
            x.Generation == set.Source.Generation && x.Stage == "RolloutReady", token);
        if (saved is null) return null;
        if (saved.FormatVersion != 1 || saved.InputFingerprint != set.Source.Fingerprint ||
            saved.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(saved.EvidenceJson)) throw new InvalidDataException("Readiness checkpoint corrupt.");
        var proof = JsonSerializer.Deserialize(saved.EvidenceJson, RolloutMetadata.Default.ReadyProof)
            ?? throw new InvalidDataException("Readiness checkpoint unavailable.");
        if (proof.Format != 1 || proof.Stage != "RolloutReady" || proof.Deployment != set.Source.DeploymentId || proof.Generation != set.Source.Generation ||
            proof.InputFingerprint != set.Source.Fingerprint || proof.Submitted != set.Checkpoint || proof.Members.Length != set.Targets.Length ||
            !proof.Members.Select(x => x.Origin.Subject).SequenceEqual(set.Targets)) throw new InvalidDataException("Readiness checkpoint origin mismatch.");
        string? request = null; int? round = null;
        foreach (var member in proof.Members)
        {
            var original = set.Members.Single(x => x.Subject == member.Origin.Subject);
            var sample = await db.ObservationRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == member.Observation, token);
            var authority = await db.ObservationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == member.Attempt, token);
            if (sample is null || sample.Kind != "Read" || sample.AttemptId != member.Attempt || sample.DeploymentId != set.Source.DeploymentId ||
                sample.Generation != set.Source.Generation || sample.Subject != member.Origin.Subject || sample.Payload is null || authority is null ||
                authority.OriginJson != RolloutJson.Encode(original.Origin) || member.Origin != original.Origin ||
                request is not null && sample.Request != request || round is not null && sample.Ordinal != round)
                throw new InvalidDataException("Readiness checkpoint evidence mismatch.");
            var observed = RolloutJson.Observation(sample.Payload);
            if (observed.Outcome != RolloutOutcome.Ready || !((IInternalReadinessHandler)original.Loaded.Handler).ValidateObservation(
                original.Loaded.Intent.Payload, original.Receipt, observed)) throw new InvalidDataException("Readiness checkpoint evidence invalid.");
            request = sample.Request; round = sample.Ordinal;
        }
        return saved;
    }
}
