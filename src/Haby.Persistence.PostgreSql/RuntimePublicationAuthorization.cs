using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Read-only coherent metadata decision. No artifact/source bodies or application material.</summary>
internal static class RuntimePublicationAuthorization
{
    internal static async Task<RuntimeMetadataAuthority?> AuthorizeAsync(IDbContextFactory<ManifestCatalogDbContext> contexts,
        Guid bindingId, Guid revisionId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var binding = await db.RuntimeIdentityBindings.AsNoTracking().Where(x => x.Id == bindingId)
            .Select(x => new { x.Id, x.EnvironmentId, x.ManagedWorkloadId, x.ProviderId, x.Status, x.ConcurrencyToken, x.RevokedAt, x.RevokedBy })
            .SingleOrDefaultAsync(token);
        if (binding is null) return null;
        if (binding.Status != "Prepared") return null;
        if (binding.ConcurrencyToken == Guid.Empty || binding.RevokedAt is not null || binding.RevokedBy is not null)
            throw new InvalidDataException("Invalid runtime binding lifecycle.");
        var permission = await db.RuntimeConfigurationPermissions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BindingId == bindingId && x.RevisionId == revisionId, token);
        if (InternalRuntimePublication.PermissionState(permission)?.Status != "Published") return null;
        var origin = RuntimePublicationCodec.Origin(permission!.OriginPayload);
        var managed = await db.ManagedWorkloads.AsNoTracking().Where(x => x.Id == binding.ManagedWorkloadId)
            .Select(x => new { x.Id, x.EnvironmentId, x.ApplicationId, x.DeploymentId, x.Component, x.Workload, x.ProviderId }).SingleAsync(token);
        var document = await db.ConfigurationDocuments.AsNoTracking().Where(x => x.Id == revisionId)
            .Select(x => new { x.Id, x.DeploymentId, x.Generation, x.Component, x.Document, x.Fingerprint }).SingleOrDefaultAsync(token);
        var original = await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == permission.LastRequest, token);
        var receipt = InternalRuntimePublication.ReadReceipt(original);
        var source = await db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == origin.Deployment && x.Generation == origin.Generation)
            .Select(x => new { x.ApplicationRecordId, x.EnvironmentRecordId, x.PlanId, x.RevisionNumber, x.Fingerprint }).SingleOrDefaultAsync(token);
        var owner = await db.ApplicationDeployments.AsNoTracking().Where(x => x.Id == origin.Deployment)
            .Select(x => new { x.EnvironmentRecordId, x.ApplicationRecordId }).SingleOrDefaultAsync(token);
        var recipient = await db.WorkloadConfigurationRecipients.AsNoTracking().Where(x => x.DeploymentId == origin.Deployment &&
            x.Generation == origin.Generation && x.Component == origin.Component && x.Workload == origin.Workload && x.Document == origin.Document)
            .Select(x => new { x.RevisionId, x.CheckpointId, x.InputFingerprint }).SingleOrDefaultAsync(token);
        if (document is null || source is null || owner is null || recipient is null ||
            managed.Id != origin.ManagedWorkload || managed.ProviderId != binding.ProviderId || binding.EnvironmentId != origin.Environment ||
            managed.EnvironmentId != origin.Environment || managed.ApplicationId != origin.Application || managed.DeploymentId != origin.Deployment ||
            managed.Component != origin.Component || managed.Workload != origin.Workload || document.DeploymentId != origin.Deployment ||
            document.Generation != origin.Generation || document.Component != origin.Component || document.Document != origin.Document ||
            source.ApplicationRecordId != origin.Application || source.EnvironmentRecordId != origin.Environment || source.PlanId != origin.Plan ||
            source.Fingerprint != origin.Fingerprint || owner.ApplicationRecordId != origin.Application || owner.EnvironmentRecordId != origin.Environment ||
            recipient.RevisionId != revisionId || recipient.CheckpointId != origin.Checkpoint || recipient.InputFingerprint != origin.Fingerprint ||
            receipt.After != InternalRuntimePublication.PermissionState(permission) || receipt.Request.Binding != bindingId || receipt.Request.Revision != revisionId ||
            receipt.Request.Origin != origin || receipt.Outcome != RuntimePublicationOutcome.Published)
            throw new InvalidDataException("Runtime permission authority mismatch.");
        token.ThrowIfCancellationRequested();
        return new(bindingId, permission.Id, revisionId, origin.Environment, origin.Application, origin.Deployment, origin.Generation,
            origin.Component, origin.Workload, origin.Document, managed.Id, binding.ProviderId, origin.Plan, source.RevisionNumber,
            document.Fingerprint, origin.Fingerprint, origin.Checkpoint);
    }
}
