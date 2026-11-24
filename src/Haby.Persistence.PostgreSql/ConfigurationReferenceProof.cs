using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class PostgreSqlExternalOperationOutputs
{
    // Metadata only: deliberately never invokes adapter ValidateAcceptedAsync, Provider I/O or SecretStore.
    internal async Task<Guid?> ConfirmReferenceDeliveryAsync(ManifestCatalogDbContext db,
        ConfigurationDeliveryArtifact artifact, ConfigurationSharedCredentialUse use, CancellationToken token)
    {
        var target = artifact.Revision.Metadata.Target;
        var pin = use.Pin;
        var scope = use.Scope;
        if (pin.Component != target.Component || scope.Environment != artifact.Environment || scope.Use != SharedCredentialUse.ApplicationDelivery ||
            pin.ProducerAction.Admission.Mode != ResourceAcquisitionMode.Reference) return null;
        var deploymentId = Guid.Parse(target.Deployment.ToString());
        var attempt = await db.DurableOperationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == use.Attempt &&
            x.DeploymentId == deploymentId && x.OperationId == pin.Producer.Value, token);
        if (attempt is null) return null;
        var targetSource = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeploymentId == deploymentId && x.Generation == target.Generation.Value, token);
        if (targetSource is null) return null;
        var graph = DurableOperationGraphCodec.Decode(targetSource.GraphJson);
        var reuse = graph.ReusedResources.SingleOrDefault(x => x.Resource == pin.ResourceLocalId);
        // The saved target and original attempt select this Resource's source. Peer activity
        // is irrelevant to a previously materialized consumer; its own receipt/head is checked below.
        if (reuse is null ? attempt.Generation != target.Generation.Value ||
            !graph.Nodes.Any(x => x.Id == pin.Producer && x.ResourceId == pin.ResourceLocalId) :
            attempt.Generation != reuse.SourceGeneration) return null;
        var key = new DurableOperationKey(target.Deployment, DeploymentGeneration.Create(attempt.Generation), pin.Producer);
        var accepted = await AcceptedAsync(db, key, token);
        if (accepted is null || accepted.Inputs.Environment != artifact.Environment ||
            accepted.Inputs.Revision.ApplicationRecordId != artifact.Revision.Metadata.Revision.ApplicationRecordId ||
            accepted.Node.ResourceId != pin.ResourceLocalId || accepted.Node.Action != pin.ProducerAction ||
            accepted.Provider.Instance != scope.Provider || accepted.Provider.Configuration != scope.Revision ||
            !await ConfirmAsync(db, key, use.Attempt, use.Output, token, validateAccepted: false, associationResource: pin.ResourceLocalId)) return null;
        var receipt = await ResourceObservationReceiptIntegrity.ReadAsync(db, key, token);
        if (receipt is null) return null;
        var saved = await db.ResourceObservationReceipts.AsNoTracking().SingleAsync(x => x.Id == receipt.Id, token);
        var association = ResourceObservationReceiptIntegrity.Decode(saved.Document).Associations.SingleOrDefault(x => x.Resource == pin.ResourceLocalId.Value);
        if (association is null || reuse is not null && association.Management != reuse.ManagementId) return null;
        var environment = Guid.Parse(artifact.Environment.ToString());
        var application = Guid.Parse(artifact.Revision.Metadata.Revision.ApplicationRecordId.ToString());
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        return await db.ResourceManagement.AsNoTracking().AnyAsync(x => x.Id == association.Management && x.Environment == environment &&
            x.Application == application && x.Resource == pin.ResourceLocalId.Value && x.Deployment == deployment && x.SourceGeneration == key.Generation.Value &&
            x.Origin == ResourceOrigin.Referenced && x.Authority == ResourceLifecycleAuthority.ConsumptionOnly, token) ? association.Management : null;
    }
}
