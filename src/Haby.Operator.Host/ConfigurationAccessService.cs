using System.Globalization;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using AccessStatus = Haby.Operator.Contracts.ConfigurationAccessStatus;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<ConfigurationAccessReply> CreateConfigurationAccess(CreateConfigurationAccessRequest request, ServerCallContext context)
    {
        try
        {
            var r = request.Selector ?? throw new ArgumentException();
            var selection = new ConfigurationDocumentSelection(EnvironmentId.Create(r.Environment), Haby.Domain.ApplicationId.Create(r.ApplicationId),
                ManifestLocalId.Create(r.ComponentId), ManifestLocalId.Create(r.DocumentId), r.HasGeneration ? DeploymentGeneration.Create(r.Generation) : null);
            DateTimeOffset? expiry = null;
            if (request.HasExpiresAtUtc)
            {
                if (!DateTimeOffset.TryParseExact(request.ExpiresAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero)
                    throw new ArgumentException();
                expiry = parsed;
            }
            var created = await configurationAccess.CreateAsync(selection, OperatorHostApplication.TechnicalActorId, expiry, context.CancellationToken);
            return created is null ? new() { Status = AccessStatus.NotFound } : new() { Status = AccessStatus.Created, Grant = AccessMetadata(created.Grant), Credential = created.Credential };
        }
        catch (ArgumentException) { return new() { Status = AccessStatus.InvalidRequest }; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new() { Status = AccessStatus.StorageUnavailable }; }
    }
    public override Task<ConfigurationAccessReply> GetConfigurationAccess(ConfigurationAccessRequest request, ServerCallContext context) => Access(request, false, context.CancellationToken);
    public override Task<ConfigurationAccessReply> RevokeConfigurationAccess(ConfigurationAccessRequest request, ServerCallContext context) => Access(request, true, context.CancellationToken);
    private async Task<ConfigurationAccessReply> Access(ConfigurationAccessRequest request, bool revoke, CancellationToken token)
    {
        if (!Guid.TryParseExact(request.GrantId, "D", out var id) || id == Guid.Empty) return new() { Status = AccessStatus.InvalidRequest };
        try
        {
            var grant = revoke ? await configurationAccess.RevokeAsync(id, OperatorHostApplication.TechnicalActorId, token) : await configurationAccess.GetAsync(id, token);
            return grant is null ? new() { Status = AccessStatus.NotFound } : new() { Status = revoke ? AccessStatus.Revoked : AccessStatus.Found, Grant = AccessMetadata(grant) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new() { Status = AccessStatus.StorageUnavailable }; }
    }
    internal static ConfigurationAccessMetadata AccessMetadata(ConfigurationAccessGrant grant)
    {
        var m = grant.Document;
        var result = new ConfigurationAccessMetadata
        {
            GrantId = grant.Id.ToString(),
            Actor = grant.Actor,
            CreatedAtUtc = grant.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            Document = new()
            {
                RevisionId = m.Id.ToString(),
                Target = DocumentIdentity(m.Target),
                Environment = m.Environment.Value,
                ApplicationId = m.Application.Value,
                SourceRevision = Reference(m.Revision),
                PlanId = m.Plan.ToString(),
                FileName = m.FileName,
                Format = m.Format,
                Serializer = m.Serializer,
                Fingerprint = m.Fingerprint,
                FrozenInputFingerprint = m.FrozenInputFingerprint,
                TechnicalActorId = m.Actor,
                CreatedAtUtc = m.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
            }
        };
        if (grant.ExpiresAt is { } expiry) result.ExpiresAtUtc = expiry.ToString("O", CultureInfo.InvariantCulture);
        if (grant.RevokedAt is { } revoked) { result.RevokedAtUtc = revoked.ToString("O", CultureInfo.InvariantCulture); result.RevokedBy = grant.RevokedBy!; }
        return result;
    }
}
