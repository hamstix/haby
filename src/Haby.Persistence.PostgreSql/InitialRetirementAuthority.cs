using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static class InitialRetirementAuthority
{
    internal static async Task<InitialRetirementInventory> CaptureAsync(ManifestCatalogDbContext db,
        DurablePlanningInputs inputs, DurableOperationGraph graph, ApplicationDeploymentRow? owner,
        IReadOnlyDictionary<ProviderInput.Workload, IReadOnlyList<ManifestLocalId>> selected, CancellationToken token)
    {
        var environment = Guid.Parse(inputs.Environment.ToString());
        var application = Guid.Parse(inputs.Revision.ApplicationRecordId.ToString());
        // These are contradictions to the accepted initial origin, not evidence inferred from empty tables.
        if (await db.ManagedWorkloads.AnyAsync(x => x.EnvironmentId == environment && x.ApplicationId == application, token) ||
            await db.ActionIntents.AnyAsync(x => x.SubjectKind == "Workload" && db.DeploymentGenerations.Any(g =>
                g.DeploymentId == x.DeploymentId && g.Generation == x.Generation && g.EnvironmentRecordId == environment && g.ApplicationRecordId == application), token))
            throw new InvalidDataException("Prior Workload ownership is not an initial retirement shape.");
        var history = owner is null ? [] : await HistoryAsync(db, owner.Id, owner.DesiredGeneration, token);
        if (owner is null && graph.Baseline is not null || owner is not null &&
            (owner.AppliedGeneration != owner.DesiredGeneration || graph.Baseline?.Generation != owner.DesiredGeneration))
            throw new InvalidDataException("Retirement baseline mismatch.");
        var targets = Targets(inputs, selected);
        return new(new(1, owner is null ? "Initial" : "ResourceBaseline", owner?.AppliedGeneration, history, targets, [], []));
    }

    internal static InitialRetirementTarget[] Targets(DurablePlanningInputs inputs,
        IReadOnlyDictionary<ProviderInput.Workload, IReadOnlyList<ManifestLocalId>> selected) => selected.Select(pair =>
        {
            var exact = inputs.Providers.Entries[pair.Key];
            return new InitialRetirementTarget("Workload/" + pair.Key.Component.Value + "/" + pair.Key.Id.Value,
                inputs.Configuration!.Manifest.Spec.Components[pair.Key.Component].Workloads[pair.Key.Id].Type.Value,
                Guid.Parse(exact.Instance.ToString()), exact.Configuration.Value,
                ResourcesReadyCheckpointCodec.Hash(InternalPlacementInput.Decode(exact.Placement ?? throw new InvalidDataException("Exact Placement required.")).Declaration.Payload),
                pair.Value.Select(x => x.Value).Order(StringComparer.Ordinal).ToArray(), pair.Value.Count > 0);
        }).OrderBy(x => x.Subject, StringComparer.Ordinal).ToArray();

    internal static async Task ValidateAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        CancellationToken token)
    {
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        var inventory = graph.Retirement?.Document ?? throw new InvalidDataException("Frozen retirement authority required.");
        if (inventory.ObsoleteObjects.Length != 0 || inventory.ObsoletePublications.Length != 0 ||
            inventory.PriorApplied != graph.Baseline?.Generation ||
            inventory.Shape == "Initial" && (source.Generation != 1 || !(InternalResourceStage.IsEmpty(source.Inputs(), graph) || InternalResourceStage.IsInitialCreate(source.Inputs(), graph))) ||
            inventory.Shape == "ResourceBaseline" && source.Generation != inventory.PriorApplied + 1)
            throw new InvalidDataException("Unsupported or nonempty retirement inventory.");
        var expected = inventory.PriorApplied is { } previous ? await HistoryAsync(db, source.DeploymentId, previous, token) : [];
        if (!expected.SequenceEqual(inventory.History)) throw new InvalidDataException("Retirement history provenance mismatch.");
        var inputs = source.Inputs();
        var declared = inputs.Providers.Entries.Keys.OfType<ProviderInput.Workload>()
            .Select(x => "Workload/" + x.Component.Value + "/" + x.Id.Value).Order(StringComparer.Ordinal);
        if (!declared.SequenceEqual(inventory.Targets.Select(x => x.Subject))) throw new InvalidDataException("Retirement target coverage mismatch.");
    }

    internal static async Task<InitialRetirementHistory[]> HistoryAsync(ManifestCatalogDbContext db, Guid deployment,
        long through, CancellationToken token)
    {
        if (through is < 1 or > InitialRetirementInventory.MaximumHistory) throw new InvalidDataException("Retirement history exceeds bound.");
        var query = db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation <= through);
        if (await query.AnyAsync(x => x.GraphJson.Length > 2097152 || x.ConfigurationJson.Length > 2097152 || x.ProviderInputsJson.Length > 2097152, token))
            throw new InvalidDataException("Retirement history payload exceeds bound.");
        if (await query.SumAsync(x => (long)x.GraphJson.Length + x.ConfigurationJson.Length + x.ProviderInputsJson.Length, token) > 16777216)
            throw new InvalidDataException("Combined retirement history payload exceeds bound.");
        var rows = await query.OrderBy(x => x.Generation).Take(InitialRetirementInventory.MaximumHistory + 1).ToArrayAsync(token);
        if (rows.Length != through || rows.Where((x, i) => x.Generation != i + 1).Any()) throw new InvalidDataException("Complete prior history required.");
        var result = new List<InitialRetirementHistory>();
        foreach (var row in rows)
        {
            if (row.InternalEffects || row.Inputs().Configuration!.Manifest.Spec.Components.Values.Any(x => x.Workloads.Count > 0))
                throw new InvalidDataException("Prior Workload declaration cannot be retired by initial completion.");
            var plan = await db.DurablePlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.PlanId, token);
            var commit = await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.ChangeSetId == row.ChangeSetId, token);
            if (plan is null || commit is null || plan.Fingerprint != row.Fingerprint || plan.GraphJson != row.GraphJson ||
                plan.ProviderInputsJson != row.ProviderInputsJson || plan.ConfigurationJson != row.ConfigurationJson || plan.InternalEffects ||
                plan.ChangeSetId != row.ChangeSetId || plan.EnvironmentRecordId != row.EnvironmentRecordId || plan.ApplicationRecordId != row.ApplicationRecordId ||
                plan.RevisionNumber != row.RevisionNumber || commit.PlanId != row.PlanId || commit.DeploymentId != row.DeploymentId ||
                commit.Generation != row.Generation || commit.ActorId != row.ActorId || commit.CommittedAt != row.AcceptedAt)
                throw new InvalidDataException("Accepted Resource-only history mismatch.");
            var proof = await DurableAggregateCompletion.ReadAsync(db, row, token) ?? throw new InvalidDataException("Applied baseline proof required.");
            result.Add(new(row.Generation, row.PlanId, row.ChangeSetId, row.RevisionNumber, row.Fingerprint, proof.Id,
                ResourcesReadyCheckpointCodec.Hash(ResourcesReadyCheckpointCodec.Encode(proof))));
        }
        return result.ToArray();
    }

    internal static bool Same(InitialRetirementInventory left, InitialRetirementInventory right) => left.Fingerprint == right.Fingerprint;
}
