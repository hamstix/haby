using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Catalog-only evidence checks. Original sources are direct, bounded and never rewritten.</summary>
internal static class ResourceContinuity
{
    internal static async Task LockAsync(ManifestCatalogDbContext db, Guid deployment, CancellationToken token)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM resource_management WHERE deployment_id = {deployment} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT r.provider_instance_id FROM provider_resource_reservations r JOIN application_deployments d ON d.environment_record_id = r.environment_record_id AND d.application_record_id = r.application_record_id WHERE d.id = {deployment} FOR SHARE OF r", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT deployment_id FROM application_deployment_generations WHERE deployment_id = {deployment} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT deployment_id FROM durable_create_operations WHERE deployment_id = {deployment} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT deployment_id FROM durable_create_operation_attempts WHERE deployment_id = {deployment} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT deployment_id FROM operation_outputs WHERE deployment_id = {deployment} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT s.secret_id FROM generated_credential_slots s JOIN application_deployments d ON d.environment_record_id = s.environment_record_id AND d.application_record_id = s.application_record_id WHERE d.id = {deployment} FOR SHARE OF s", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT s.id FROM secrets s JOIN application_deployments d ON d.environment_record_id = s.environment_record_id AND d.application_record_id = s.application_record_id WHERE d.id = {deployment} FOR SHARE OF s", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT v.id FROM secret_versions v JOIN generated_credential_slots s ON s.version_id = v.id JOIN application_deployments d ON d.environment_record_id = s.environment_record_id AND d.application_record_id = s.application_record_id WHERE d.id = {deployment} FOR SHARE OF v", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT p.secret_version_id FROM secret_payloads p JOIN generated_credential_slots s ON s.version_id = p.secret_version_id JOIN application_deployments d ON d.environment_record_id = s.environment_record_id AND d.application_record_id = s.application_record_id WHERE d.id = {deployment} FOR SHARE OF p", token);
    }
    internal static DurablePlanningInputs Inputs(DeploymentGenerationRow row)
    {
        var inputs = new DurablePlanningInputs(EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString()), EnvironmentId.Create(row.EnvironmentKey),
            ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()), ManifestRevisionNumber.Create(row.RevisionNumber)),
            ProviderAlias.Create(row.ProviderAlias), ProviderInstanceId.Parse(row.ProviderInstanceId.ToString()), ProviderConfigurationRevisionNumber.Create(row.ConfigurationRevisionNumber), row.ImplementationId);
        inputs = inputs with { Configuration = ConfigurationPlanningSnapshotCodec.Decode(row.ConfigurationJson, inputs.Revision) };
        if (DurableOperationGraphCodec.Fingerprint(inputs, DurableOperationGraphCodec.Decode(row.GraphJson)) != row.Fingerprint) throw new InvalidDataException();
        return inputs;
    }

    internal static async Task<bool> ValidateOutputsAsync(ManifestCatalogDbContext db, Guid deployment,
        DurableOperationGraph reuse, IEnumerable<IExternalOperationOutputContract> registrations,
        IEnumerable<IProviderConfigurationCodec> codecs, DurableActionAdmission admission, DurablePlanningInputs target, CancellationToken token, bool validateMaterial = false, long? creationGeneration = null)
    {
        var contracts = registrations.ToArray();
        foreach (var generation in reuse.ReusedResources.Concat(reuse.RetainedResources).Select(x => x.SourceGeneration).Concat(creationGeneration is { } g ? [g] : []).Distinct())
        {
            var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
            var inputs = Inputs(source);
            var provider = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == source.ProviderInstanceId &&
                x.RevisionNumber == source.ConfigurationRevisionNumber && x.EnvironmentRecordId == source.EnvironmentRecordId && x.IsSealed, token);
            var instance = await db.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.ProviderInstanceId && x.EnvironmentRecordId == source.EnvironmentRecordId, token);
            if (provider is null || instance is null || provider.ImplementationId != source.ImplementationId) return false;
            var matches = codecs.Where(x => x.ProviderKindId == instance.ProviderKindId && x.ShapeId == provider.ConfigurationShapeId && x.PayloadFormatVersion == provider.PayloadFormatVersion).ToArray();
            if (matches.Length != 1) return false;
            PreparedProviderConfiguration configuration;
            try { configuration = matches[0].Encode(matches[0].Decode(provider.PayloadJson)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException) { return false; }
            if (configuration.PayloadJson != provider.PayloadJson || configuration.ImplementationId != source.ImplementationId) return false;
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            var adapter = new ProviderAdapterKey(instance.ProviderKindId, provider.ConfigurationShapeId, provider.ImplementationId);
            if (!admission.Supports(graph) || !admission.ValidateCreationGraph(inputs.Configuration!.Manifest.Spec, adapter, inputs, configuration, graph)) return false;
            foreach (var requirement in reuse.ReusedResources.Where(x => x.SourceGeneration == generation))
                if (!admission.CanReuseResource(inputs.Configuration.Manifest.Spec, target.Configuration!.Manifest.Spec, requirement.Resource, adapter, target, configuration)) return false;
            foreach (var requirement in reuse.RetainedResources.Where(x => x.SourceGeneration == generation))
                if (!admission.CanRetainResource(inputs.Configuration.Manifest.Spec, target.Configuration!.Manifest.Spec, requirement.Resource, adapter, target, configuration)) return false;
            var all = await db.OperationOutputs.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == generation).ToArrayAsync(token);
            var references = all.Select(x => new ConfirmedOperationOutput(OperationId.Create(x.OperationId), new(x.Contract, x.Key, x.Payload))).ToArray();
            foreach (var node in graph.Nodes)
            {
                var outputs = await db.OperationOutputs.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == generation && x.OperationId == node.Id.Value).ToArrayAsync(token);
                var validators = contracts.Where(x => x.Producer == node.Action).ToArray();
                if (validators.Length > 1 || (validators.Length == 1 && outputs.Length != 1)) return false;
                foreach (var output in outputs)
                {
                    if (GeneratedCredentialContracts.IsProducer(node))
                    {
                        var input = GeneratedCredentialInputCodec.Decode(node.Input);
                        var credential = GeneratedCredentialOutputCodec.Decode(output.Contract, output.Payload);
                        if (outputs.Length != 1 || input is null || credential is null || output.Key != GeneratedCredentialContracts.Password) return false;
                        var slot = await PostgreSqlGeneratedCredentialSlots.ReadAsync(db, new(input.Owner, input.SlotId), input.GenerationProfile, token, validateMaterial);
                        if (slot?.Outcome != GeneratedCredentialOutcome.Existing || slot.Reference != credential.Reference) return false;
                        continue;
                    }
                    var value = new ExternalOperationOutput(output.Contract, output.Key, output.Payload);
                    if (validators.Length != 1 || !validators[0].Validate(node, value) ||
                        !await validators[0].ValidateReuseAsync(new(ApplicationDeploymentId.Parse(deployment.ToString()), DeploymentGeneration.Create(generation), node.Id),
                            new(inputs, graph, node, inputs.Configuration!.Manifest.Spec), value, references, configuration, token)) return false;
                }
            }
        }
        return true;
    }
    internal static async Task<string?> EvidenceAsync(ManifestCatalogDbContext db, ResourceManagementRow management,
        CancellationToken token, bool requireApplied = true)
    {
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeploymentId == management.Deployment && x.Generation == management.SourceGeneration, token);
        if (source is null || source.EnvironmentRecordId != management.Environment || source.ApplicationRecordId != management.Application)
            return null;
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        _ = Inputs(source);
        // The creation generation may contain reused peers. Its aggregate must have
        // completed; inspect this Resource's original nodes directly, never recurse.
        if (requireApplied && !await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == management.Deployment &&
            x.AppliedGeneration >= management.SourceGeneration, token)) return null;
        if (graph.ReusedResources.Any(r => r.Resource.Value == management.Resource)) return null;
        var ids = graph.Nodes.Select(x => x.Id.Value).ToArray();
        if (await db.DurableOperations.CountAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation, token) != ids.Length ||
            await db.DurableOperations.AnyAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation &&
                (!ids.Contains(x.OperationId) || x.State != nameof(DurableOperationState.Succeeded)), token)) return null;
        var nodes = graph.Nodes.Where(x => x.ResourceId.Value == management.Resource).ToArray();
        if (nodes.Length == 0) return null;
        var fields = new List<string> { source.Fingerprint, management.Id.ToString(), management.Resource };
        foreach (var node in nodes)
        {
            var operation = await db.DurableOperations.AsNoTracking().SingleOrDefaultAsync(x =>
                x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == node.Id.Value, token);
            if (operation?.State != nameof(DurableOperationState.Succeeded) || operation.CurrentAttemptId is not { } attempt) return null;
            var local = GeneratedCredentialContracts.IsProducer(node);
            if (!await db.DurableOperationAttempts.AsNoTracking().AnyAsync(x => x.AttemptId == attempt &&
                x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == node.Id.Value &&
                x.AttemptNumber == operation.AttemptNumber && x.Outcome == nameof(DurableAttemptOutcome.Succeeded) &&
                x.FinishedAt != null && (local ? x.DispatchedAt == null : x.DispatchedAt != null), token)) return null;
            fields.Add(node.Id.Value); fields.Add(attempt.ToString());
            var outputs = await db.OperationOutputs.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId &&
                x.Generation == source.Generation && x.OperationId == node.Id.Value).OrderBy(x => x.Contract).ThenBy(x => x.Key).ToArrayAsync(token);
            foreach (var output in outputs) { fields.Add(output.Contract); fields.Add(output.Key); fields.Add(output.Payload); }
            // Every declared consumer selector must have its exact original producer output.
            foreach (var selector in graph.Nodes.SelectMany(x => x.OutputReferences).Where(x => x.ProducerOperationId == node.Id))
                if (!outputs.Any(x => x.Contract == selector.OutputContract && x.Key == selector.OutputKey)) return null;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields.ToArray(), ManifestCatalogJsonContext.Default.StringArray))));
    }

    internal static async Task<bool> ValidateAsync(ManifestCatalogDbContext db, Guid deployment,
        DurableOperationGraph graph, CancellationToken token)
    {
        if (graph.Baseline is not { } baseline || Guid.Parse(baseline.Deployment.ToString()) != deployment) return false;
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, token);
        if (owner is null) return false;
        var previous = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeploymentId == deployment && x.Generation == baseline.Generation, token);
        if (previous?.Fingerprint != baseline.Fingerprint) return false;
        var accepted = Inputs(previous);
        var previousGraph = DurableOperationGraphCodec.Decode(previous.GraphJson);
        var inventory = accepted.Configuration!.Manifest.Spec.Resources.Keys.Concat(previousGraph.RetainedResources.Select(x => x.Resource)).ToHashSet();
        var requirements = graph.ReusedResources.Concat(graph.RetainedResources).ToArray();
        if (!inventory.SetEquals(requirements.Select(x => x.Resource))) return false;
        foreach (var reuse in requirements)
        {
            var managed = await db.ResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reuse.ManagementId, token);
            if (managed is null || managed.Deployment != deployment || managed.Environment != owner.EnvironmentRecordId ||
                managed.Application != owner.ApplicationRecordId || managed.Resource != reuse.Resource.Value || managed.SourceGeneration != reuse.SourceGeneration ||
                await EvidenceAsync(db, managed, token) != reuse.EvidenceFingerprint) return false;
            if (graph.ReusedResources.Contains(reuse) && reuse.Reintroduced != previousGraph.RetainedResources.Any(x => x.Resource == reuse.Resource)) return false;
            var original = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == reuse.SourceGeneration, token);
            if (original.ProviderInstanceId != previous.ProviderInstanceId || original.ConfigurationRevisionNumber != previous.ConfigurationRevisionNumber ||
                original.ImplementationId != previous.ImplementationId) return false;
        }
        return graph.ReusedResources.Count > 0;
    }

    internal static async Task<DurableOperationKey?> ResolveAsync(ManifestCatalogDbContext db, DurableOperationKey requested,
        ManifestLocalId resource, CancellationToken token)
    {
        var deployment = Guid.Parse(requested.DeploymentId.ToString());
        var target = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeploymentId == deployment && x.Generation == requested.Generation.Value, token);
        if (target is null) return null;
        var graph = DurableOperationGraphCodec.Decode(target.GraphJson);
        var inputs = Inputs(target);
        if (graph.Baseline is null) return requested;
        if (!inputs.Configuration!.Manifest.Spec.Resources.ContainsKey(resource)) return null;
        var reuse = graph.ReusedResources.SingleOrDefault(x => x.Resource == resource);
        if (!await ValidateAsync(db, deployment, graph, token)) return null;
        return reuse is not null ? requested with { Generation = DeploymentGeneration.Create(reuse.SourceGeneration) }
            : graph.Nodes.Any(n => n.ResourceId == resource && n.Id == requested.OperationId) ? requested : null;
    }
}
