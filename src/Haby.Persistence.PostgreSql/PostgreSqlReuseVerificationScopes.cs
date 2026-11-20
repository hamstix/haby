using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Invocation/attempt-scoped permits, not cached Provider health. A live permit is consumed
/// only with its exact captured catalog state; restart or disposal requires fresh adapter verification.</summary>
public sealed class PostgreSqlReuseVerificationScopes
{
    internal enum Transition { Begin, Marker, Completion, Aggregate }
    internal sealed record Original(AcceptedOperation Accepted, ResourceImportReceipt Receipt);
    internal sealed record OperationState(string Id, string State, Guid? Attempt, long Number, DateTimeOffset? Dispatched,
        DateTimeOffset? Finalized, string? Reason, string? Outcome, DateTimeOffset? AttemptFinished);
    internal sealed record Capture(string Fingerprint, OperationState? Operation, IReadOnlyList<Original> Originals);
    private readonly ConcurrentDictionary<(Guid, long), Permit> permits = new();

    internal sealed class Permit(PostgreSqlReuseVerificationScopes owner, Guid deployment, long generation, OperationId? operation,
        Capture capture, DurableActionAdmission admission, IExternalOperationOutputContract[] contracts,
        IProviderConfigurationCodec[] codecs, CancellationToken token) : IDisposable
    {
        internal Guid Deployment => deployment;
        internal long Generation => generation;
        internal OperationId? Operation => operation;
        internal string Fingerprint => capture.Fingerprint;
        internal OperationState? State = capture.Operation;
        internal DurableActionAdmission Admission => admission;
        internal IExternalOperationOutputContract[] Contracts => contracts;
        internal IProviderConfigurationCodec[] Codecs => codecs;
        internal CancellationToken Token => token;
        internal bool Begun;
        private int disposed;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
            ((ICollection<KeyValuePair<(Guid, long), Permit>>)owner.permits).Remove(new((deployment, generation), this));
        }
        public override string ToString() => "[Resource reuse verification permit]";
    }

    internal Permit Create(Guid deployment, long generation, OperationId? operation, Capture capture, DurableActionAdmission admission,
        IExternalOperationOutputContract[] contracts, IProviderConfigurationCodec[] codecs, CancellationToken token)
    {
        var permit = new Permit(this, deployment, generation, operation, capture, admission, contracts, codecs, token);
        permits.AddOrUpdate((deployment, generation), permit, (_, previous) => { previous.Dispose(); return permit; });
        return permit;
    }

    internal static async Task<bool> RequiresAsync(ManifestCatalogDbContext db, Guid deployment, long generation, CancellationToken token)
    {
        var row = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
        if (row is null) return true;
        var graph = DurableOperationGraphCodec.Decode(row.GraphJson);
        var ids = graph.ReusedResources.Select(x => x.ManagementId).ToArray();
        return await db.ResourceManagement.AsNoTracking().AnyAsync(x => ids.Contains(x.Id) && x.Authority == ResourceLifecycleAuthority.UseAndRetain, token);
    }

    internal static async Task<bool> BeforeAsync(ManifestCatalogDbContext db, Guid deployment, long generation,
        OperationId? operation, Transition transition, CancellationToken token, Guid? attempt = null)
    {
        Permit? permit = null;
        db.ReuseVerificationScopes?.permits.TryGetValue((deployment, generation), out permit);
        if (permit is null) return !await RequiresAsync(db, deployment, generation, token);
        if (permit.IsDisposed || permit.Token.IsCancellationRequested ||
            operation is not null && permit.Operation != operation || transition != Transition.Aggregate && permit.Operation is null ||
            transition == Transition.Begin && permit.Begun || transition is Transition.Marker or Transition.Completion && !permit.Begun ||
            attempt is not null && permit.State?.Attempt != attempt) return false;
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Reuse verification needs the owning transition transaction.");
        await DurableAggregateCompletion.LockAsync(db, deployment, token);
        await ResourceContinuity.LockAsync(db, deployment, token);
        var captured = await ReadAsync(db, deployment, generation, permit.Operation, permit.Admission, permit.Contracts, permit.Codecs, token);
        return captured is not null && captured.Fingerprint == permit.Fingerprint && captured.Operation == permit.State &&
            !permit.Token.IsCancellationRequested && !permit.IsDisposed;
    }

    internal static async Task UpdatedAsync(ManifestCatalogDbContext db, DurableOperationKey key, CancellationToken token, bool begun = false)
    {
        if (db.ReuseVerificationScopes?.permits.TryGetValue((Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value), out var permit) != true ||
            permit is null || permit.Operation != key.OperationId || permit.IsDisposed) return;
        permit.State = await OwnStateAsync(db, Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value, key.OperationId, token);
        if (begun) permit.Begun = true;
    }

    internal static async Task<Capture?> ReadAsync(ManifestCatalogDbContext db, Guid deployment, long generation, OperationId? operation,
        DurableActionAdmission admission, IExternalOperationOutputContract[] contracts, IProviderConfigurationCodec[] codecs, CancellationToken token,
        bool validateMaterial = false)
    {
        try
        {
            var row = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, token);
            if (row is null || owner is null || owner.DesiredGeneration != generation) return null;
            var inputs = row.Inputs(); var graph = DurableOperationGraphCodec.Decode(row.GraphJson);
            if (DurableOperationGraphCodec.Fingerprint(inputs, graph) != row.Fingerprint ||
                operation is not null && graph.Nodes.All(x => x.Id != operation)) return null;
            var metadata = await DurableAggregateCompletion.InspectMetadataAsync(db, deployment, generation, contracts, codecs, token, admission);
            if (metadata is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, token) ||
                !await ResourceContinuity.ValidateAsync(db, deployment, graph, token) ||
                !await ResourceContinuity.ValidateOutputsAsync(db, deployment, graph, contracts, codecs, admission, inputs, token, validateMaterial: validateMaterial)) return null;
            var fields = new List<string> { deployment.ToString(), generation.ToString(CultureInfo.InvariantCulture), row.Fingerprint,
                row.GraphJson, owner.DesiredGeneration.ToString(CultureInfo.InvariantCulture), owner.AppliedGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-" };
            var originals = new List<Original>();
            foreach (var reuse in graph.ReusedResources.OrderBy(x => x.Resource.Value, StringComparer.Ordinal))
            {
                var managed = await db.ResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reuse.ManagementId, token);
                if (managed is null) return null;
                fields.AddRange([managed.Id.ToString(), managed.Environment.ToString(), managed.Application.ToString(), managed.Resource,
                    managed.Deployment.ToString(), managed.SourceGeneration.ToString(CultureInfo.InvariantCulture), managed.Origin.ToString(), managed.Authority.ToString(), reuse.EvidenceFingerprint]);
                var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == reuse.SourceGeneration, token);
                var originalInputs = source.Inputs(); var originalGraph = DurableOperationGraphCodec.Decode(source.GraphJson);
                fields.Add(source.Fingerprint); fields.Add(source.GraphJson);
                var pinned = originalInputs.Providers.Resource(reuse.Resource);
                var instance = Guid.Parse(pinned.Instance.ToString());
                var configuration = await db.ProviderConfigurationRevisions.AsNoTracking().SingleAsync(x => x.ProviderInstanceId == instance &&
                    x.EnvironmentRecordId == source.EnvironmentRecordId && x.RevisionNumber == pinned.Configuration.Value, token);
                fields.AddRange([instance.ToString(), configuration.ConfigurationShapeId, configuration.PayloadFormatVersion.ToString(CultureInfo.InvariantCulture),
                    configuration.ImplementationId, configuration.IsSealed.ToString(), configuration.PayloadJson]);
                if (managed.Authority != ResourceLifecycleAuthority.UseAndRetain) continue;
                var node = originalGraph.Nodes.First(x => x.ResourceId == reuse.Resource);
                var key = new DurableOperationKey(ApplicationDeploymentId.Parse(deployment.ToString()), DeploymentGeneration.Create(source.Generation), node.Id);
                var receipt = await ResourceImportReceiptIntegrity.ReadAsync(db, key, token);
                var receiptRow = await db.ResourceImportReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == deployment && x.Generation == source.Generation, token);
                if (receipt is null || receiptRow is null) return null;
                fields.AddRange([receipt.Id.ToString(), receiptRow.Fingerprint]);
                originals.Add(new(new(originalInputs, originalGraph, node, originalInputs.Configuration!.Manifest.Spec), receipt));
            }
            var operations = await db.DurableOperations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == generation)
                .OrderBy(x => x.OperationId).ToArrayAsync(token);
            foreach (var item in operations.Where(x => x.OperationId != operation?.Value))
            {
                var state = await OwnStateAsync(db, deployment, generation, OperationId.Create(item.OperationId), token);
                if (state is null) return null;
                fields.AddRange([state.Id, state.State, state.Attempt?.ToString() ?? "-", state.Number.ToString(CultureInfo.InvariantCulture),
                    state.Dispatched?.ToString("O", CultureInfo.InvariantCulture) ?? "-", state.Finalized?.ToString("O", CultureInfo.InvariantCulture) ?? "-",
                    state.Reason ?? "-", state.Outcome ?? "-", state.AttemptFinished?.ToString("O", CultureInfo.InvariantCulture) ?? "-"]);
            }
            return new(ResourcesReadyCheckpointCodec.Hash(JsonSerializer.Serialize(fields.ToArray(), ManifestCatalogJsonContext.Default.StringArray)),
                operation is null ? null : await OwnStateAsync(db, deployment, generation, operation, token), originals);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException) { return null; }
    }

    private static async Task<OperationState?> OwnStateAsync(ManifestCatalogDbContext db, Guid deployment, long generation, OperationId operation, CancellationToken token)
    {
        var row = await db.DurableOperations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation && x.OperationId == operation.Value, token);
        if (row is null) return null;
        var attempt = row.CurrentAttemptId is { } id ? await db.DurableOperationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == id &&
            x.DeploymentId == deployment && x.Generation == generation && x.OperationId == operation.Value && x.AttemptNumber == row.AttemptNumber, token) : null;
        return new(row.OperationId, row.State, row.CurrentAttemptId, row.AttemptNumber, row.DispatchedAt, row.FinalizedAt, row.ReasonCode, attempt?.Outcome, attempt?.FinishedAt);
    }
}
