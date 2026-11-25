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
    internal sealed record Original(AcceptedOperation Accepted, ResourceObservationReceipt Receipt);
    internal sealed record OperationState(string Id, string State, Guid? Attempt, long Number, DateTimeOffset? Dispatched,
        DateTimeOffset? Finalized, string? Reason, string? Outcome, DateTimeOffset? AttemptFinished);
    internal sealed record Capture(string Fingerprint, OperationState? Operation, IReadOnlyList<Original> Originals);

    internal sealed class Permit(PostgreSqlReuseVerificationScopes owner, Guid deployment, long generation, OperationId? operation,
        Capture capture, DurableActionAdmission admission, IExternalOperationOutputContract[] contracts,
        IProviderConfigurationCodec[] codecs, CancellationToken token, bool internalResourceStage) : IDisposable
    {
        internal PostgreSqlReuseVerificationScopes Owner => owner;
        internal Guid Deployment => deployment;
        internal long Generation => generation;
        internal OperationId? Operation => operation;
        internal string Fingerprint => capture.Fingerprint;
        internal OperationState? State = capture.Operation;
        internal DurableActionAdmission Admission => admission;
        internal IExternalOperationOutputContract[] Contracts => contracts;
        internal IProviderConfigurationCodec[] Codecs => codecs;
        internal CancellationToken Token => token;
        internal bool InternalResourceStage => internalResourceStage;
        internal bool Begun = capture.Operation?.State is nameof(DurableOperationState.Dispatching) or nameof(DurableOperationState.NeedsAttention);
        private int disposed;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
        }
        public override string ToString() => "[Resource reuse verification permit]";
    }

    internal Permit Create(Guid deployment, long generation, OperationId? operation, Capture capture, DurableActionAdmission admission,
        IExternalOperationOutputContract[] contracts, IProviderConfigurationCodec[] codecs, CancellationToken token, bool internalResourceStage)
        => new(this, deployment, generation, operation, capture, admission, contracts, codecs, token, internalResourceStage);

    internal static async ValueTask<ResourceReuseValidationResult> VerifyAsync(IDbContextFactory<ManifestCatalogDbContext> contexts,
        IEnumerable<IExternalOperationOutputContract> contracts, IEnumerable<IProviderConfigurationCodec> codecs,
        IEnumerable<IDurableResourceReuseVerifier> verifiers, Func<DurableActionAdmission>? admission,
        ApplicationDeploymentId deployment, DeploymentGeneration generation,
        CancellationToken token, OperationId? operation = null, Guid? attempt = null)
    {
        await using (var db = await contexts.CreateDbContextAsync(token))
            if (!await RequiresAsync(db, Guid.Parse(deployment.ToString()), generation.Value, token))
                return new(ResourceReuseValidationStatus.Confirmed);
        if (admission is null) return new(ResourceReuseValidationStatus.InputUnavailable);
        DurableActionAdmission exact;
        try { exact = admission(); }
        catch (InvalidOperationException) { return new(ResourceReuseValidationStatus.InputUnavailable); }
        if (exact is null) return new(ResourceReuseValidationStatus.InputUnavailable);
        var result = await new PostgreSqlDurableOperationJournal(contexts, contracts, codecs, verifiers)
            .ValidateReuseAsync(deployment, generation, exact, token, operation);
        if (attempt is { } expected && result.Scope is Permit permit)
        {
            if (permit.State?.Attempt != expected) { result.Dispose(); return new(ResourceReuseValidationStatus.InputUnavailable); }
            permit.Begun = true;
        }
        return result;
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
        OperationId? operation, Transition transition, CancellationToken token, Guid? attempt = null, ResourceReuseValidationResult? verification = null)
    {
        var permit = verification?.Status == ResourceReuseValidationStatus.Confirmed ? verification.Scope as Permit : null;
        if (permit is null || !ReferenceEquals(permit.Owner, db.ReuseVerificationScopes) || permit.Deployment != deployment || permit.Generation != generation)
            return !await RequiresAsync(db, deployment, generation, token);
        if (permit.IsDisposed || permit.Token.IsCancellationRequested ||
            operation is not null && permit.Operation != operation || transition != Transition.Aggregate && permit.Operation is null ||
            transition == Transition.Begin && permit.Begun || transition is Transition.Marker or Transition.Completion && !permit.Begun ||
            attempt is not null && permit.State?.Attempt != attempt) return false;
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Reuse verification needs the owning transition transaction.");
        await DurableAggregateCompletion.LockAsync(db, deployment, token);
        await ResourceContinuity.LockAsync(db, deployment, token);
        var captured = await ReadAsync(db, deployment, generation, permit.Operation, permit.Admission, permit.Contracts, permit.Codecs, token, internalResourceStage: permit.InternalResourceStage);
        return captured is not null && captured.Fingerprint == permit.Fingerprint && captured.Operation == permit.State &&
            !permit.Token.IsCancellationRequested && !permit.IsDisposed;
    }

    internal static async Task UpdatedAsync(ManifestCatalogDbContext db, DurableOperationKey key, CancellationToken token, bool begun = false, ResourceReuseValidationResult? verification = null)
    {
        var permit = verification?.Status == ResourceReuseValidationStatus.Confirmed ? verification.Scope as Permit : null;
        if (permit is null || !ReferenceEquals(permit.Owner, db.ReuseVerificationScopes) || permit.Deployment != Guid.Parse(key.DeploymentId.ToString()) ||
            permit.Generation != key.Generation.Value || permit.Operation != key.OperationId || permit.IsDisposed) return;
        permit.State = await OwnStateAsync(db, Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value, key.OperationId, token);
        if (begun) permit.Begun = true;
    }

    internal static async Task<Capture?> ReadAsync(ManifestCatalogDbContext db, Guid deployment, long generation, OperationId? operation,
        DurableActionAdmission admission, IExternalOperationOutputContract[] contracts, IProviderConfigurationCodec[] codecs, CancellationToken token,
        bool validateMaterial = false, bool internalResourceStage = false)
    {
        try
        {
            var row = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, token);
            if (row is null || owner is null || owner.DesiredGeneration != generation || row.InternalEffects != internalResourceStage) return null;
            var inputs = row.Inputs(); var graph = DurableOperationGraphCodec.Decode(row.GraphJson);
            if (DurableOperationGraphCodec.Fingerprint(inputs, graph) != row.Fingerprint ||
                operation is not null && graph.Nodes.All(x => x.Id != operation) &&
                    !(internalResourceStage && await db.ActionIntents.AsNoTracking().AnyAsync(x => x.DeploymentId == deployment && x.Generation == generation && x.OperationId == operation.Value, token))) return null;
            var metadata = await DurableAggregateCompletion.InspectMetadataAsync(db, deployment, generation, contracts, codecs, token, admission, internalResourceStage: internalResourceStage, completedRead: internalResourceStage);
            if (metadata is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, token) ||
                !await ResourceContinuity.ValidateAsync(db, deployment, graph, token) ||
                !await ResourceContinuity.ValidateOutputsAsync(db, deployment, graph, contracts, codecs, admission, internalResourceStage ? InternalResourceStage.Project(inputs) : inputs, token, validateMaterial: validateMaterial)) return null;
            var fields = new List<string> { deployment.ToString(), generation.ToString(CultureInfo.InvariantCulture), row.Fingerprint,
                row.GraphJson, owner.DesiredGeneration.ToString(CultureInfo.InvariantCulture), owner.AppliedGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-" };
            var originals = new List<Original>();
            foreach (var reuse in graph.ReusedResources.OrderBy(x => x.Resource.Value, StringComparer.Ordinal))
            {
                var managed = await db.CurrentResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reuse.ManagementId, token);
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
                var receipt = await ResourceObservationReceiptIntegrity.ReadAsync(db, key, token);
                var receiptRow = receipt is null ? null : await db.ResourceObservationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receipt.Id, token);
                if (receipt is null || receiptRow is null) return null;
                fields.AddRange([receipt.Id.ToString(), receiptRow.Fingerprint]);
                originals.Add(new(new(originalInputs, originalGraph, node, originalInputs.Configuration!.Manifest.Spec), receipt));
            }
            if (internalResourceStage)
            {
                foreach (var item in await db.ActionIntents.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == generation).OrderBy(x => x.OperationId).ToArrayAsync(token))
                    fields.AddRange([item.OperationId, item.RequestId, item.Marker.ToString(), item.ManagedIdentity.ToString(), item.SubjectKind, item.Component, item.LocalId,
                        item.SubjectContract, item.ProviderKind, item.ConfigurationContract, item.Implementation, item.ActionContract, item.InputFormat, item.OutputContract,
                        item.ProviderId.ToString(), item.ConfigurationRevision.ToString(CultureInfo.InvariantCulture), ResourcesReadyCheckpointCodec.Hash(item.Payload), item.CheckpointId.ToString(), item.InputFingerprint]);
                foreach (var exact in inputs.Providers.Entries.Values.DistinctBy(x => (x.Instance, x.Configuration)).OrderBy(x => x.Instance.ToString(), StringComparer.Ordinal).ThenBy(x => x.Configuration.Value))
                {
                    var instance = Guid.Parse(exact.Instance.ToString());
                    var configuration = await db.ProviderConfigurationRevisions.AsNoTracking().SingleAsync(x => x.EnvironmentRecordId == row.EnvironmentRecordId &&
                        x.ProviderInstanceId == instance && x.RevisionNumber == exact.Configuration.Value, token);
                    fields.AddRange([instance.ToString(), configuration.ConfigurationShapeId, configuration.PayloadFormatVersion.ToString(CultureInfo.InvariantCulture),
                        configuration.ImplementationId, configuration.IsSealed.ToString(), configuration.PayloadJson]);
                }
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
