using System.Globalization;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override Task<ListProfilesReply> ListProfiles(ListProfilesRequest request, ServerCallContext context)
    {
        var size = request.HasPageSize ? request.PageSize : 50;
        ProfileId? after;
        try
        {
            if (size is < 1 or > 100) throw new ArgumentException();
            after = request.HasAfterProfileId ? ProfileId.Create(request.AfterProfileId) : null;
        }
        catch (ArgumentException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid Profile page selector.")); }
        return SafeProfileOperation(async () =>
        {
            var page = await profileDiscovery.ListAsync(size, after, context.CancellationToken);
            var reply = new ListProfilesReply();
            reply.Items.AddRange(page.Items.Select(DiscoveryMetadata));
            if (page.NextAfterProfileId is { } next) reply.NextAfterProfileId = next.Value;
            return reply;
        }, context.CancellationToken);
    }

    public override Task<ListProfileRevisionsReply> ListProfileRevisions(ListProfileRevisionsRequest request, ServerCallContext context)
    {
        var size = request.HasPageSize ? request.PageSize : 50;
        ProfileId id;
        try
        {
            id = ProfileId.Create(request.ProfileId);
            if (size is < 1 or > 100 || request.HasAfterRevision && request.AfterRevision <= 0) throw new ArgumentException();
        }
        catch (ArgumentException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid Profile revision page selector.")); }
        return SafeProfileOperation(async () =>
        {
            var page = await profileDiscovery.ListRevisionsAsync(id, size, request.HasAfterRevision ? request.AfterRevision : null, context.CancellationToken);
            var reply = new ListProfileRevisionsReply { ProfileId = id.Value };
            reply.Items.AddRange(page.Items.Select(DiscoveryMetadata));
            if (page.NextAfterRevision is { } next) reply.NextAfterRevision = next;
            return reply;
        }, context.CancellationToken);
    }

    private static ProfileMetadata DiscoveryMetadata(ImportedProfileMetadata item) => new()
    {
        Reference = ProfileReference(item.Reference),
        TechnicalActorId = item.AuditMetadata.ActorId,
        ImportedAtUtc = item.AuditMetadata.ImportedAt.ToString("O", CultureInfo.InvariantCulture)
    };
}
