using System.Text.RegularExpressions;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.PostgreSql;

internal sealed class PostgreSqlDurableCreateInputResolver(IManifestRevisionQuery manifests)
{
    private static readonly Regex SafeDatabase = new(@"\A[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async ValueTask<(DurableCreateTargetInput? Input, DurableCreateOperationReason? Reason, bool Definitive)>
        ResolveAsync(DurableCreateOperationKey key, DurableCreateOperation operation,
            CancellationToken cancellationToken)
    {
        ManifestRevision? revision;
        try { revision = await manifests.FindAsync(operation.Revision, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException)
        {
            return (null, DurableCreateOperationReason.ProviderInputUnavailable, true);
        }
        catch (Exception)
        {
            return (null, DurableCreateOperationReason.ProviderInputUnavailable, false);
        }
        if (revision is null)
            return (null, DurableCreateOperationReason.ProviderInputUnavailable, false);
        var spec = revision.Declaration.Spec;
        if (spec.Resources.Count != 1 || spec.Components.Count != 0 || spec.Exports.Count != 0 ||
            spec.Parameters.Count != 0)
            return (null, DurableCreateOperationReason.UnsupportedShape, true);
        var entry = spec.Resources.Single();
        if (entry.Key != key.ResourceId) return (null, DurableCreateOperationReason.UnsupportedShape, true);
        var resource = entry.Value;
        if (resource.Type != PostgreSqlDatabaseContract.Id || resource.Properties.Members.Count != 1 ||
            !resource.Properties.Members.TryGetValue("name", out var value) ||
            value is not ConfigurationString name)
            return (null, DurableCreateOperationReason.UnsupportedShape, true);
        if (!SafeDatabase.IsMatch(name.Value))
            return (null, DurableCreateOperationReason.DatabaseNameInvalid, true);
        return (new DurableCreateTargetInput(operation.Environment, operation.ProviderInstance,
            operation.ConfigurationRevision, operation.ImplementationId, name.Value), null, false);
    }
}
