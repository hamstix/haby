using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.PostgreSql;

internal sealed class PostgreSqlDurableCreateInputResolver(IManifestRevisionQuery manifests)
{
    public async ValueTask<(PostgreSqlDatabaseCreateInput? Input, DurableCreateOperationReason? Reason, bool Definitive)>
        ResolveAsync(DurableCreateOperationKey key, DurableCreateOperation operation,
            CancellationToken cancellationToken)
    {
        ManifestRevision? revision;
        try { revision = await manifests.FindAsync(operation.Revision, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException)
        {
            return (null, DurableCreateOperationReason.InputUnavailable, true);
        }
        catch (Exception)
        {
            return (null, DurableCreateOperationReason.InputUnavailable, false);
        }
        if (revision is null)
            return (null, DurableCreateOperationReason.InputUnavailable, false);
        var spec = revision.Declaration.Spec;
        if (spec.Resources.Count != 1 || spec.Components.Count != 0 || spec.Exports.Count != 0 ||
            spec.Parameters.Count != 0)
            return (null, DurableCreateOperationReason.InputInvalid, true);
        var entry = spec.Resources.Single();
        if (entry.Key != key.ResourceId) return (null, DurableCreateOperationReason.InputInvalid, true);
        var resource = entry.Value;
        var name = PostgreSqlDatabaseCreateValidation.Name(resource);
        if (name is null) return (null, DurableCreateOperationReason.InputInvalid, true);
        return (new PostgreSqlDatabaseCreateInput(operation.Environment, operation.ProviderInstance,
            operation.ConfigurationRevision, operation.ImplementationId, name), null, false);
    }
}
