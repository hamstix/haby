using System.Data;
using System.Data.Common;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>One coherent, uncached application-use decision. Neither authentication route can override it.</summary>
public sealed class PostgreSqlConfigurationApplicationAuthorization(IDbContextFactory<ManifestCatalogDbContext> contexts,
    PostgreSqlExternalOperationOutputs outputs) : IConfigurationApplicationAuthorization
{
    public async ValueTask<bool> AuthorizeAsync(ConfigurationDeliveryArtifact artifact,
        IReadOnlyList<ConfigurationSharedCredentialUse> uses, CancellationToken token)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            if (await SourceAsync(db, artifact, requireDocument: true, token) is null) return false;
            var document = Guid.Parse(artifact.Revision.Metadata.Id.ToString());
            if (await db.ConfigurationSharedUses.CountAsync(x => x.Document == document, token) != uses.Count) return false;
            var saved = await db.ConfigurationSharedUses.AsNoTracking().Where(x => x.Document == document).ToArrayAsync(token);
            foreach (var use in uses)
            {
                token.ThrowIfCancellationRequested();
                var association = await outputs.ConfirmReferenceDeliveryAsync(db, artifact, use, token);
                if (association is null || !await HasUseAsync(db, use.Scope, token)) return false;
                var expected = Row(artifact, use, association.Value);
                var exact = saved.SingleOrDefault(x => x.ProjectionHash == expected.ProjectionHash);
                if (exact is null || ManifestCatalogDbContext.ConfigurationSharedUseIdentity.From(exact) !=
                    ManifestCatalogDbContext.ConfigurationSharedUseIdentity.From(expected)) return false;
            }
            return uses.Count != 0;
        }
        catch (InvalidDataException) { return false; }
        catch (DbException) { throw new ConfigurationDeliveryStorageException(); }
    }

    // Called only by the existing document-acceptance transaction; no inferred or unmaterialized dependencies.
    internal async Task<bool> CaptureAsync(ManifestCatalogDbContext db, ConfigurationDeliveryArtifact artifact,
        IReadOnlyList<ConfigurationSharedCredentialUse> uses, CancellationToken token)
    {
        if (uses.Count == 0) return true;
        var environment = Guid.Parse(artifact.Environment.ToString());
        if (!await db.AcquireDocumentUseGatesAsync(environment, uses.Select(x => Guid.Parse(x.Scope.Provider.ToString())), token) ||
            await SourceAsync(db, artifact, requireDocument: false, token) is null) return false;
        var rows = new List<ConfigurationSharedUseRow>();
        foreach (var use in uses)
        {
            var association = await outputs.ConfirmReferenceDeliveryAsync(db, artifact, use, token);
            if (association is null || !await HasUseAsync(db, use.Scope, token)) return false;
            rows.Add(Row(artifact, use, association.Value));
        }
        if (rows.Select(x => x.ProjectionHash).Distinct(StringComparer.Ordinal).Count() != rows.Count) return false;
        foreach (var row in rows)
        {
            db.AuthorizeDocumentSharedUse(row);
            db.ConfigurationSharedUses.Add(row);
        }
        return true;
    }

    private static ConfigurationSharedUseRow Row(ConfigurationDeliveryArtifact artifact, ConfigurationSharedCredentialUse use, Guid association) => new()
    {
        Document = Guid.Parse(artifact.Revision.Metadata.Id.ToString()),
        ProjectionHash = ResourcesReadyCheckpointCodec.Hash(use.Projection),
        Projection = use.Projection,
        Environment = Guid.Parse(artifact.Environment.ToString()),
        Application = Guid.Parse(artifact.Revision.Metadata.Revision.ApplicationRecordId.ToString()),
        Resource = use.Pin.ResourceLocalId.Value,
        Association = association,
        Provider = Guid.Parse(use.Scope.Provider.ToString()),
        Revision = use.Scope.Revision.Value,
        Slot = use.Scope.Slot,
        Secret = Guid.Parse(use.Scope.Reference.SecretId.ToString()),
        Version = Guid.Parse(use.Scope.Reference.SecretVersionId.ToString()),
        Producer = use.Pin.Producer.Value,
        Attempt = use.Attempt,
        Formatter = use.Pin.Formatter.Value,
        Implementation = use.Pin.FormatterImplementation.Value
    };

    private static async Task<DeploymentGenerationRow?> SourceAsync(ManifestCatalogDbContext db, ConfigurationDeliveryArtifact artifact,
        bool requireDocument, CancellationToken token)
    {
        var revision = artifact.Revision;
        var metadata = revision.Metadata;
        var deployment = Guid.Parse(metadata.Target.Deployment.ToString());
        var query = db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == metadata.Target.Generation.Value);
        if (await query.AnyAsync(x => x.ConfigurationJson.Length > ConfigurationDocumentCodec.MaxBytes || x.GraphJson.Length > ConfigurationDocumentCodec.MaxBytes ||
            x.ProviderInputsJson.Length > ConfigurationDocumentCodec.MaxBytes, token)) throw new ConfigurationDocumentLimitException();
        var source = await query.SingleOrDefaultAsync(token);
        if (source is null || source.EnvironmentRecordId.ToString() != artifact.Environment.ToString() ||
            source.ApplicationRecordId.ToString() != metadata.Revision.ApplicationRecordId.ToString() || source.RevisionNumber != metadata.Revision.RevisionNumber.Value ||
            source.PlanId.ToString() != metadata.Plan.ToString() || source.EnvironmentKey != metadata.Environment.Value ||
            source.ConfigurationJson != revision.FrozenInputs || PostgreSqlConfigurationDocumentStore.InputFingerprint(source) != metadata.FrozenInputFingerprint ||
            ConfigurationDocumentCodec.Fingerprint(ConfigurationDocumentCodec.Encode(revision)) != metadata.Fingerprint) return null;
        _ = source.Inputs();
        var frozen = ConfigurationPlanningSnapshotCodec.Decode(source.ConfigurationJson, metadata.Revision);
        if (frozen.Manifest.ApplicationId != metadata.Application || !frozen.Layouts.Any(x => x.Component == metadata.Target.Component &&
            x.Documents.Any(d => d.Definition.Id == metadata.Target.Document && d.Definition.FileName == metadata.FileName))) return null;
        if (requireDocument)
        {
            var id = Guid.Parse(metadata.Id.ToString());
            if (!await db.ConfigurationDocuments.AsNoTracking().AnyAsync(x => x.Id == id && x.DeploymentId == deployment &&
                x.Generation == metadata.Target.Generation.Value && x.Component == metadata.Target.Component.Value && x.Document == metadata.Target.Document.Value &&
                x.Fingerprint == metadata.Fingerprint, token)) return null;
        }
        return source;
    }

    private static Task<bool> HasUseAsync(ManifestCatalogDbContext db, SharedCredentialScope scope, CancellationToken token)
    {
        if (scope.Use != SharedCredentialUse.ApplicationDelivery) return Task.FromResult(false);
        var environment = Guid.Parse(scope.Environment.ToString()); var provider = Guid.Parse(scope.Provider.ToString());
        var secret = Guid.Parse(scope.Reference.SecretId.ToString()); var version = Guid.Parse(scope.Reference.SecretVersionId.ToString());
        return (from binding in db.SharedCredentialBindings.AsNoTracking()
                join revision in db.ProviderConfigurationRevisions.AsNoTracking() on
                    new { binding.ProviderInstanceId, binding.RevisionNumber, binding.EnvironmentRecordId } equals
                    new { revision.ProviderInstanceId, revision.RevisionNumber, revision.EnvironmentRecordId }
                join metadata in db.Secrets.AsNoTracking() on binding.SecretId equals metadata.Id
                join materialVersion in db.SecretVersions.AsNoTracking() on binding.SecretVersionId equals materialVersion.Id
                where binding.EnvironmentRecordId == environment && binding.ProviderInstanceId == provider && binding.RevisionNumber == scope.Revision.Value &&
                    binding.Slot == scope.Slot && binding.Use == "ApplicationDelivery" && binding.SecretId == secret && binding.SecretVersionId == version && revision.IsSealed &&
                    metadata.EnvironmentRecordId == environment && metadata.Owner == "Environment" && metadata.Purpose == "SharedApplicationCredential" && metadata.Name != null &&
                    metadata.ApplicationRecordId == null && metadata.ResourceLocalId == null && materialVersion.SecretId == secret &&
                    !db.SharedCredentialRevocations.Any(x => x.ProviderInstanceId == provider && x.RevisionNumber == scope.Revision.Value && x.Slot == scope.Slot)
                select binding.Slot).AnyAsync(token);
    }
}
