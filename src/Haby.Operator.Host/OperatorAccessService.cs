using System.Globalization;
using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<OperatorAccessReply> CreateOperatorAccess(CreateOperatorAccessRequest request, ServerCallContext context)
    {
        try
        {
            DateTimeOffset? expiry = null;
            if (request.HasExpiresAtUtc)
            {
                if (!DateTimeOffset.TryParseExact(request.ExpiresAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero)
                    throw new ArgumentException();
                expiry = parsed;
            }
            var created = await operatorAccess.CreateAsync(request.HasDisplayName ? request.DisplayName : null, Actor, expiry, context.CancellationToken);
            return new() { Grant = OperatorMetadata(created.Grant), Credential = created.Credential };
        }
        catch (ArgumentException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid operator access request.")); }
        catch (OperationCanceledException) { throw new RpcException(new(StatusCode.Cancelled, "Operator access canceled.")); }
        catch (Exception) { throw new RpcException(new(StatusCode.Unavailable, "Operator access storage unavailable; issuance outcome may be uncertain.")); }
    }
    public override Task<OperatorAccessReply> GetOperatorAccess(OperatorAccessRequest request, ServerCallContext context) => OperatorAccess(request, false, context);
    public override Task<OperatorAccessReply> RevokeOperatorAccess(OperatorAccessRequest request, ServerCallContext context) => OperatorAccess(request, true, context);
    private async Task<OperatorAccessReply> OperatorAccess(OperatorAccessRequest request, bool revoke, ServerCallContext context)
    {
        if (!Guid.TryParseExact(request.GrantId, "D", out var id) || id == Guid.Empty) throw new RpcException(new(StatusCode.InvalidArgument, "Invalid grant identity."));
        try
        {
            var grant = revoke ? await operatorAccess.RevokeAsync(id, Actor, context.CancellationToken) : await operatorAccess.GetAsync(id, context.CancellationToken);
            return grant is null ? new() : new() { Grant = OperatorMetadata(grant) };
        }
        catch (OperationCanceledException) { throw new RpcException(new(StatusCode.Cancelled, "Operator access canceled.")); }
        catch (Exception) { throw new RpcException(new(StatusCode.Unavailable, "Operator access storage unavailable.")); }
    }
    public override async Task<ListOperatorAccessReply> ListOperatorAccess(ListOperatorAccessRequest request, ServerCallContext context)
    {
        Guid? after = null;
        if (request.HasAfterGrantId)
        {
            if (!Guid.TryParseExact(request.AfterGrantId, "D", out var id) || id == Guid.Empty) throw new RpcException(new(StatusCode.InvalidArgument, "Invalid cursor."));
            after = id;
        }
        var size = request.PageSize == 0 ? 50 : request.PageSize;
        if (size is < 1 or > 100) throw new RpcException(new(StatusCode.InvalidArgument, "Invalid page size."));
        try
        {
            var grants = await operatorAccess.ListAsync(after, size, context.CancellationToken);
            var reply = new ListOperatorAccessReply(); reply.Grants.AddRange(grants.Select(OperatorMetadata));
            if (grants.Count == size) reply.NextAfterGrantId = grants[^1].Id.ToString();
            return reply;
        }
        catch (OperationCanceledException) { throw new RpcException(new(StatusCode.Cancelled, "Operator access canceled.")); }
        catch (Exception) { throw new RpcException(new(StatusCode.Unavailable, "Operator access storage unavailable.")); }
    }
    internal static OperatorAccessMetadata OperatorMetadata(OperatorAccessGrant grant)
    {
        var result = new OperatorAccessMetadata { GrantId = grant.Id.ToString(), Actor = grant.Actor, CreatedAtUtc = grant.CreatedAt.ToString("O", CultureInfo.InvariantCulture) };
        if (grant.DisplayName is { } name) result.DisplayName = name;
        if (grant.ExpiresAt is { } expiry) result.ExpiresAtUtc = expiry.ToString("O", CultureInfo.InvariantCulture);
        if (grant.RevokedAt is { } revoked) { result.RevokedAtUtc = revoked.ToString("O", CultureInfo.InvariantCulture); result.RevokedBy = grant.RevokedBy!; }
        return result;
    }
}
