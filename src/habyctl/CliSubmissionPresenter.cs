using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliSubmissionMember(string Component, string Workload, string Documents, string Identity, string Publication, string Submission, string RuntimeAuthority, string? OperationId);
internal sealed record CliSubmissionProgress(string DeploymentId, long Generation, long DesiredGeneration, long? AppliedGeneration, string Submission, string? ResourcesReadyId, string? SubmittedId, CliSubmissionMember[] Members, int Completed, int Total);
internal sealed record CliSubmissionData(CliSubmissionProgress? Progress, string? Stage, string? Subject, string? Reason, bool RequiresInspection);
internal static class SubmissionPresentation
{
    internal static CliSubmissionProgress? Progress(WorkloadSubmissionProgress? value) => value is null ? null : new(value.DeploymentId,
        value.Generation, value.DesiredGeneration, value.HasAppliedGeneration ? value.AppliedGeneration : null, value.Submission,
        value.HasResourcesReadyId ? value.ResourcesReadyId : null, value.HasSubmittedId ? value.SubmittedId : null,
        [.. value.Members.Select(x => new CliSubmissionMember(x.Component, x.Workload, x.Documents, x.Identity, x.Publication, x.Submission, x.RuntimeAuthority, x.HasOperationId ? x.OperationId : null))], value.Completed, value.Total);
    internal static int Exit(WorkloadSubmissionReply value)
    {
        if (value.Outcome == "submitted")
        {
            if (value.Progress is not { Submission: "submitted", HasSubmittedId: true, HasResourcesReadyId: true } p ||
                p.Members.Count is 0 or > 512 || p.Total != p.Members.Count || p.Completed != p.Total || p.Members.Any(x => x.Submission != "confirmed") ||
                !Guid.TryParse(p.SubmittedId, out var submitted) || submitted == Guid.Empty ||
                !Guid.TryParse(p.ResourcesReadyId, out var resources) || resources == Guid.Empty)
                throw new InvalidDataException("Incomplete whole-set submission proof.");
            return CliApplication.Success;
        }
        return value.Outcome switch
        {
            "cancelled" or "unavailable" => CliApplication.TransportError,
            "invalidRequest" => CliApplication.UsageError,
            "stopped" or "notFound" or "unsupportedShape" => CliApplication.Rejected,
            _ => throw new InvalidDataException("Unknown submission outcome.")
        };
    }
}
internal sealed partial class CliJsonPresenter
{
    internal int PrintSubmission(WorkloadSubmissionReply reply)
    {
        var exit = SubmissionPresentation.Exit(reply);
        Write("deployment generation submit", reply.Outcome, new CliSubmissionData(SubmissionPresentation.Progress(reply.Progress),
            reply.HasStage ? reply.Stage : null, reply.HasSubject ? reply.Subject : null, reply.HasReason ? reply.Reason : null, reply.RequiresInspection),
            NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliSubmissionData);
        return exit;
    }
}
internal sealed partial class CliTextPresenter
{
    internal void PrintSubmissionProgress(CliSubmissionProgress? value)
    {
        if (value is null) return;
        _output.WriteLine($"Submission: {value.Submission}; deployment: {value.DeploymentId}; generation: {value.Generation}; AppliedGeneration: {value.AppliedGeneration?.ToString() ?? "none"}");
        foreach (var member in value.Members)
            _output.WriteLine($"Workload: {member.Component}/{member.Workload}; documents: {member.Documents}; identity: {member.Identity}; publication: {member.Publication}; submission: {member.Submission}; current runtime authority: {member.RuntimeAuthority}");
        _output.WriteLine("Submitted records historical API confirmation. Readiness and Applied completion are separate.");
    }
    internal int PrintSubmission(WorkloadSubmissionReply reply)
    {
        var exit = SubmissionPresentation.Exit(reply); _output.WriteLine(reply.Outcome);
        PrintSubmissionProgress(SubmissionPresentation.Progress(reply.Progress));
        if (reply.HasStage) _output.WriteLine($"Stopped stage: {reply.Stage}; subject: {reply.Subject}; reason: {reply.Reason}");
        if (reply.RequiresInspection) _output.WriteLine("Inspect persisted progress. A lost or canceled response does not prove rollback; no automatic retry is performed.");
        return exit;
    }
}
