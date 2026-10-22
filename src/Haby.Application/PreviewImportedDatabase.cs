using System.Text.RegularExpressions;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>Request-scoped, non-secret metadata. It is never accepted as durable Provider state.</summary>
public sealed record PreviewProviderSelection(string Alias, string InstanceId, long ConfigurationRevision,
    string ImplementationId);

public abstract record PreviewDatabaseResult
{
    private PreviewDatabaseResult() { }
    public sealed record WouldCreate(string EnvironmentId, string ResourceId, string DatabaseName,
        string ProviderAlias, string ProviderInstanceId, long ConfigurationRevision,
        string ImplementationId) : PreviewDatabaseResult;
    public sealed record RevisionNotFound : PreviewDatabaseResult;
    public sealed record InvalidContext : PreviewDatabaseResult;
    public sealed record AliasNotResolved(string Alias) : PreviewDatabaseResult;
    public sealed record CapabilityMismatch(string Alias) : PreviewDatabaseResult;
    public sealed record UnsupportedShape : PreviewDatabaseResult;
}

/// <summary>Evaluates one accepted revision without creating or accepting desired state.</summary>
public sealed class PreviewImportedDatabase(IManifestRevisionQuery revisions, ICheckpointProviderCapability capability)
{
    private static readonly Regex SafeInstance = new(@"\A[a-z][a-z0-9-]{0,63}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SafeDatabase = new(@"\A[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async ValueTask<PreviewDatabaseResult> ExecuteAsync(ManifestRevisionRef reference, string environmentId,
        IReadOnlyList<PreviewProviderSelection> selections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!ValidContext(environmentId, selections)) return new PreviewDatabaseResult.InvalidContext();
        var revision = await revisions.FindAsync(reference, cancellationToken);
        if (revision is null) return new PreviewDatabaseResult.RevisionNotFound();
        var spec = revision.Declaration.Spec;
        if (spec.Resources.Count != 1 || spec.Components.Count != 0 || spec.Exports.Count != 0 ||
            spec.Parameters.Count != 0) return new PreviewDatabaseResult.UnsupportedShape();
        var entry = spec.Resources.Single();
        var resource = entry.Value;
        if (resource.Type != PostgreSqlDatabaseContract.Id || resource.Properties.Members.Count != 1 ||
            !resource.Properties.Members.TryGetValue("name", out var value) ||
            value is not ConfigurationString name || !SafeDatabase.IsMatch(name.Value))
            return new PreviewDatabaseResult.UnsupportedShape();
        if (selections.Count != 1) return new PreviewDatabaseResult.InvalidContext();

        var selected = selections.ToDictionary(item => ProviderAlias.Create(item.Alias), item =>
            new ProviderResolution(item.InstanceId, item.ConfigurationRevision, item.ImplementationId));
        var resolution = CheckpointPlanningRules.Resolve(revision.Declaration,
            alias => selected.GetValueOrDefault(alias), capability);
        return resolution switch
        {
            CheckpointPlanningResolution.Blocked { Error: PlanChangeSetResult.ProviderAliasNotResolved missing } =>
                new PreviewDatabaseResult.AliasNotResolved(missing.Alias.Value),
            CheckpointPlanningResolution.Blocked { Error: PlanChangeSetResult.ProviderCapabilityMismatch mismatch } =>
                new PreviewDatabaseResult.CapabilityMismatch(mismatch.Alias.Value),
            CheckpointPlanningResolution.Resolved { Providers: { Count: 1 } providers } =>
                new PreviewDatabaseResult.WouldCreate(environmentId, entry.Key.Value, name.Value,
                    providers[0].Alias.Value, providers[0].Resolution.ProviderInstanceId,
                    providers[0].Resolution.ConfigurationRevision, providers[0].Resolution.ImplementationId),
            _ => new PreviewDatabaseResult.UnsupportedShape()
        };
    }

    private static bool ValidContext(string environmentId, IReadOnlyList<PreviewProviderSelection> selections)
    {
        if (selections is null || selections.Count is < 1 or > 16) return false;
        try { _ = EnvironmentId.Create(environmentId); }
        catch (ArgumentException) { return false; }
        var aliases = new HashSet<ProviderAlias>();
        foreach (var item in selections)
        {
            if (item is null || item.ConfigurationRevision <= 0 ||
                item.InstanceId is null || !SafeInstance.IsMatch(item.InstanceId) ||
                item.ImplementationId is null || item.ImplementationId.Length is < 1 or > 128 ||
                item.ImplementationId.Any(character => character is < ' ' or > '~')) return false;
            ProviderAlias alias;
            try { alias = ProviderAlias.Create(item.Alias); }
            catch (ArgumentException) { return false; }
            if (!aliases.Add(alias)) return false;
        }
        return true;
    }
}
