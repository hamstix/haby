using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    private static async Task<T?> SharingSingleAsync<T>(IQueryable<T> query, bool asynchronous, CancellationToken token) where T : class =>
        asynchronous ? await query.SingleOrDefaultAsync(token) : query.SingleOrDefault();

    private bool CompatibleSharing(ReferenceSharingParticipant own, ReferenceSharingParticipant peer)
    {
        try
        {
            return own.Authorization.Contract == peer.Authorization.Contract &&
                identityContracts.SingleOrDefault(x => x.Producer == own.Accepted.Node.Action) is IResourceReferenceSharingContract left &&
                identityContracts.SingleOrDefault(x => x.Producer == peer.Accepted.Node.Action) is IResourceReferenceSharingContract right &&
                left.AreCompatible(own, peer) && right.AreCompatible(peer, own);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) { return false; }
    }

    // Called under the same persisted Provider-row lock as every reservation/claim writer.
    // A decision on one peer never supplies missing authority, evidence or attempts for another.
    private async Task<ReferenceSharingParticipant?> ReadSharingParticipantAsync(ResourceIdentityClaimRow claim, bool pending,
        bool asynchronous, CancellationToken token)
    {
        if (claim.SharingJson is null) return null;
        try
        {
            var authorization = ReferenceSharingAuthorizationCodec.Decode(claim.SharingJson);
            var association = await SharingSingleAsync(ResourceManagement.AsNoTracking().Where(x => x.Id == claim.Association &&
                x.Environment == claim.Environment && x.Application == claim.Application && x.Resource == claim.Resource &&
                x.Origin == ResourceOrigin.Referenced && x.Authority == ResourceLifecycleAuthority.ConsumptionOnly), asynchronous, token);
            if (association is null) return null;
            var source = await SharingSingleAsync(DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == association.Deployment &&
                x.Generation == association.SourceGeneration && x.EnvironmentRecordId == claim.Environment && x.ApplicationRecordId == claim.Application), asynchronous, token);
            if (source is null) return null;
            var inputs = source.Inputs();
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            graph.ValidateReferences(inputs);
            var nodes = graph.Nodes.Where(x => x.ResourceId.Value == claim.Resource).OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToArray();
            if (nodes.Length == 0 || nodes.Any(x => x.Sharing != authorization)) return null;
            var resource = ManifestLocalId.Create(claim.Resource);
            var provider = inputs.Providers.Resource(resource);
            if (provider.Instance.ToString() != claim.Provider.ToString() || provider.Environment.ToString() != claim.Environment.ToString()) return null;
            var group = graph.ObservationGroups.SingleOrDefault(x => x.Operations.Contains(nodes[0].Id));
            if (group?.Mode != ResourceAcquisitionMode.Reference || nodes.Any(x => !group.Operations.Contains(x.Id))) return null;
            var receipt = await SharingSingleAsync(ResourceObservationReceipts.AsNoTracking().Where(x => x.Deployment == source.DeploymentId &&
                x.Generation == source.Generation && x.Group == group.Id), asynchronous, token);
            if (receipt is null || receipt.Fingerprint != ResourceObservationReceiptIntegrity.Fingerprint(source.Fingerprint, receipt.Document)) return null;
            var document = ResourceObservationReceiptIntegrity.Decode(receipt.Document);
            if (document.Mode != (int)ResourceAcquisitionMode.Reference || document.Group != group.Id ||
                !document.Associations.Any(x => x.Resource == claim.Resource && x.Management == claim.Association) ||
                !document.Reservations.Any(x => x.Provider == claim.Provider && x.Resource == claim.Resource && x.Namespace == claim.Namespace && x.Value == claim.Value) ||
                !group.Operations.Select(x => x.Value).ToHashSet(StringComparer.Ordinal).SetEquals(document.Operations.Select(x => x.Operation))) return null;
            var evidence = new DurableActionPayload(document.EvidenceFormat, document.EvidenceJson);
            var identities = document.Reservations.Select(x => new ProviderResourceReservation(ProviderInstanceId.Parse(x.Provider.ToString()),
                inputs.Environment, inputs.Revision.ApplicationRecordId, ManifestLocalId.Create(x.Resource), x.Namespace, x.Value)).ToArray();
            foreach (var node in nodes)
            {
                var contract = identityContracts.SingleOrDefault(x => x.Producer == node.Action);
                var accepted = new AcceptedOperation(inputs, graph, node, inputs.Configuration!.Manifest.Spec);
                if (contract is not IResourceReferenceSharingContract sharing || !ReferenceSharingAuthorizationCodec.Supports(node, sharing) ||
                    !sharing.ValidateObservationEvidence(accepted, evidence) || !sharing.ValidateReferenceIdentities(accepted, identities)) return null;
            }
            if (!pending)
            {
                var key = new DurableOperationKey(ApplicationDeploymentId.Parse(source.DeploymentId.ToString()), DeploymentGeneration.Create(source.Generation), nodes[0].Id);
                if (await ResourceObservationReceiptIntegrity.ReadAsync(this, key, token, asynchronous) is null) return null;
            }
            else foreach (var member in document.Operations)
            {
                var node = graph.Nodes.Single(x => x.Id.Value == member.Operation);
                if (node.RequiredOutput != new ExternalOperationOutputIdentity(member.Contract, member.Key)) return null;
                var operation = await SharingSingleAsync(DurableOperations.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId &&
                    x.Generation == source.Generation && x.OperationId == member.Operation && x.CurrentAttemptId == member.Attempt && x.DispatchedAt != null), asynchronous, token);
                if (operation is null || operation.State is not (nameof(DurableOperationState.Dispatching) or nameof(DurableOperationState.Succeeded))) return null;
                var attempt = await SharingSingleAsync(DurableOperationAttempts.AsNoTracking().Where(x => x.AttemptId == member.Attempt &&
                    x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == member.Operation &&
                    x.AttemptNumber == operation.AttemptNumber && x.DispatchedAt == operation.DispatchedAt), asynchronous, token);
                if (attempt is null) return null;
            }
            return new(new(inputs, graph, nodes[0], inputs.Configuration!.Manifest.Spec), authorization,
                new(provider.Instance, inputs.Environment, inputs.Revision.ApplicationRecordId, resource, claim.Namespace, claim.Value), evidence);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) { return null; }
    }
}
