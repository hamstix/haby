using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static class ResourceProviderDispatch
{
    internal static async Task<IReadOnlyDictionary<ManifestLocalId, DurableResourceProviderContext>> CaptureAsync(
        ManifestCatalogDbContext db, DurablePlanningInputs inputs, IEnumerable<IProviderConfigurationCodec> codecs, CancellationToken token)
    {
        var result = new Dictionary<ManifestLocalId, DurableResourceProviderContext>();
        foreach (var resource in inputs.Providers.Declaration.Spec.Resources.Keys)
        {
            var pin = inputs.Providers.Resource(resource);
            var id = Guid.Parse(pin.Instance.ToString());
            var instance = await db.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.EnvironmentRecordId == Guid.Parse(inputs.Environment.ToString()), token);
            var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == id &&
                x.EnvironmentRecordId == Guid.Parse(inputs.Environment.ToString()) && x.RevisionNumber == pin.Configuration.Value && x.IsSealed, token);
            if (instance is null || revision is null || revision.ImplementationId != pin.Implementation.Value) throw new InvalidDataException("Exact Resource Provider is unavailable.");
            var matches = codecs.Where(x => x.ProviderKindId == instance.ProviderKindId && x.ShapeId == revision.ConfigurationShapeId && x.PayloadFormatVersion == revision.PayloadFormatVersion).ToArray();
            if (matches is not [var codec]) throw new InvalidDataException("Exact Resource configuration codec is unavailable.");
            PreparedProviderConfiguration configuration;
            try { configuration = codec.Encode(codec.Decode(revision.PayloadJson)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or FormatException)
            { throw new InvalidDataException("Exact Resource configuration is unavailable."); }
            var references = await db.ProviderRevisionSecretRefs.AsNoTracking().Where(x => x.ProviderInstanceId == id && x.RevisionNumber == revision.RevisionNumber).ToArrayAsync(token);
            if (configuration.ProviderKindId != instance.ProviderKindId || configuration.ShapeId != revision.ConfigurationShapeId ||
                configuration.PayloadFormatVersion != revision.PayloadFormatVersion || configuration.SecretReferences.Count != references.Length ||
                configuration.SecretReferences.Any(r => !references.Any(x => x.Slot == r.Slot && x.SecretId.ToString() == r.Reference.SecretId.ToString() && x.SecretVersionId.ToString() == r.Reference.SecretVersionId.ToString())) ||
                configuration.PayloadJson != revision.PayloadJson || configuration.ImplementationId != pin.Implementation.Value) throw new InvalidDataException("Exact Resource configuration is inconsistent.");
            result.Add(resource, new(new(instance.ProviderKindId, revision.ConfigurationShapeId, revision.ImplementationId), configuration));
        }
        return result;
    }

    internal static async Task<bool> ValidateCreationAsync(ManifestCatalogDbContext db, DurablePlanningInputs inputs,
        DurableOperationGraph graph, IEnumerable<IProviderConfigurationCodec> codecs, DurableActionAdmission admission, CancellationToken token)
    {
        var exact = await CaptureAsync(db, inputs, codecs, token);
        var context = exact.Values.First();
        return admission.ValidateCreationGraph(inputs.Configuration!.Manifest.Spec, context.Provider, inputs, context.Configuration, graph, exact);
    }
}

public sealed partial class PostgreSqlDurableDeploymentPlanner
{
    private async Task<(DurablePlanStatus Status, ExactProviderResolution[] Resources, ProviderConfigurationRevisionRow? Revision)> CaptureResourceProvidersAsync(
        ManifestCatalogDbContext db, NormalizedApplicationManifest manifest, EnvironmentRecordId environment, CancellationToken token)
    {
        var entries = new List<ExactProviderResolution>();
        ProviderConfigurationRevisionRow? first = null;
        foreach (var group in manifest.Spec.Resources.GroupBy(x => x.Value.ProviderRef).OrderBy(x => x.Key.Value, StringComparer.Ordinal))
        {
            var env = Guid.Parse(environment.ToString());
            await PostgreSqlResourceNamingStore.LockAsync(db, env, group.Key.Value, true, token);
            var resolved = await aliases.ResolveAsync(environment, group.Key, token);
            if (resolved is null) return (DurablePlanStatus.AliasNotResolved, [], null);
            if (resolved.Alias != group.Key) return (DurablePlanStatus.PlanInputUnavailable, [], null);
            var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == Guid.Parse(resolved.Binding.InstanceId.ToString()) &&
                x.RevisionNumber == resolved.Binding.RevisionNumber.Value && x.EnvironmentRecordId == env && x.IsSealed, token);
            if (revision is null || revision.ImplementationId != resolved.ImplementationId) return (DurablePlanStatus.ProviderRevisionUnavailable, [], null);
            first ??= revision;
            ResourceNamingSnapshot naming;
            try { naming = (await PostgreSqlResourceNamingStore.CaptureAsync(db, env, group.Key.Value, token)).Snapshot; }
            catch (InvalidDataException) { return (DurablePlanStatus.PlanInputUnavailable, [], null); }
            entries.AddRange(group.Select(x => new ExactProviderResolution(new ProviderInput.Resource(x.Key), environment, group.Key,
                resolved.Binding.InstanceId, resolved.Binding.RevisionNumber, ProviderImplementationId.Create(resolved.ImplementationId), ResourceNamingCodec.Exact(naming))));
        }
        return (DurablePlanStatus.Planned, entries.ToArray(), first);
    }
}
