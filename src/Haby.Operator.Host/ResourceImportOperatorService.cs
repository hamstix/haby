using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<ResourceImportRequestReply> CreateResourceImportChangeSet(CreateResourceImportChangeSetRequest request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(request.RequestId, out var id) || id == Guid.Empty)
            return new() { Status = ResourceImportRequestStatus.InvalidInput };
        ResourceImportPreparationRequest preparation;
        OperatorImportCorrectionTarget? correction;
        try
        {
            if (request.Reference is null || request.InputJson.Length > 65536 || request.CredentialMaterial.Length is < 1 or > 65536)
                return ImportReply(id, new(OperatorImportRequestStatus.InvalidInput));
            preparation = new(id, EnvironmentRecordId.Parse(request.EnvironmentRecordId),
                ManifestRevisionRef.Create(ApplicationRecordId.Parse(request.Reference.ApplicationRecordId), ManifestRevisionNumber.Create(request.Reference.RevisionNumber)),
                new(request.InputFormat, new UTF8Encoding(false, true).GetString(request.InputJson.Span)));
            correction = ReadCorrection(request.Correction, id);
        }
        catch (Exception error) when (error is ArgumentException or DecoderFallbackException)
        { return ImportReply(id, new(OperatorImportRequestStatus.InvalidInput)); }
        // Authorization is enforced by the shared operator-admin endpoint policy before this handler.
        // Mutable copies are cleared; protobuf/managed immutable copies cannot be promised zeroization.
        var bytes = request.CredentialMaterial.ToByteArray();
        request.CredentialMaterial = Google.Protobuf.ByteString.Empty;
        try
        {
            using var material = new SecretMaterial(bytes);
            return ImportReply(id, await importCreation.ExecuteAsync(preparation, correction, material, Actor, context.CancellationToken));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public override async Task<ResourceImportRequestReply> GetResourceImportRequest(ResourceImportRequestKey request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(request.RequestId, out var id) || id == Guid.Empty)
            return new() { Status = ResourceImportRequestStatus.InvalidInput };
        return ImportReply(id, await importRequests.GetAsync(id, context.CancellationToken));
    }

    private static OperatorImportCorrectionTarget? ReadCorrection(ResourceImportCorrection? correction, Guid request)
    {
        if (correction is null) return null;
        if (!Guid.TryParse(correction.PreviousRequestId, out var previous)) throw new ArgumentException("Invalid previous import request.");
        var reason = correction.Reason switch
        {
            ResourceImportCorrectionReason.CredentialRejected => OperatorImportCorrectionReason.CredentialRejected,
            ResourceImportCorrectionReason.InputCorrection => OperatorImportCorrectionReason.InputCorrection,
            ResourceImportCorrectionReason.ExplicitRetry => OperatorImportCorrectionReason.ExplicitRetry,
            _ => throw new ArgumentException("Invalid import correction reason.")
        };
        var target = correction.PreconditionCase switch
        {
            ResourceImportCorrection.PreconditionOneofCase.ExpectedAbsence when correction.ExpectedAbsence => new OperatorImportCorrectionTarget(previous, null, null, reason),
            ResourceImportCorrection.PreconditionOneofCase.Committed when correction.Committed is { } committed => new(previous,
                ApplicationDeploymentId.Parse(committed.DeploymentId), DeploymentGeneration.Create(committed.Generation), reason),
            _ => throw new ArgumentException("Explicit import correction precondition is required.")
        };
        target.Validate(request);
        return target;
    }

    private static ResourceImportCorrection CorrectionSummary(OperatorImportCorrectionTarget target)
    {
        var reply = new ResourceImportCorrection
        {
            PreviousRequestId = target.PreviousRequestId.ToString(),
            Reason = target.Reason switch
            {
                OperatorImportCorrectionReason.CredentialRejected => ResourceImportCorrectionReason.CredentialRejected,
                OperatorImportCorrectionReason.InputCorrection => ResourceImportCorrectionReason.InputCorrection,
                OperatorImportCorrectionReason.ExplicitRetry => ResourceImportCorrectionReason.ExplicitRetry,
                _ => throw new InvalidDataException("Unknown import correction reason.")
            }
        };
        if (target.Deployment is null) reply.ExpectedAbsence = true;
        else reply.Committed = new() { DeploymentId = target.Deployment.ToString(), Generation = target.Generation!.Value };
        return reply;
    }

    private static ResourceImportRequestReply ImportReply(Guid requestId, OperatorImportRequestResult result)
    {
        var reply = new ResourceImportRequestReply
        {
            RequestId = requestId.ToString(),
            Status = result.Status switch
            {
                OperatorImportRequestStatus.Pending => ResourceImportRequestStatus.Pending,
                OperatorImportRequestStatus.Created => ResourceImportRequestStatus.Created,
                OperatorImportRequestStatus.Existing => ResourceImportRequestStatus.Existing,
                OperatorImportRequestStatus.NotFound => ResourceImportRequestStatus.NotFound,
                OperatorImportRequestStatus.InvalidInput => ResourceImportRequestStatus.InvalidInput,
                OperatorImportRequestStatus.EnvironmentNotFound => ResourceImportRequestStatus.EnvironmentNotFound,
                OperatorImportRequestStatus.RevisionNotFound => ResourceImportRequestStatus.RevisionNotFound,
                OperatorImportRequestStatus.ImportNotSupported => ResourceImportRequestStatus.ImportNotSupported,
                OperatorImportRequestStatus.Conflict => ResourceImportRequestStatus.Conflict,
                OperatorImportRequestStatus.StorageUnavailable => ResourceImportRequestStatus.StorageUnavailable,
                OperatorImportRequestStatus.Superseded => ResourceImportRequestStatus.Superseded,
                OperatorImportRequestStatus.ProviderUnavailable => ResourceImportRequestStatus.ProviderUnavailable,
                OperatorImportRequestStatus.SecretUnavailable => ResourceImportRequestStatus.SecretUnavailable,
                OperatorImportRequestStatus.Connectivity => ResourceImportRequestStatus.Connectivity,
                _ => throw new InvalidOperationException("Unknown import request outcome.")
            }
        };
        if (result.Request is not { } view) return reply;
        reply.Request = new()
        {
            RequestId = view.RequestId.ToString(),
            EnvironmentRecordId = view.Environment.ToString(),
            Reference = new() { ApplicationRecordId = view.Revision.ApplicationRecordId.ToString(), RevisionNumber = view.Revision.RevisionNumber.Value },
            CredentialResourceId = view.CredentialResource.Value,
            CreatedAtUtc = view.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            CreatedBy = view.CreatedBy
        };
        if (view.ChangeSetId is { } change) reply.Request.ChangeSetId = change.ToString();
        if (view.CompletedAt is { } completed) reply.Request.CompletedAtUtc = completed.ToString("O", CultureInfo.InvariantCulture);
        if (view.CompletedBy is { } actor) reply.Request.CompletedBy = actor;
        if (view.Correction is { } correction) reply.Request.Correction = CorrectionSummary(correction);
        if (view.SupersededByRequestId is { } successor) reply.Request.SupersededByRequestId = successor.ToString();
        return reply;
    }
}
