using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static partial class DurableAggregateCompletion
{
    private static async Task<ResourceReadyResult[]?> BuildResultsAsync(ManifestCatalogDbContext db, DeploymentGenerationRow target,
        DurableOperationGraph graph, IExternalOperationOutputContract[]? contracts, IProviderConfigurationCodec[] codecs, CancellationToken token, bool checkCredential)
    {
        var current = await db.DurableOperations.AsNoTracking().Where(x => x.DeploymentId == target.DeploymentId && x.Generation == target.Generation).ToArrayAsync(token);
        if (current.Length != graph.Nodes.Count || graph.Nodes.Any(n => !current.Any(x => x.OperationId == n.Id.Value && x.State == nameof(DurableOperationState.Succeeded)))) return null;
        var resources = graph.Nodes.Select(n => n.ResourceId.Value).Concat(graph.ReusedResources.Select(r => r.Resource.Value))
            .Concat(graph.RetainedResources.Select(r => r.Resource.Value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var results = new List<ResourceReadyResult>();
        foreach (var resource in resources)
        {
            var reuse = graph.ReusedResources.Concat(graph.RetainedResources).SingleOrDefault(x => x.Resource.Value == resource);
            var managed = await db.ResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == target.DeploymentId && x.Resource == resource, token);
            var generation = reuse?.SourceGeneration ?? target.Generation;
            if (managed is null || managed.Environment != target.EnvironmentRecordId || managed.Application != target.ApplicationRecordId ||
                managed.SourceGeneration != generation || reuse is not null && managed.Id != reuse.ManagementId) return null;
            var source = generation == target.Generation ? target : await db.DeploymentGenerations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.DeploymentId == target.DeploymentId && x.Generation == generation, token);
            if (source is null || source.EnvironmentRecordId != target.EnvironmentRecordId || source.ApplicationRecordId != target.ApplicationRecordId) return null;
            var inputs = source.Inputs();
            var original = DurableOperationGraphCodec.Decode(source.GraphJson);
            if (original.ReusedResources.Any(x => x.Resource.Value == resource)) return null;
            var nodes = original.Nodes.Where(n => n.ResourceId.Value == resource).ToArray();
            if (nodes.Length == 0) return null;
            var operations = new List<ResourceOperationResult>();
            foreach (var node in nodes)
            {
                var operation = await db.DurableOperations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == generation && x.OperationId == node.Id.Value, token);
                if (operation?.State != nameof(DurableOperationState.Succeeded) || operation.ReasonCode is not null || operation.FinalizedAt is null || operation.CurrentAttemptId is not { } attemptId) return null;
                var attempt = await db.DurableOperationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == attemptId && x.DeploymentId == source.DeploymentId && x.Generation == generation && x.OperationId == node.Id.Value && x.AttemptNumber == operation.AttemptNumber, token);
                if (attempt?.FinishedAt is null || operation.DispatchedAt != attempt.DispatchedAt || operation.FinalizedAt != attempt.FinishedAt ||
                    (GeneratedCredentialContracts.IsProducer(node) ? attempt.DispatchedAt is not null : attempt.DispatchedAt is null)) return null;
                Guid? recovery = null;
                if (attempt.Outcome != nameof(DurableAttemptOutcome.Succeeded))
                {
                    var decision = await db.PostgreSqlDatabaseRecoveryDecisions.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == generation && x.OperationId == node.Id.Value && x.AttemptId == attemptId, token);
                    if (attempt.Outcome != nameof(DurableAttemptOutcome.Uncertain) || decision is null || !decision.Acknowledged ||
                        decision.AttemptNumber != operation.AttemptNumber || decision.AttemptOutcome != attempt.Outcome ||
                        decision.OperationState != nameof(DurableOperationState.Succeeded) || decision.Reason != nameof(PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase) ||
                        original.Nodes.Count != 1 || original.Baseline is not null || node.Dependencies.Count != 0 || node.OutputReferences.Count != 0) return null;
                    recovery = decision.Id;
                }
                var outputs = await db.OperationOutputs.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == generation && x.OperationId == node.Id.Value)
                    .OrderBy(x => x.Contract).ThenBy(x => x.Key).ToArrayAsync(token);
                if (recovery is not null && outputs.Length != 0) return null;
                if (recovery is null && node.RequiresExternalOutput && outputs.Length != 1) return null;
                if (recovery is null && node.RequiredOutput is { } required &&
                    (outputs[0].Contract != required.Contract || outputs[0].Key != required.Key)) return null;
                if (contracts is not null && recovery is null && !GeneratedCredentialContracts.IsProducer(node))
                {
                    var validators = contracts.Where(x => x.Producer == node.Action).ToArray();
                    if (validators.Length > 1 || validators.Length == 1 && (node.RequiredOutput != validators[0].Identity || outputs.Length != 1 || !validators[0].Validate(node, new(outputs[0].Contract, outputs[0].Key, outputs[0].Payload))) ||
                        validators.Length == 0 && outputs.Length != 0) return null;
                    if (validators.Length == 1 && !await ValidateTypedOutputAsync(db, source, inputs, original, node,
                        outputs[0], validators[0], codecs, token)) return null;
                }
                foreach (var selector in original.Nodes.SelectMany(n => n.OutputReferences).Where(x => x.ProducerOperationId == node.Id))
                    if (!outputs.Any(x => x.Contract == selector.OutputContract && x.Key == selector.OutputKey)) return null;
                var references = new List<ResourceOutputReference>();
                foreach (var output in outputs)
                {
                    Guid? secret = null, version = null;
                    if (GeneratedCredentialContracts.IsProducer(node))
                    {
                        var input = GeneratedCredentialInputCodec.Decode(node.Input);
                        var credential = GeneratedCredentialOutputCodec.Decode(output.Contract, output.Payload);
                        if (outputs.Length != 1 || input is null || credential is null || output.Key != GeneratedCredentialContracts.Password) return null;
                        if (checkCredential)
                        {
                            var slot = await PostgreSqlGeneratedCredentialSlots.ReadAsync(db, new(input.Owner, input.SlotId), input.GenerationProfile, token);
                            if (slot?.Outcome != GeneratedCredentialOutcome.Existing || slot.Reference != credential.Reference) return null;
                        }
                        secret = Guid.Parse(credential.Reference.SecretId.ToString()); version = Guid.Parse(credential.Reference.SecretVersionId.ToString());
                    }
                    references.Add(new(output.Contract, output.Key, ResourcesReadyCheckpointCodec.Hash(output.Payload), secret, version));
                }
                if (GeneratedCredentialContracts.IsProducer(node) && outputs.Length != 1) return null;
                operations.Add(new(node.Id.Value, attemptId, operation.AttemptNumber, recovery, references));
            }
            results.Add(new(resource, managed.Id, generation, source.Fingerprint, graph.RetainedResources.Any(x => x.Resource.Value == resource), reuse?.Reintroduced ?? false, operations));
        }
        return results.ToArray();
    }

    private static async Task<bool> ValidateTypedOutputAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        DurablePlanningInputs inputs, DurableOperationGraph graph, DurableOperationNode node, OperationOutputRow output,
        IExternalOperationOutputContract validator, IProviderConfigurationCodec[] codecs, CancellationToken token)
    {
        var exact = inputs.Providers.Resource(node.ResourceId);
        var providerId = Guid.Parse(exact.Instance.ToString());
        var instance = await db.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == providerId && x.EnvironmentRecordId == source.EnvironmentRecordId, token);
        var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == providerId &&
            x.EnvironmentRecordId == source.EnvironmentRecordId && x.RevisionNumber == exact.Configuration.Value && x.IsSealed, token);
        if (instance is null || revision is null || revision.ImplementationId != exact.Implementation.Value) return false;
        var matches = codecs.Where(x => x.ProviderKindId == instance.ProviderKindId && x.ShapeId == revision.ConfigurationShapeId && x.PayloadFormatVersion == revision.PayloadFormatVersion).ToArray();
        if (matches is not [var codec]) return false;
        PreparedProviderConfiguration prepared;
        try { prepared = codec.Encode(codec.Decode(revision.PayloadJson)); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return false; }
        if (prepared.PayloadJson != revision.PayloadJson || prepared.ImplementationId != exact.Implementation.Value) return false;
        var outputs = await db.OperationOutputs.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation).ToArrayAsync(token);
        var references = outputs.Select(x => new ConfirmedOperationOutput(OperationId.Create(x.OperationId), new(x.Contract, x.Key, x.Payload))).ToArray();
        var rows = await db.ProviderResourceReservations.AsNoTracking().Where(x => x.EnvironmentRecordId == source.EnvironmentRecordId && x.ApplicationRecordId == source.ApplicationRecordId).ToArrayAsync(token);
        var reservations = rows.Select(x => new ProviderResourceReservation(Haby.Domain.ProviderInstanceId.Parse(x.ProviderInstanceId.ToString()),
            inputs.Environment, inputs.Revision.ApplicationRecordId, Haby.Domain.ManifestLocalId.Create(x.ResourceLocalId), x.Namespace, x.Value)).ToArray();
        return validator.ValidateReuse(new(Haby.Domain.ApplicationDeploymentId.Parse(source.DeploymentId.ToString()), Haby.Domain.DeploymentGeneration.Create(source.Generation), node.Id),
            new(inputs, graph, node, inputs.Configuration!.Manifest.Spec), new(output.Contract, output.Key, output.Payload), references, prepared, reservations);
    }
}
