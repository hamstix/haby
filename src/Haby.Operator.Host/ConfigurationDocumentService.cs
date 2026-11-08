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
                ? materialize ? await configurationDocuments.MaterializeAsync(exact, Actor, token) : await configurationDocuments.ReadAsync(exact, token)
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
                reply.Artifact = DocumentMetadata(revision.Metadata);
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
    private static Haby.Operator.Contracts.ConfigurationDocumentMetadata DocumentMetadata(Haby.Application.ConfigurationDocumentMetadata m) => new()
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

    public override async Task<ConfigurationApplicationReply> MaterializeApplicationConfiguration(ConfigurationApplicationRequest request, ServerCallContext context)
    {
        ConfigurationApplicationSelection selection;
        try
        {
            selection = new(EnvironmentId.Create(request.Environment), ApplicationId.Create(request.ApplicationId),
                request.HasGeneration ? DeploymentGeneration.Create(request.Generation) : null);
        }
        catch (ArgumentException) { return new() { Selector = request.Clone(), Status = Haby.Operator.Contracts.ConfigurationApplicationStatus.InvalidRequest }; }
        var result = await applicationConfiguration.ExecuteAsync(selection, Actor, context.CancellationToken);
        var reply = new ConfigurationApplicationReply
        {
            Selector = request.Clone(),
            Status = result.Status switch
            {
                Haby.Application.ConfigurationApplicationStatus.Succeeded => Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded,
                Haby.Application.ConfigurationApplicationStatus.Failed => Haby.Operator.Contracts.ConfigurationApplicationStatus.Failed,
                Haby.Application.ConfigurationApplicationStatus.InvalidRequest => Haby.Operator.Contracts.ConfigurationApplicationStatus.InvalidRequest,
                Haby.Application.ConfigurationApplicationStatus.NotFound => Haby.Operator.Contracts.ConfigurationApplicationStatus.NotFound,
                Haby.Application.ConfigurationApplicationStatus.NotApplied => Haby.Operator.Contracts.ConfigurationApplicationStatus.NotApplied,
                Haby.Application.ConfigurationApplicationStatus.InputCorrupt => Haby.Operator.Contracts.ConfigurationApplicationStatus.InputCorrupt,
                Haby.Application.ConfigurationApplicationStatus.StorageUnavailable => Haby.Operator.Contracts.ConfigurationApplicationStatus.StorageUnavailable,
                _ => Haby.Operator.Contracts.ConfigurationApplicationStatus.LimitExceeded
            },
            Target = result.Target is { } t ? new() { DeploymentId = t.Deployment.ToString(), Generation = t.Generation.Value } : null,
            SelectedCount = result.Items.Count
        };
        foreach (var item in result.Items)
            reply.Items.Add(new Haby.Operator.Contracts.ConfigurationApplicationItem { Target = DocumentIdentity(item.Target), Status = ItemStatus(item.Status), Artifact = item.Artifact is { } m ? DocumentMetadata(m) : null });
        if (reply.CalculateSize() > ConfigurationApplicationLimits.MaxResponseBytes)
            throw new RpcException(new(Grpc.Core.StatusCode.Internal, "Configuration response unavailable; accepted documents may exist. Inspect the exact generation."));
        return reply;
    }

    private static Status ItemStatus(Haby.Application.ConfigurationDocumentStatus status) => status switch
    {
        Haby.Application.ConfigurationDocumentStatus.Created => Status.Created,
        Haby.Application.ConfigurationDocumentStatus.Existing => Status.Existing,
        Haby.Application.ConfigurationDocumentStatus.NotFound => Status.NotFound,
        Haby.Application.ConfigurationDocumentStatus.InvalidRequest => Status.InvalidRequest,
        Haby.Application.ConfigurationDocumentStatus.NotApplied => Status.NotApplied,
        Haby.Application.ConfigurationDocumentStatus.InputUnavailable => Status.InputUnavailable,
        Haby.Application.ConfigurationDocumentStatus.IntegrityConflict => Status.IntegrityConflict,
        Haby.Application.ConfigurationDocumentStatus.LimitExceeded => Status.LimitExceeded,
        Haby.Application.ConfigurationDocumentStatus.StorageUnavailable => Status.StorageUnavailable,
        Haby.Application.ConfigurationDocumentStatus.InputCorrupt => Status.InputCorrupt,
        _ => Status.NotAttempted
    };

    private static ConfigurationDocumentIdentity DocumentIdentity(ConfigurationDocumentTarget target) => new()
    { DeploymentId = target.Deployment.ToString(), Generation = target.Generation.Value, ComponentId = target.Component.Value, DocumentId = target.Document.Value };
}
