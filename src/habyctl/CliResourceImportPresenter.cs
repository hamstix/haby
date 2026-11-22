using System.Globalization;
using System.Text.Json.Serialization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliResourceImportCorrection(string PreviousRequestId, string Reason, bool ExpectedUncommitted, string? DeploymentId, long? Generation);
internal sealed record CliResourceImportRecord(string EnvironmentRecordId, CliReference Reference, string CredentialResourceId,
    string CreatedAtUtc, string CreatedBy, string? ChangeSetId, string? CompletedAtUtc, string? CompletedBy,
    CliResourceImportCorrection? Correction, string? SupersededByRequestId);
internal sealed record CliResourceImportRecovery(string LookupCommand, string ReplayRequestId, string Instruction);
internal sealed record CliResourceImportData(string RequestId, CliResourceImportRecord? Request, CliResourceImportRecovery? Recovery);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliEnvelope<CliResourceImportData>))]
internal partial class CliResourceImportJsonContext : JsonSerializerContext;

internal static class ResourceImportPresentation
{
    internal static string Outcome(ResourceImportRequestStatus status) => status switch
    {
        ResourceImportRequestStatus.Pending => "pending",
        ResourceImportRequestStatus.Created => "created",
        ResourceImportRequestStatus.Existing => "existing",
        ResourceImportRequestStatus.NotFound => "notFound",
        ResourceImportRequestStatus.InvalidInput => "invalidInput",
        ResourceImportRequestStatus.EnvironmentNotFound => "environmentNotFound",
        ResourceImportRequestStatus.RevisionNotFound => "revisionNotFound",
        ResourceImportRequestStatus.ImportNotSupported => "importNotSupported",
        ResourceImportRequestStatus.Conflict => "conflict",
        ResourceImportRequestStatus.StorageUnavailable => "storageUnavailable",
        ResourceImportRequestStatus.Superseded => "superseded",
        ResourceImportRequestStatus.ProviderUnavailable => "providerUnavailable",
        ResourceImportRequestStatus.SecretUnavailable => "secretUnavailable",
        ResourceImportRequestStatus.Connectivity => "connectivity",
        _ => throw new InvalidDataException("Unknown import request outcome.")
    };

    internal static CliResourceImportRecovery Recovery(string request) => new($"habyctl changeset import get {request}", request,
        "Lookup observes durable metadata, not rollback. To replay create, retain this exact request ID, Environment, Application revision, names/correction target and exact password bytes. Do not generate a new ID as a retry.");

    internal static CliResourceImportData Data(ResourceImportRequestReply reply)
    {
        _ = Outcome(reply.Status);
        if (!Uuid(reply.RequestId)) throw new InvalidDataException("Invalid import request identity.");
        if (reply.Request is null && reply.Status is ResourceImportRequestStatus.Pending or ResourceImportRequestStatus.Created or
            ResourceImportRequestStatus.Existing or ResourceImportRequestStatus.Superseded) throw new InvalidDataException("Missing import request metadata.");
        CliResourceImportRecord? record = null;
        if (reply.Request is { } view)
        {
            if (view.RequestId != reply.RequestId || !RecordId(view.EnvironmentRecordId) || view.Reference is null ||
                !RecordId(view.Reference.ApplicationRecordId) || view.Reference.RevisionNumber <= 0 || string.IsNullOrWhiteSpace(view.CredentialResourceId) ||
                view.CredentialResourceId.Length > 63 || !Timestamp(view.CreatedAtUtc) || string.IsNullOrWhiteSpace(view.CreatedBy) || view.CreatedBy.Length > 256 ||
                view.HasChangeSetId != view.HasCompletedAtUtc || view.HasChangeSetId != view.HasCompletedBy ||
                view.HasChangeSetId && (!RecordId(view.ChangeSetId) || !Timestamp(view.CompletedAtUtc) || string.IsNullOrWhiteSpace(view.CompletedBy) || view.CompletedBy.Length > 256) ||
                reply.Status is ResourceImportRequestStatus.Created or ResourceImportRequestStatus.Existing && !view.HasChangeSetId ||
                reply.Status == ResourceImportRequestStatus.Pending && view.HasChangeSetId ||
                view.HasSupersededByRequestId && (!Uuid(view.SupersededByRequestId) || view.SupersededByRequestId == reply.RequestId) ||
                reply.Status == ResourceImportRequestStatus.Superseded && !view.HasSupersededByRequestId)
                throw new InvalidDataException("Invalid import request metadata.");
            record = new(view.EnvironmentRecordId, new(view.Reference.ApplicationRecordId, view.Reference.RevisionNumber), view.CredentialResourceId,
                view.CreatedAtUtc, view.CreatedBy, view.HasChangeSetId ? view.ChangeSetId : null,
                view.HasCompletedAtUtc ? view.CompletedAtUtc : null, view.HasCompletedBy ? view.CompletedBy : null,
                view.Correction is { } correction ? Correction(correction, reply.RequestId) : null,
                view.HasSupersededByRequestId ? view.SupersededByRequestId : null);
        }
        return new(reply.RequestId, record, reply.Status is ResourceImportRequestStatus.StorageUnavailable or ResourceImportRequestStatus.Connectivity or
            ResourceImportRequestStatus.NotFound ? Recovery(reply.RequestId) : null);
    }

    private static CliResourceImportCorrection Correction(ResourceImportCorrection value, string request)
    {
        if (!Uuid(value.PreviousRequestId) || value.PreviousRequestId == request ||
            value.PreconditionCase is not (ResourceImportCorrection.PreconditionOneofCase.ExpectedAbsence or ResourceImportCorrection.PreconditionOneofCase.Committed) ||
            value.PreconditionCase == ResourceImportCorrection.PreconditionOneofCase.ExpectedAbsence && !value.ExpectedAbsence ||
            value.PreconditionCase == ResourceImportCorrection.PreconditionOneofCase.Committed &&
                (value.Committed is null || !RecordId(value.Committed.DeploymentId) || value.Committed.Generation <= 0))
            throw new InvalidDataException("Invalid import correction metadata.");
        var reason = value.Reason switch
        {
            ResourceImportCorrectionReason.CredentialRejected => "credentialRejected",
            ResourceImportCorrectionReason.InputCorrection => "inputCorrection",
            ResourceImportCorrectionReason.ExplicitRetry => "explicitRetry",
            _ => throw new InvalidDataException("Unknown import correction reason.")
        };
        return new(value.PreviousRequestId, reason, value.ExpectedAbsence, value.Committed?.DeploymentId, value.Committed?.Generation);
    }

    internal static int Exit(string command, ResourceImportRequestStatus status) => status is ResourceImportRequestStatus.Created or ResourceImportRequestStatus.Existing ||
        command == "changeset import get" && status is ResourceImportRequestStatus.Pending or ResourceImportRequestStatus.Superseded ? CliApplication.Success : CliApplication.Rejected;
    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString();
    private static bool RecordId(string value) => Uuid(value) && value[14] == '7' && value[19] is ('8' or '9' or 'a' or 'b');
    private static bool Timestamp(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintResourceImport(string command, ResourceImportRequestReply reply)
    {
        var data = ResourceImportPresentation.Data(reply);
        Write(command, ResourceImportPresentation.Outcome(reply.Status), data, NoDiagnostics, CliResourceImportJsonContext.Default.CliEnvelopeCliResourceImportData);
        return ResourceImportPresentation.Exit(command, reply.Status);
    }
    internal int PrintImportTicket(string command, Guid request, bool sent, int exit)
    {
        Write(command, sent ? "uncertain" : "notSubmitted", new CliResourceImportData(request.ToString(), null, ResourceImportPresentation.Recovery(request.ToString())),
            NoDiagnostics, CliResourceImportJsonContext.Default.CliEnvelopeCliResourceImportData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal int PrintResourceImport(string command, ResourceImportRequestReply reply)
    {
        var data = ResourceImportPresentation.Data(reply);
        _output.WriteLine($"RequestId: {data.RequestId}; {ResourceImportPresentation.Outcome(reply.Status)}");
        if (data.Request is { } view)
        {
            _output.WriteLine($"Environment: {view.EnvironmentRecordId}; Application: {view.Reference.ApplicationRecordId}; revision: {view.Reference.RevisionNumber}");
            _output.WriteLine($"Original actor: {view.CreatedBy}; created: {view.CreatedAtUtc}");
            if (view.ChangeSetId is { } change) _output.WriteLine($"ChangeSet: {change}; linked by: {view.CompletedBy}; at: {view.CompletedAtUtc}. Plan, Commit and Execute remain explicit.");
            if (view.SupersededByRequestId is { } successor) _output.WriteLine($"Superseded by request: {successor}");
        }
        if (data.Recovery is { } recovery) { _output.WriteLine(recovery.LookupCommand); _output.WriteLine(recovery.Instruction); }
        return ResourceImportPresentation.Exit(command, reply.Status);
    }
    internal int PrintImportTicket(string command, Guid request, bool sent, int exit)
    {
        _output.WriteLine($"RequestId: {request}; {(sent ? "outcome uncertain" : "not submitted")}");
        var recovery = ResourceImportPresentation.Recovery(request.ToString());
        _output.WriteLine(recovery.LookupCommand); _output.WriteLine(recovery.Instruction);
        return exit;
    }
}
