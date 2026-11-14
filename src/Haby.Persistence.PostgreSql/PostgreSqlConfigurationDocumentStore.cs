using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>One immutable artifact per exact target. No delivery, current Provider or secret access.</summary>
public sealed class PostgreSqlConfigurationDocumentStore(IDbContextFactory<ManifestCatalogDbContext> contexts,
    ComposeConfigurationDocument composer, TimeProvider clock) : IConfigurationDocumentStore, IConfigurationApplicationInventory
{
    public async ValueTask<ConfigurationApplicationInventory> SelectApplicationAsync(ConfigurationApplicationSelection selection, CancellationToken token)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var view = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var owner = await (from deployment in db.ApplicationDeployments.AsNoTracking()
                               join environment in db.Environments on deployment.EnvironmentRecordId equals environment.Id
                               join application in db.Applications on deployment.ApplicationRecordId equals application.Id
                               where environment.EnvironmentId == selection.Environment.Value && application.ApplicationId == selection.Application.Value
                               select deployment).SingleOrDefaultAsync(token);
            if (owner is null) return new(ConfigurationDocumentStatus.NotFound);
            var target = new ConfigurationApplicationTarget(ApplicationDeploymentId.Parse(owner.Id.ToString()),
                DeploymentGeneration.Create(selection.Generation?.Value ?? owner.DesiredGeneration));
            if (selection.Generation is null && owner.DesiredGeneration != owner.AppliedGeneration)
                return new(ConfigurationDocumentStatus.NotApplied, target);
            // Source and eligibility use the same exact-generation implementation as single documents.
            var source = await Source(db, target, token);
            if (source is null) return new(ConfigurationDocumentStatus.NotFound, target);
            if (source.InternalEffects) return new(ConfigurationDocumentStatus.NotApplied, target);
            if (source.EnvironmentRecordId != owner.EnvironmentRecordId || source.ApplicationRecordId != owner.ApplicationRecordId || source.EnvironmentKey != selection.Environment.Value)
                throw new InvalidDataException("Stored deployment owner is inconsistent.");
            var frozen = Layout(source);
            if (!await Eligible(db, source, token)) return new(ConfigurationDocumentStatus.NotApplied, target);
            var count = frozen.Layouts.Sum(x => (long)x.Documents.Count);
            if (count > ConfigurationApplicationLimits.MaxItems || !ConfigurationApplicationLimits.Fits((int)count))
                return new(ConfigurationDocumentStatus.LimitExceeded, target);
            if (frozen.Layouts.SelectMany(x => x.Documents).Any(x => !ConfigurationApplicationLimits.SafeText(x.Definition.FileName, 255)))
                return new(ConfigurationDocumentStatus.LimitExceeded, target);
            var pairs = frozen.Layouts.SelectMany(x => x.Documents.Select(d => new ConfigurationDocumentTarget(target.Deployment, target.Generation, x.Component, d.Definition.Id)))
                .OrderBy(x => x.Component.Value, StringComparer.Ordinal).ThenBy(x => x.Document.Value, StringComparer.Ordinal).ToArray();
            return new(ConfigurationDocumentStatus.Found, target, pairs);
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or DbUpdateException) { throw new IOException("Configuration storage unavailable."); }
    }

    public async ValueTask<ConfigurationDocumentResult> SelectAsync(ConfigurationDocumentSelection selection, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var view = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var owner = await (from deployment in db.ApplicationDeployments.AsNoTracking()
                           join environment in db.Environments on deployment.EnvironmentRecordId equals environment.Id
                           join application in db.Applications on deployment.ApplicationRecordId equals application.Id
                           where environment.EnvironmentId == selection.Environment.Value && application.ApplicationId == selection.Application.Value
                           select deployment).SingleOrDefaultAsync(token);
        if (owner is null) return new(ConfigurationDocumentStatus.NotFound);
        var generation = selection.Generation?.Value ?? owner.DesiredGeneration;
        var target = new ConfigurationDocumentTarget(ApplicationDeploymentId.Parse(owner.Id.ToString()), DeploymentGeneration.Create(generation), selection.Component, selection.Document);
        if (selection.Generation is null && owner.DesiredGeneration != owner.AppliedGeneration)
            return new(ConfigurationDocumentStatus.NotApplied, Target: target);
        var source = await Source(db, target, token);
        if (source is null) return new(ConfigurationDocumentStatus.NotFound, Target: target);
        if (source.InternalEffects) return new(ConfigurationDocumentStatus.NotApplied, Target: target);
        if (source.EnvironmentRecordId != owner.EnvironmentRecordId || source.ApplicationRecordId != owner.ApplicationRecordId || source.EnvironmentKey != selection.Environment.Value)
            throw new InvalidDataException("Stored deployment owner is inconsistent.");
        var frozen = Layout(source);
        if (!HasDocument(frozen, target)) return new(ConfigurationDocumentStatus.NotFound, Target: target);
        if (selection.Generation is null && !await Complete(db, source, token))
            return new(ConfigurationDocumentStatus.NotApplied, Target: target);
        return new(ConfigurationDocumentStatus.Found, Target: target);
    }

    public async ValueTask<ConfigurationDocumentResult> ReadAsync(ConfigurationDocumentTarget target, CancellationToken token)
    {
        try { return await ReadCoreAsync(target, token); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or DbUpdateException) { throw new IOException("Configuration storage unavailable."); }
    }

    private async ValueTask<ConfigurationDocumentResult> ReadCoreAsync(ConfigurationDocumentTarget target, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await Source(db, target, token);
        if (source?.InternalEffects == true) return new(ConfigurationDocumentStatus.NotApplied, Target: target);
        var row = await Row(db, target, token);
        return row is null ? new(ConfigurationDocumentStatus.NotFound, Target: target) :
            new(ConfigurationDocumentStatus.Found, Decode(row, target), target);
    }

    public ValueTask<ConfigurationDocumentResult> MaterializeAsync(ConfigurationDocumentTarget target, string actor, CancellationToken token) =>
        MaterializeCoreAsync(target, actor, null, token);

    internal ValueTask<ConfigurationDocumentResult> MaterializeInternalAsync(ConfigurationDocumentTarget target, string actor,
        InternalWorkloadConfiguration.Authority authority, CancellationToken token) => MaterializeCoreAsync(target, actor, authority, token);

    private async ValueTask<ConfigurationDocumentResult> MaterializeCoreAsync(ConfigurationDocumentTarget target, string actor,
        InternalWorkloadConfiguration.Authority? authority, CancellationToken token)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        try
        {
            // Fast repeat needs no active renderer or evidence. Frozen identity is still checked.
            await using (var read = await contexts.CreateDbContextAsync(token))
            {
                await using var view = await read.Database.BeginTransactionAsync(token);
                if (authority is not null) await LockDocumentAsync(read, target, token);
                var source = await Source(read, target, token);
                if (source is null) return new(ConfigurationDocumentStatus.NotFound, Target: target);
                if (authority is null && source.InternalEffects) return new(ConfigurationDocumentStatus.NotApplied, Target: target);
                if (authority is not null) _ = await authority.RequireAsync(read, source, target, token);
                var existing = await Row(read, target, token);
                if (existing is not null)
                {
                    var result = Existing(existing, source, target);
                    if (authority is not null && result.Revision is not null)
                    {
                        await authority.AssociateAsync(read, source, result.Revision, token);
                        await read.SaveChangesAsync(token);
                        token.ThrowIfCancellationRequested(); await view.CommitAsync(CancellationToken.None);
                    }
                    return result;
                }
            }
            DeploymentGenerationRow inputs;
            ConfigurationPlanningSnapshot frozen;
            await using (var read = await contexts.CreateDbContextAsync(token))
            {
                await using var view = await read.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
                inputs = await Source(read, target, token) ?? throw new InvalidDataException();
                frozen = Frozen(inputs);
                if (!HasDocument(frozen, target)) return new(ConfigurationDocumentStatus.NotFound, Target: target);
                if (!await EligibilityAsync(read, inputs, target, authority, token)) return new(ConfigurationDocumentStatus.NotApplied, Target: target);
            }
            var composed = await composer.ExecuteAsync(frozen, target, EnvironmentRecordId.Parse(inputs.EnvironmentRecordId.ToString()), token);
            if (composed is null) return new(ConfigurationDocumentStatus.InputUnavailable, Target: target);
            var document = frozen.Layouts.Single(x => x.Component == target.Component).Documents.Single(x => x.Definition.Id == target.Document).Definition;
            // The output boundary has already checked owner/action/contract and the confirmed successful attempt.
            // Capture exact consumed associations and recheck their immutable rows at acceptance.
            var associations = new List<(ConsumedConfigurationOutput Output, Guid Attempt)>();
            await using (var read = await contexts.CreateDbContextAsync(token))
                foreach (var output in composed.Outputs)
                {
                    var key = await ResourceContinuity.ResolveAsync(read, new(target.Deployment, target.Generation, output.Pin.Producer), output.Pin.ResourceLocalId, token);
                    if (key is null) return new(ConfigurationDocumentStatus.InputUnavailable, Target: target);
                    var operation = await read.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == inputs.DeploymentId &&
                        x.Generation == key.Generation.Value && x.OperationId == output.Pin.Producer.Value, token);
                    if (operation.State != nameof(DurableOperationState.Succeeded) || operation.CurrentAttemptId is not { } attempt)
                        return new(ConfigurationDocumentStatus.InputUnavailable, Target: target);
                    associations.Add((output, attempt));
                }
            var associationJson = Associations(associations);
            var metadata = new ConfigurationDocumentMetadata(ConfigurationDocumentRevisionId.New(), target, EnvironmentId.Create(inputs.EnvironmentKey), frozen.Manifest.ApplicationId,
                frozen.Revision, DeploymentPlanId.Parse(inputs.PlanId.ToString()), document.FileName, "json", ConfigurationJson.Profile, "", InputFingerprint(inputs), actor, clock.GetUtcNow().ToUniversalTime());
            var candidate = new ConfigurationDocumentRevision(metadata, inputs.ConfigurationJson, associationJson, composed.Tree);
            var payload = ConfigurationDocumentCodec.Encode(candidate);
            var fingerprint = ConfigurationDocumentCodec.Fingerprint(payload);
            candidate = candidate with { Metadata = metadata with { Fingerprint = fingerprint } };
            _ = ConfigurationJson.Preview(candidate.Tree); // Both complete byte budgets precede any write.

            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            // Serialize same-key acceptance before reading the winner. Different documents use separate locks/transactions.
            await LockDocumentAsync(db, target, token);
            var current = await Source(db, target, token) ?? throw new InvalidDataException();
            if (InputFingerprint(current) != metadata.FrozenInputFingerprint) return new(ConfigurationDocumentStatus.IntegrityConflict, Target: target);
            _ = current.Inputs();
            var winner = await Row(db, target, token);
            if (!await EligibilityAsync(db, current, target, authority, token)) return new(ConfigurationDocumentStatus.NotApplied, Target: target);
            if (winner is not null)
            {
                var saved = Decode(winner, target);
                var equivalent = candidate with { Metadata = saved.Metadata };
                if (ConfigurationDocumentCodec.Encode(equivalent) != winner.Payload) return new(ConfigurationDocumentStatus.IntegrityConflict, Target: target);
                if (authority is not null)
                {
                    await authority.AssociateAsync(db, current, saved, token);
                    await db.SaveChangesAsync(token);
                    token.ThrowIfCancellationRequested(); await transaction.CommitAsync(CancellationToken.None);
                }
                return new(ConfigurationDocumentStatus.Existing, saved, target);
            }
            foreach (var (output, attempt) in associations)
            {
                var key = await ResourceContinuity.ResolveAsync(db, new(target.Deployment, target.Generation, output.Pin.Producer), output.Pin.ResourceLocalId, token);
                if (key is null) return new(ConfigurationDocumentStatus.InputUnavailable, Target: target);
                if (!await db.OperationOutputs.AsNoTracking().AnyAsync(x => x.DeploymentId == inputs.DeploymentId && x.Generation == key.Generation.Value &&
                    x.OperationId == output.Pin.Producer.Value && x.Contract == output.Output.Contract && x.Key == output.Output.Key && x.Payload == output.Output.Payload, token) ||
                    !await db.DurableOperations.AsNoTracking().AnyAsync(x => x.DeploymentId == inputs.DeploymentId && x.Generation == key.Generation.Value &&
                    x.OperationId == output.Pin.Producer.Value && x.State == nameof(DurableOperationState.Succeeded) && x.CurrentAttemptId == attempt, token))
                    return new(ConfigurationDocumentStatus.InputUnavailable, Target: target);
            }
            db.ConfigurationDocuments.Add(new()
            {
                Id = Guid.Parse(metadata.Id.ToString()),
                DeploymentId = inputs.DeploymentId,
                Generation = inputs.Generation,
                Component = target.Component.Value,
                Document = target.Document.Value,
                Payload = payload,
                Fingerprint = fingerprint
            });
            if (authority is not null) await authority.AssociateAsync(db, current, candidate, token);
            await db.SaveChangesAsync(token);
            token.ThrowIfCancellationRequested();
            // Once commit starts, caller cancellation cannot discard a confirmed receipt.
            await transaction.CommitAsync(CancellationToken.None);
            return new(ConfigurationDocumentStatus.Created, candidate, target);
        }
        catch (ConfigurationDocumentLimitException) { return new(ConfigurationDocumentStatus.LimitExceeded, Target: target); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or DbUpdateException) { throw new IOException("Configuration storage unavailable."); }
    }

    private static async Task<bool> EligibilityAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        ConfigurationDocumentTarget target, InternalWorkloadConfiguration.Authority? authority, CancellationToken token)
    {
        if (authority is null) return await Eligible(db, source, token);
        _ = await authority.RequireAsync(db, source, target, token);
        return true;
    }

    private static async Task LockDocumentAsync(ManifestCatalogDbContext db, ConfigurationDocumentTarget target, CancellationToken token)
    {
        // One lock order for initial acceptance, concurrent winners and missing recipient associations.
        var lockKey = $"haby-configuration:{target.Deployment}:{target.Generation.Value}:{target.Component.Value}:{target.Document.Value}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", token);
        var deployment = Guid.Parse(target.Deployment.ToString());
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_deployments WHERE id = {deployment} FOR SHARE", token);
    }

    private static ConfigurationDocumentResult Existing(ConfigurationDocumentRow row, DeploymentGenerationRow source, ConfigurationDocumentTarget target)
    {
        var saved = Decode(row, target);
        if (saved.FrozenInputs != source.ConfigurationJson || saved.Metadata.FrozenInputFingerprint != InputFingerprint(source))
            return new(ConfigurationDocumentStatus.IntegrityConflict, Target: target);
        _ = source.Inputs();
        return new(ConfigurationDocumentStatus.Existing, saved, target);
    }
    private static ConfigurationDocumentRevision Decode(ConfigurationDocumentRow row, ConfigurationDocumentTarget target)
    {
        var saved = ConfigurationDocumentCodec.Decode(row.Payload, row.Fingerprint);
        if (saved.Metadata.Target != target || saved.Metadata.Id.ToString() != row.Id.ToString()) throw new InvalidDataException("Stored configuration identity is inconsistent.");
        return saved;
    }
    private static ConfigurationPlanningSnapshot Frozen(DeploymentGenerationRow source) => source.Inputs().Configuration!;
    // Layout-only selection grants no materialization or delivery authority. Actual
    // materialization validates the entire accepted input set, including fast repeats.
    private static ConfigurationPlanningSnapshot Layout(DeploymentGenerationRow source) => ConfigurationPlanningSnapshotCodec.Decode(source.ConfigurationJson,
        ManifestRevisionRef.Create(ApplicationRecordId.Parse(source.ApplicationRecordId.ToString()), ManifestRevisionNumber.Create(source.RevisionNumber)));
    private static bool HasDocument(ConfigurationPlanningSnapshot frozen, ConfigurationDocumentTarget target) =>
        frozen.Layouts.Any(x => x.Component == target.Component && x.Documents.Any(d => d.Definition.Id == target.Document));
    private static Task<DeploymentGenerationRow?> Source(ManifestCatalogDbContext db, ConfigurationDocumentTarget target, CancellationToken token) =>
        Source(db, new ConfigurationApplicationTarget(target.Deployment, target.Generation), token);
    private static async Task<DeploymentGenerationRow?> Source(ManifestCatalogDbContext db, ConfigurationApplicationTarget target, CancellationToken token)
    {
        var id = Guid.Parse(target.Deployment.ToString());
        var query = db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == id && x.Generation == target.Generation.Value);
        // Read lengths on the server before allocating a protected frozen payload. Its
        // encoded revision cannot fit if even the source character count exceeds the budget.
        if (await query.AnyAsync(x => x.ConfigurationJson.Length > ConfigurationDocumentCodec.MaxBytes || x.GraphJson.Length > ConfigurationDocumentCodec.MaxBytes ||
            x.ProviderInputsJson.Length > ConfigurationDocumentCodec.MaxBytes, token))
            throw new ConfigurationDocumentLimitException();
        return await query.SingleOrDefaultAsync(token);
    }
    private static async Task<ConfigurationDocumentRow?> Row(ManifestCatalogDbContext db, ConfigurationDocumentTarget target, CancellationToken token)
    {
        var id = Guid.Parse(target.Deployment.ToString());
        var query = db.ConfigurationDocuments.AsNoTracking().Where(x => x.DeploymentId == id && x.Generation == target.Generation.Value &&
            x.Component == target.Component.Value && x.Document == target.Document.Value);
        if (await query.AnyAsync(x => x.Payload.Length > ConfigurationDocumentCodec.MaxBytes, token)) throw new ConfigurationDocumentLimitException();
        return await query.SingleOrDefaultAsync(token);
    }
    private static async Task<bool> Eligible(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token) =>
        !source.InternalEffects && await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == source.DeploymentId && x.AppliedGeneration >= source.Generation, token) &&
        await DurableAggregateCompletion.HasProofAsync(db, source.DeploymentId, source.Generation, token) && await Complete(db, source, token);
    private static async Task<bool> Complete(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        if (graph.Baseline is not null && !await ResourceContinuity.ValidateAsync(db, source.DeploymentId, graph, token)) return false;
        var rows = await db.DurableOperations.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation).ToArrayAsync(token);
        return rows.Length == graph.Nodes.Count && graph.Nodes.All(n => rows.Any(x => x.OperationId == n.Id.Value && x.State == nameof(DurableOperationState.Succeeded)));
    }
    private static string InputFingerprint(DeploymentGenerationRow source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new[] { "haby.configuration-input/v1", source.PlanId.ToString(), source.Fingerprint, source.GraphJson, source.ConfigurationJson },
            ManifestCatalogJsonContext.Default.StringArray))));
    private static string Associations(IEnumerable<(ConsumedConfigurationOutput Output, Guid Attempt)> outputs)
    {
        return ConfigurationDocumentCodec.EncodeBounded(w =>
        {
            w.WriteStartArray();
            foreach (var (o, attempt) in outputs)
            {
                w.WriteStartArray(); w.WriteStringValue(o.Pin.Producer.Value); w.WriteStringValue(attempt.ToString());
                w.WriteStringValue(o.Output.Contract); w.WriteStringValue(o.Output.Key); w.WriteStringValue(o.Output.Payload); w.WriteEndArray();
            }
            w.WriteEndArray();
        });
    }
}
