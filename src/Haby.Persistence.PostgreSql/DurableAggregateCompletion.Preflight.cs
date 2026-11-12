using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static partial class DurableAggregateCompletion
{
    internal sealed record PendingResult(DurableOperationKey Key, ExternalOperationOutput Output);
    internal sealed record PreparedCredential(long Generation, string Operation, string Fingerprint,
        GeneratedCredentialInput Input, GeneratedCredentialOutput Output);
    internal sealed record ReadinessSource(DeploymentGenerationRow Row, DurablePlanningInputs Inputs, DurableOperationGraph Graph);
    internal sealed record ReadinessMetadata(Guid Deployment, long Generation, string Fingerprint,
        IReadOnlyList<ReadinessSource> Sources, IReadOnlyDictionary<string, Guid> Management, IReadOnlyList<PreparedCredential> Credentials);
    internal sealed record ReadinessPreparation(Guid Deployment, long Generation, string Fingerprint,
        IReadOnlyDictionary<long, string> Sources, IReadOnlyDictionary<string, Guid> Management, IReadOnlyList<PreparedCredential> Credentials);

    // This first pass is deliberately complete before touching any credential table or
    // material. Only the bounded accepted graph and its direct original sources enter it.
    internal static async Task<ReadinessMetadata?> InspectMetadataAsync(ManifestCatalogDbContext db, Guid deployment,
        long generation, IEnumerable<IExternalOperationOutputContract> registrations,
        IEnumerable<IProviderConfigurationCodec> codecs, CancellationToken token,
        DurableActionAdmission? admission = null, PendingResult? pending = null)
    {
        try
        {
            var root = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, token);
            if (root is null || owner is null) return null;
            var target = DurableOperationGraphCodec.Decode(root.GraphJson);
            var resourceIds = target.Nodes.Select(x => x.ResourceId.Value).Concat(target.ReusedResources.Select(x => x.Resource.Value))
                .Concat(target.RetainedResources.Select(x => x.Resource.Value)).Distinct(StringComparer.Ordinal).ToArray();
            var managed = await db.ResourceManagement.AsNoTracking().Where(x => x.Deployment == deployment && resourceIds.Contains(x.Resource)).ToArrayAsync(token);
            var identities = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var resource in resourceIds)
            {
                var reuse = target.ReusedResources.Concat(target.RetainedResources).SingleOrDefault(x => x.Resource.Value == resource);
                var identity = managed.SingleOrDefault(x => x.Resource == resource);
                if (identity is null || identity.Environment != owner.EnvironmentRecordId || identity.Application != owner.ApplicationRecordId ||
                    identity.SourceGeneration != (reuse?.SourceGeneration ?? generation) || reuse is not null && identity.Id != reuse.ManagementId) return null;
                identities.Add(resource, identity.Id);
            }
            if (target.Baseline is not null && !await ResourceContinuity.ValidateAsync(db, deployment, target, token)) return null;
            var generations = target.ReusedResources.Concat(target.RetainedResources).Select(x => x.SourceGeneration).Append(generation).Distinct().ToArray();
            if (generations.Any(x => x > generation)) return null;
            var sources = new List<ReadinessSource>();
            var contracts = registrations.ToArray();
            var typedCodecs = codecs.ToArray();
            foreach (var number in generations)
            {
                var row = number == generation ? root : await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == number, token);
                if (row is null || row.ApplicationRecordId != owner.ApplicationRecordId || row.EnvironmentRecordId != owner.EnvironmentRecordId || row.Presence != nameof(DesiredPresence.Present)) return null;
                var inputs = row.Inputs();
                var graph = DurableOperationGraphCodec.Decode(row.GraphJson);
                graph.ValidateReferences(inputs);
                if (inputs.Configuration is not { } configuration || configuration.Manifest.Spec.Components.Values.Any(x => x.Workloads.Count != 0) ||
                    !ResourceReadinessCoverage.IsComplete(configuration.Manifest.Spec.Resources.Keys.Select(x => x.Value),
                        graph.Nodes.Select(x => x.ResourceId.Value), graph.ReusedResources.Select(x => x.Resource.Value)) ||
                    graph.Nodes.Count + graph.ReusedResources.Count == 0) return null;
                if (DurableOperationGraphCodec.Fingerprint(inputs, graph) != row.Fingerprint || admission is not null && !admission.Supports(graph)) return null;
                var revision = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x => x.ApplicationRecordId == row.ApplicationRecordId && x.LocalRevisionNumber == row.RevisionNumber, token);
                if (revision is null) return null;
                var spec = NormalizedSnapshotCodec.Decode(revision.Snapshot, revision.InputApiVersion, revision.NormalizedModelVersion, revision.ApplicationId, revision.SnapshotFormatVersion);
                foreach (var node in graph.Nodes)
                {
                    if (!spec.Spec.Resources.TryGetValue(node.ResourceId, out var resource) || resource.Type != node.Action.Admission.ResourceContract || resource.ProviderRef != inputs.Providers.Resource(node.ResourceId).Alias) return null;
                    var matches = contracts.Where(x => x.Producer == node.Action).ToArray();
                    if (GeneratedCredentialContracts.IsProducer(node))
                    {
                        if (matches.Length != 0 || GeneratedCredentialInputCodec.Decode(node.Input) is not { } input ||
                            input.Owner.EnvironmentRecordId != inputs.Environment || input.Owner.ApplicationRecordId != inputs.Revision.ApplicationRecordId || input.Owner.ResourceLocalId != node.ResourceId) return null;
                    }
                    else if (node.RequiresExternalOutput
                        ? matches is not [var validator] || node.RequiredOutput != validator.Identity
                        : matches.Length != 0 || node.RequiredOutput is not null) return null;
                }
                if (admission is not null && !await ValidateSourceGraphAsync(db, row, inputs, graph, typedCodecs, admission, token)) return null;
                sources.Add(new(row, inputs, graph));
            }
            // All registration identities and accepted source contexts above precede
            // even generated-slot metadata reads. Typed result validation is metadata-only.
            var credentials = new List<PreparedCredential>();
            foreach (var source in sources)
            {
                var operations = await db.DurableOperations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == source.Row.Generation)
                    .OrderBy(x => x.OperationId).Take(source.Graph.Nodes.Count + 1).ToArrayAsync(token);
                var attemptIds = operations.Where(x => x.CurrentAttemptId is not null).Select(x => x.CurrentAttemptId!.Value).ToArray();
                var attempts = await db.DurableOperationAttempts.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == source.Row.Generation && attemptIds.Contains(x.AttemptId)).ToArrayAsync(token);
                var outputs = await db.OperationOutputs.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == source.Row.Generation)
                    .OrderBy(x => x.OperationId).ThenBy(x => x.Contract).ThenBy(x => x.Key).Take(source.Graph.Nodes.Count + 1).ToArrayAsync(token);
                if (operations.Length != source.Graph.Nodes.Count || outputs.Any(x => source.Graph.Nodes.All(node => node.Id.Value != x.OperationId))) return null;
                foreach (var node in source.Graph.Nodes)
                {
                    var operation = operations.SingleOrDefault(x => x.OperationId == node.Id.Value);
                    if (operation is null) return null;
                    var candidate = pending is not null && pending.Key.DeploymentId.ToString() == deployment.ToString() && pending.Key.Generation.Value == source.Row.Generation && pending.Key.OperationId == node.Id ? pending.Output : null;
                    var rows = outputs.Where(x => x.OperationId == node.Id.Value).ToArray();
                    if (candidate is not null)
                    {
                        if (rows.Length != 0 && (rows.Length != 1 || rows[0].Contract != candidate.Contract || rows[0].Key != candidate.Key || rows[0].Payload != candidate.Payload)) return null;
                        rows = [new OperationOutputRow { DeploymentId = deployment, Generation = source.Row.Generation, OperationId = node.Id.Value, Contract = candidate.Contract, Key = candidate.Key, Payload = candidate.Payload }];
                    }
                    if (operation.State != nameof(DurableOperationState.Succeeded) && candidate is null)
                    {
                        if (source.Row.Generation != generation || rows.Length != 0) return null;
                        continue;
                    }
                    if (operation.State == nameof(DurableOperationState.Succeeded))
                    {
                        var attempt = attempts.SingleOrDefault(x => x.AttemptId == operation.CurrentAttemptId && x.OperationId == node.Id.Value && x.AttemptNumber == operation.AttemptNumber);
                        if (attempt?.Outcome != nameof(DurableAttemptOutcome.Succeeded) || attempt.FinishedAt is null || operation.FinalizedAt != attempt.FinishedAt ||
                            operation.DispatchedAt != attempt.DispatchedAt || operation.ReasonCode is not null ||
                            (GeneratedCredentialContracts.IsProducer(node) ? attempt.DispatchedAt is not null : attempt.DispatchedAt is null)) return null;
                    }
                    if (GeneratedCredentialContracts.IsProducer(node))
                    {
                        if (rows is not [var output] || output.Contract != GeneratedCredentialContracts.OutputContract || output.Key != GeneratedCredentialContracts.Password ||
                            GeneratedCredentialOutputCodec.Decode(output.Contract, output.Payload) is not { } reference) return null;
                        credentials.Add(new(source.Row.Generation, node.Id.Value, ResourcesReadyCheckpointCodec.Hash(output.Payload), GeneratedCredentialInputCodec.Decode(node.Input)!, reference));
                    }
                    else if (node.RequiresExternalOutput)
                    {
                        var validator = contracts.Single(x => x.Producer == node.Action);
                        if (rows is not [var output] || node.RequiredOutput != new ExternalOperationOutputIdentity(output.Contract, output.Key) ||
                            !validator.Validate(node, new(output.Contract, output.Key, output.Payload)) ||
                            !await ValidateTypedOutputAsync(db, source.Row, source.Inputs, source.Graph, node, output, validator, typedCodecs, token)) return null;
                    }
                    else if (rows.Length != 0) return null;
                }
            }
            return new(deployment, generation, root.Fingerprint, sources.AsReadOnly(), new System.Collections.ObjectModel.ReadOnlyDictionary<string, Guid>(identities), credentials.AsReadOnly());
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or System.Text.Json.JsonException) { return null; }
    }

    private static async Task<bool> ValidateSourceGraphAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        DurablePlanningInputs inputs, DurableOperationGraph graph, IProviderConfigurationCodec[] codecs,
        DurableActionAdmission admission, CancellationToken token)
    {
        var pin = inputs.Providers.RequireUniformResourceProvider();
        var id = Guid.Parse(pin.Instance.ToString());
        var instance = await db.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.EnvironmentRecordId == source.EnvironmentRecordId, token);
        var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == id &&
            x.EnvironmentRecordId == source.EnvironmentRecordId && x.RevisionNumber == pin.Configuration.Value && x.IsSealed, token);
        if (instance is null || revision is null || revision.ImplementationId != pin.Implementation.Value) return false;
        var matches = codecs.Where(x => x.ProviderKindId == instance.ProviderKindId && x.ShapeId == revision.ConfigurationShapeId && x.PayloadFormatVersion == revision.PayloadFormatVersion).ToArray();
        if (matches is not [var codec]) return false;
        PreparedProviderConfiguration prepared;
        try { prepared = codec.Encode(codec.Decode(revision.PayloadJson)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return false; }
        return prepared.PayloadJson == revision.PayloadJson && prepared.ImplementationId == pin.Implementation.Value &&
            admission.ValidateCreationGraph(inputs.Configuration!.Manifest.Spec,
                new(instance.ProviderKindId, revision.ConfigurationShapeId, revision.ImplementationId), inputs, prepared, graph);
    }
    internal static async Task<bool> CheckCredentialMetadataAsync(ManifestCatalogDbContext db, ReadinessMetadata metadata, CancellationToken token)
    {
        foreach (var credential in metadata.Credentials)
        {
            var slot = await PostgreSqlGeneratedCredentialSlots.ReadAsync(db, new(credential.Input.Owner, credential.Input.SlotId), credential.Input.GenerationProfile, token, validateMaterial: false);
            if (slot?.Outcome != GeneratedCredentialOutcome.Existing || slot.Reference != credential.Output.Reference) return false;
        }
        return true;
    }

    internal static async Task<ReadinessPreparation?> PrepareAsync(ManifestCatalogDbContext db, ReadinessMetadata? metadata, CancellationToken token)
    {
        if (metadata is null || !await CheckCredentialMetadataAsync(db, metadata, token)) return null;
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Material preflight must precede the completion transaction.");
        foreach (var credential in metadata.Credentials)
        {
            var slot = await PostgreSqlGeneratedCredentialSlots.ReadAsync(db, new(credential.Input.Owner, credential.Input.SlotId), credential.Input.GenerationProfile, token);
            if (slot?.Outcome != GeneratedCredentialOutcome.Existing || slot.Reference != credential.Output.Reference) return null;
        }
        return new(metadata.Deployment, metadata.Generation, metadata.Fingerprint,
            new System.Collections.ObjectModel.ReadOnlyDictionary<long, string>(metadata.Sources.ToDictionary(x => x.Row.Generation, x => x.Row.Fingerprint)), metadata.Management, metadata.Credentials);
    }

    internal static async Task<ReadinessPreparation?> PrepareAsync(ManifestCatalogDbContext db, Guid deployment, long generation,
        IEnumerable<IExternalOperationOutputContract> registrations, IEnumerable<IProviderConfigurationCodec> codecs,
        CancellationToken token, DurableActionAdmission? admission = null, PendingResult? pending = null)
    {
        var row = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
        // A validated historical receipt is intentionally independent of today's installation/material.
        if (row is not null && await ReadAsync(db, row, token) is not null) return null;
        return await PrepareAsync(db, await InspectMetadataAsync(db, deployment, generation, registrations, codecs, token, admission, pending), token);
    }

    private static bool MatchesPreparation(ReadinessPreparation prepared, Guid deployment, long generation,
        string fingerprint, ResourceReadyResult[] results) =>
        prepared.Deployment == deployment && prepared.Generation == generation && prepared.Fingerprint == fingerprint &&
        results.All(resource => prepared.Management.GetValueOrDefault(resource.Resource) == resource.ManagementId &&
            prepared.Sources.GetValueOrDefault(resource.SourceGeneration) == resource.SourceFingerprint &&
            resource.Operations.All(operation => operation.Outputs.Where(output => output.SecretId is not null).All(output =>
                prepared.Credentials.Any(credential => credential.Generation == resource.SourceGeneration && credential.Operation == operation.OperationId &&
                    credential.Fingerprint == output.Fingerprint && Guid.Parse(credential.Output.Reference.SecretId.ToString()) == output.SecretId &&
                    Guid.Parse(credential.Output.Reference.SecretVersionId.ToString()) == output.SecretVersionId))));
}
