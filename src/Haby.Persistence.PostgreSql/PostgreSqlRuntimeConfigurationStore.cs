using System.Data;
using System.Data.Common;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed class PostgreSqlRuntimeConfigurationStore(IDbContextFactory<ManifestCatalogDbContext> contexts) : IRuntimeConfigurationStore
{
    public async ValueTask<RuntimeConfigurationAuthority?> AuthorizeAsync(RuntimeAuthenticatedContext identity,
        ConfigurationDocumentRevisionId revision, CancellationToken token)
    {
        try
        {
            var metadata = await RuntimePublicationAuthorization.AuthorizeAsync(contexts, identity.Binding, Guid.Parse(revision.ToString()), token);
            if (metadata is null || metadata.Environment != identity.Environment || metadata.ManagedWorkload != identity.ManagedWorkload ||
                metadata.Provider != identity.Provider) return null;
            return new(identity, metadata.Permission, revision,
                new(ApplicationDeploymentId.Parse(metadata.Deployment.ToString()), DeploymentGeneration.Create(metadata.Generation),
                    ManifestLocalId.Create(metadata.Component), ManifestLocalId.Create(metadata.Document)), metadata.Application, metadata.Plan,
                metadata.ManifestRevision, metadata.Fingerprint, metadata.InputFingerprint, metadata.Checkpoint);
        }
        catch (DbException) { throw new ConfigurationDeliveryStorageException(); }
    }

    public async ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(RuntimeConfigurationAuthority authority, CancellationToken token)
    {
        authority.Consume();
        try
        {
            token.ThrowIfCancellationRequested();
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var id = Guid.Parse(authority.Revision.ToString());
            var documentQuery = db.ConfigurationDocuments.AsNoTracking().Where(x => x.Id == id);
            if (await documentQuery.AnyAsync(x => x.Payload.Length > ConfigurationDocumentCodec.MaxBytes, token))
                throw new ConfigurationDocumentLimitException();
            var row = await documentQuery.SingleOrDefaultAsync(token);
            if (row is null) return null;
            var revision = ConfigurationDocumentCodec.Decode(row.Payload, row.Fingerprint);
            var metadata = revision.Metadata;
            var deployment = Guid.Parse(authority.Target.Deployment.ToString());
            if (metadata.Id != authority.Revision || metadata.Fingerprint != authority.Fingerprint || metadata.Target != authority.Target ||
                row.DeploymentId != deployment || row.Generation != authority.Target.Generation.Value || row.Component != authority.Target.Component.Value ||
                row.Document != authority.Target.Document.Value || metadata.Plan.ToString() != authority.Plan.ToString() ||
                metadata.Revision.ApplicationRecordId.ToString() != authority.Application.ToString() ||
                metadata.Revision.RevisionNumber.Value != authority.ManifestRevision) throw new InvalidDataException();
            var sources = db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == row.Generation);
            if (await sources.AnyAsync(x => x.ConfigurationJson.Length > ConfigurationDocumentCodec.MaxBytes ||
                x.GraphJson.Length > ConfigurationDocumentCodec.MaxBytes || x.ProviderInputsJson.Length > ConfigurationDocumentCodec.MaxBytes, token))
                throw new ConfigurationDocumentLimitException();
            var source = await sources.SingleOrDefaultAsync(token) ?? throw new InvalidDataException();
            if (source.EnvironmentRecordId != authority.Identity.Environment || source.ApplicationRecordId != authority.Application ||
                source.PlanId != authority.Plan || source.RevisionNumber != authority.ManifestRevision || source.Fingerprint != authority.InputFingerprint ||
                source.EnvironmentKey != metadata.Environment.Value || source.ConfigurationJson != revision.FrozenInputs ||
                PostgreSqlConfigurationDocumentStore.InputFingerprint(source) != metadata.FrozenInputFingerprint) throw new InvalidDataException();
            var frozen = ConfigurationPlanningSnapshotCodec.Decode(source.ConfigurationJson, metadata.Revision);
            if (frozen.Manifest.ApplicationId != metadata.Application) throw new InvalidDataException();
            var recipient = await db.WorkloadConfigurationRecipients.AsNoTracking().Where(x => x.DeploymentId == deployment &&
                x.Generation == row.Generation && x.Component == row.Component && x.Document == row.Document && x.RevisionId == id &&
                x.CheckpointId == authority.Checkpoint && x.InputFingerprint == authority.InputFingerprint)
                .Join(db.ManagedWorkloads.AsNoTracking().Where(x => x.Id == authority.Identity.ManagedWorkload),
                    x => x.Workload, x => x.Workload, (x, managed) => new
                    {
                        x.Actor,
                        x.CreatedAt,
                        managed.ProviderId,
                        managed.EnvironmentId,
                        managed.ApplicationId,
                        managed.DeploymentId,
                        managed.Component
                    }).SingleOrDefaultAsync(token);
            if (recipient is null || recipient.Actor != metadata.Actor || recipient.CreatedAt != metadata.CreatedAt.AddTicks(-(metadata.CreatedAt.Ticks % TimeSpan.TicksPerMicrosecond)) ||
                recipient.ProviderId != authority.Identity.Provider || recipient.EnvironmentId != authority.Identity.Environment ||
                recipient.ApplicationId != authority.Application || recipient.DeploymentId != deployment || recipient.Component != row.Component)
                throw new InvalidDataException();
            token.ThrowIfCancellationRequested();
            // No lifecycle recheck here: revocation after the coherent decision permits this in-flight read.
            return new(revision, EnvironmentRecordId.Parse(authority.Identity.Environment.ToString()));
        }
        catch (DbException) { throw new ConfigurationDeliveryStorageException(); }
    }
}
