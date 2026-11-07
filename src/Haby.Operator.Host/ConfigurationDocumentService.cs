using ApplicationId = Haby.Domain.ApplicationId;
using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Status = Haby.Operator.Contracts.ConfigurationDocumentStatus;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override Task<ConfigurationDocumentReply> MaterializeConfiguration(ConfigurationDocumentRequest request, ServerCallContext context) =>
        ConfigurationDocument(request, true, false, context.CancellationToken);
    public override Task<ConfigurationDocumentReply> GetConfiguration(ConfigurationDocumentRequest request, ServerCallContext context) =>
        ConfigurationDocument(request, false, false, context.CancellationToken);
    public override Task<ConfigurationDocumentReply> PreviewConfiguration(ConfigurationDocumentRequest request, ServerCallContext context) =>
        ConfigurationDocument(request, false, true, context.CancellationToken);

    private async Task<ConfigurationDocumentReply> ConfigurationDocument(ConfigurationDocumentRequest request, bool materialize, bool preview, CancellationToken token)
    {
        var reply = new ConfigurationDocumentReply { Selector = request.Clone() };
        ConfigurationDocumentSelection selection;
        try
        {
            selection = new(EnvironmentId.Create(request.Environment), ApplicationId.Create(request.ApplicationId), ManifestLocalId.Create(request.ComponentId),
                ManifestLocalId.Create(request.DocumentId), request.HasGeneration ? DeploymentGeneration.Create(request.Generation) : null);
        }
        catch (ArgumentException) { reply.Status = Status.InvalidRequest; return reply; }
        try
        {
            var selected = await configurationDocuments.SelectAsync(selection, token);
            if (selected.Target is { } target) reply.Target = DocumentIdentity(target);
            var result = selected.Status == Haby.Application.ConfigurationDocumentStatus.Found && selected.Target is { } exact
                ? materialize ? await configurationDocuments.MaterializeAsync(exact, OperatorHostApplication.TechnicalActorId, token) : await configurationDocuments.ReadAsync(exact, token)
                : selected;
            reply.Status = result.Status switch
            {
                Haby.Application.ConfigurationDocumentStatus.Created => Status.Created,
                Haby.Application.ConfigurationDocumentStatus.Existing => Status.Existing,
                Haby.Application.ConfigurationDocumentStatus.Found => Status.Found,
                Haby.Application.ConfigurationDocumentStatus.NotFound => Status.NotFound,
                Haby.Application.ConfigurationDocumentStatus.NotApplied => Status.NotApplied,
                Haby.Application.ConfigurationDocumentStatus.IntegrityConflict => Status.IntegrityConflict,
                Haby.Application.ConfigurationDocumentStatus.LimitExceeded => Status.LimitExceeded,
                _ => Status.InputUnavailable
            };
            if (result.Revision is { } revision)
            {
                var m = revision.Metadata;
                reply.Artifact = new()
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
                    CreatedAtUtc = m.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                };
                if (preview) reply.PreviewJson = ByteString.CopyFromUtf8(ConfigurationJson.Preview(revision.Tree).Json);
            }
            if (reply.CalculateSize() > 8 * 1024 * 1024) throw new ConfigurationDocumentLimitException();
            return reply;
        }
        catch (ConfigurationDocumentLimitException) { reply.Status = Status.LimitExceeded; }
        catch (InvalidDataException) { reply.Status = Status.InputCorrupt; }
        catch (Exception e) when (e is NpgsqlException or DbUpdateException or IOException) { reply.Status = Status.StorageUnavailable; }
        reply.Artifact = null; reply.PreviewJson = ByteString.Empty;
        return reply;
    }
    private static ConfigurationDocumentIdentity DocumentIdentity(ConfigurationDocumentTarget target) => new()
    { DeploymentId = target.Deployment.ToString(), Generation = target.Generation.Value, ComponentId = target.Component.Value, DocumentId = target.Document.Value };
}
