using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override Task<ProfileReply> ValidateProfile(ProfilePayloadRequest request, ServerCallContext context)
        => SafeProfileOperation(() =>
        {
            var result = ValidateConfigurationProfile.Execute(request.Payload.Memory, context.CancellationToken);
            return ValueTask.FromResult(result.IsSuccess
                ? new ProfileReply { Status = ProfileStatus.Valid, Reference = ProfileReference(result.Declaration!.Reference) }
                : InvalidProfile(result));
        }, context.CancellationToken);

    public override async Task<ProfileReply> ImportProfile(ProfilePayloadRequest request, ServerCallContext context)
    {
        var result = await SafeProfileOperation(() => profileImporter.ExecuteAsync(request.Payload.Memory, Actor,
            context.CancellationToken), context.CancellationToken);
        return result switch
        {
            ImportConfigurationProfileResult.Succeeded accepted => ProfileAccepted(accepted.Revision,
                accepted.Disposition == ImportConfigurationProfileResult.ImportDisposition.Created
                    ? ProfileStatus.Created : ProfileStatus.ExistingEquivalent),
            ImportConfigurationProfileResult.InvalidDeclaration invalid => InvalidProfile(invalid.Report),
            ImportConfigurationProfileResult.RevisionContentConflict conflict => new ProfileReply
            { Status = ProfileStatus.RevisionContentConflict, Reference = ProfileReference(conflict.Reference) },
            _ => throw new InvalidOperationException("Unknown Profile outcome.")
        };
    }

    public override async Task<ProfileReply> GetProfileRevision(ProfileRevisionRequest request, ServerCallContext context)
    {
        ProfileRevisionRef reference;
        try { reference = new(ProfileId.Create(request.ProfileId), request.Revision); }
        catch (ArgumentException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid Profile revision reference.")); }
        return await SafeProfileOperation(async () =>
        {
            var result = await profileRevisions.FindAsync(reference, context.CancellationToken);
            if (result is null) return new ProfileReply { Status = ProfileStatus.NotFound, Reference = ProfileReference(reference) };
            context.CancellationToken.ThrowIfCancellationRequested();
            var reply = ProfileAccepted(result, ProfileStatus.Found);
            reply.DeclarationJson = Google.Protobuf.ByteString.CopyFrom(ConfigurationProfileDeclaration.Serialize(result.Declaration));
            context.CancellationToken.ThrowIfCancellationRequested();
            return reply;
        }, context.CancellationToken);
    }

    private static ProfileReply ProfileAccepted(AcceptedConfigurationProfile accepted, ProfileStatus status) => new()
    {
        Status = status,
        Reference = ProfileReference(accepted.Reference),
        Metadata = new Haby.Operator.Contracts.ProfileMetadata
        {
            Reference = ProfileReference(accepted.Reference),
            TechnicalActorId = accepted.AuditMetadata.ActorId,
            ImportedAtUtc = accepted.AuditMetadata.ImportedAt.ToUniversalTime().ToString("O")
        }
    };

    private static ProfileRevisionRequest ProfileReference(ProfileRevisionRef reference) => new()
    { ProfileId = reference.ProfileId.Value, Revision = reference.Revision };

    // Do not let driver/snapshot exceptions enter the gRPC exception logger or status detail.
    private static async Task<T> SafeProfileOperation<T>(Func<ValueTask<T>> action, CancellationToken cancellationToken)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new RpcException(new(StatusCode.Internal, "Profile operation failed.")); }
    }

    // Arbitrary JSON member names can themselves contain confidential text. Retain only
    // fixed contract paths; root still identifies a rejected declaration without echoing input.
    private static string SafeProfilePath(string path) => path.Length <= 1024 && path.Split('/').All(segment =>
        segment is "" or "apiVersion" or "kind" or "metadata" or "id" or "revision" or "spec" or
            "documents" or "staticContributions" or "bindingRules" or "fileName" or "format" or "template" or
            "documentRef" or "path" or "value" or "secretProjections" or "secretRef" or "name" or "version" or
            "slot" or "rendererRef" or "options" or "documentProjections" ||
        segment.Length <= 3 && segment.All(char.IsAsciiDigit)) ? path : "";

    private static ProfileReply InvalidProfile(ProfileWireResult<ConfigurationProfileRevision> report)
    {
        var diagnostics = report.Diagnostics.Select(x => (x.Code, Path: SafeProfilePath(x.Path))).Distinct()
            .OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();
        var reply = new ProfileReply
        {
            Status = ProfileStatus.InvalidDeclaration,
            ErrorCount = report.ErrorCount,
            DiagnosticsTruncated = report.ErrorCount > diagnostics.Length
        };
        reply.Diagnostics.AddRange(diagnostics.Select(x => new ProfileDiagnostic { Code = x.Code, Path = x.Path }));
        return reply;
    }
}
