using Google.Protobuf;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliReadinessMember(string Subject, string? AttemptId, string? DeadlineUtc, string Budget, string Outcome, string Code, bool Temporary);
internal sealed record CliReadinessData(string RequestId, bool CurrentReady, bool OriginalRequestReady, bool Replayed,
    string? SubmittedId, string? SavedReadyId, string? Stop, CliReadinessMember[] Members, int Total);
internal static class ObservationPresentation
{
    internal static int Exit(bool resume, GenerationReadinessReply value)
    {
        if (value.CalculateSize() > 2097152 || value.Members.Count > 512 || value.Total != value.Members.Count ||
            value.Members.Select(x => x.Subject).Distinct(StringComparer.Ordinal).Count() != value.Members.Count ||
            value.Members.Any(x => x.Budget is not ("Active" or "Expired") || x.Outcome is not ("Ready" or "Pending" or "Conflict" or "Unavailable" or "Refused" or "NotSubmitted" or "NotObserved") ||
                x.Code.Length is < 1 or > 128 || x.Code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.')) || x.Temporary && x.Outcome != "Unavailable") ||
            value.Replayed && value.CurrentReady || value.CurrentReady && (!value.OriginalRequestReady || resume || value.Members.Count == 0 ||
                !value.HasSubmittedId || !value.HasSavedReadyId || value.Members.Any(x => x.Outcome != "Ready" || !x.HasAttemptId || !x.HasDeadlineUtc)))
            throw new InvalidDataException("Invalid whole-set readiness result.");
        return value.Outcome switch
        {
            "resumed" when resume => CliApplication.Success,
            "observed" when !resume && value.CurrentReady => CliApplication.Success,
            "observed" when !resume && value.Stop is "Canceled" or "RequestDeadline" => CliApplication.TransportError,
            "observed" when !resume && value.Members.Any(x => x.Outcome == "Unavailable") => CliApplication.TransportError,
            "observed" when !resume => CliApplication.Rejected,
            "invalidRequest" => CliApplication.UsageError,
            "canceled" or "deadline" or "unavailable" => CliApplication.TransportError,
            "notFound" or "unsupportedShape" or "unknown" or "conflict" or "refused" => CliApplication.Rejected,
            _ => throw new InvalidDataException("Unknown readiness result.")
        };
    }
    internal static CliReadinessData Data(GenerationReadinessReply value) => new(value.RequestId, value.CurrentReady, value.OriginalRequestReady,
        value.Replayed, value.HasSubmittedId ? value.SubmittedId : null, value.HasSavedReadyId ? value.SavedReadyId : null,
        value.HasStop ? value.Stop : null, value.Members.Select(x => new CliReadinessMember(x.Subject, x.HasAttemptId ? x.AttemptId : null,
            x.HasDeadlineUtc ? x.DeadlineUtc : null, x.Budget, x.Outcome, x.Code, x.Temporary)).ToArray(), value.Total);
}
internal sealed partial class CliJsonPresenter
{
    internal int PrintObservation(bool resume, GenerationReadinessReply reply)
    {
        var exit = ObservationPresentation.Exit(resume, reply);
        Write("deployment generation " + (resume ? "resume" : "wait"), reply.Outcome, ObservationPresentation.Data(reply),
            NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliReadinessData);
        return exit;
    }
}
internal sealed partial class CliTextPresenter
{
    internal int PrintObservation(bool resume, GenerationReadinessReply reply)
    {
        var exit = ObservationPresentation.Exit(resume, reply);
        _output.WriteLine($"{reply.Outcome}; RequestId: {reply.RequestId}; replayed: {reply.Replayed}; current Ready: {reply.CurrentReady}; original request Ready: {reply.OriginalRequestReady}; stop: {reply.Stop}");
        _output.WriteLine($"Historical Submitted: {reply.SubmittedId}; historical SavedReady: {reply.SavedReadyId}");
        foreach (var member in reply.Members)
            _output.WriteLine($"{member.Subject}; attempt: {member.AttemptId}; deadline: {member.DeadlineUtc}; budget: {member.Budget}; {member.Outcome}; {member.Code}; temporary: {member.Temporary}");
        if (resume && reply.Outcome == "resumed") _output.WriteLine(reply.Replayed ? "Historical renewal replayed; no attempt renewed again." : "Expired observation renewed. Use a separate wait to observe readiness.");
        _output.WriteLine("Readiness observation only. Historical/replayed Ready is not current readiness. No lifecycle or Applied completion is claimed.");
        return exit;
    }
}
