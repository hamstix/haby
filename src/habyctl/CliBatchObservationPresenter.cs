using Google.Protobuf;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliBatchSummary(string DeploymentId, long Generation, string RequestId, string Outcome,
    bool CurrentReady, bool OriginalRequestReady, bool Replayed, string? SubmittedId, string? SavedReadyId, string Stop);
internal sealed record CliBatchData(string RequestId, bool CurrentReady, bool Canceled, bool Deadline, CliBatchSummary[] Members, int Total);
internal static class BatchObservationPresentation
{
    internal static int Exit(GenerationBatchWaitRequest request, GenerationBatchReadinessReply reply)
    {
        if (reply.CalculateSize() > BatchObservationWire.ReplyBytes || reply.RequestId != request.RequestId ||
            reply.InvalidRequest && (reply.CurrentReady || reply.Members.Count != 0 || reply.Total != 0)) throw Invalid();
        if (reply.InvalidRequest) return CliApplication.UsageError;
        if (reply.Total != request.Members.Count || reply.Members.Count != request.Members.Count) throw Invalid();
        for (var i = 0; i < reply.Members.Count; i++)
        {
            var item = reply.Members[i];
            if (item.Member is null || !item.Member.Equals(request.Members[i]) || !Enum.IsDefined(item.Outcome) || item.Outcome == BatchReadinessOutcome.Unspecified ||
                !Enum.IsDefined(item.Stop) || item.Replayed && item.CurrentReady ||
                item.HasSubmittedId && (!Guid.TryParseExact(item.SubmittedId, "D", out var submitted) || submitted == Guid.Empty) ||
                item.HasSavedReadyId && (!Guid.TryParseExact(item.SavedReadyId, "D", out var saved) || saved == Guid.Empty) ||
                item.CurrentReady && (item.Outcome != BatchReadinessOutcome.Observed || !item.OriginalRequestReady ||
                    !item.HasSubmittedId || !item.HasSavedReadyId || item.Stop != BatchReadinessStop.Complete) ||
                item.Outcome is BatchReadinessOutcome.NotStartedBudget or BatchReadinessOutcome.Refused or BatchReadinessOutcome.Conflict &&
                    (item.CurrentReady || item.OriginalRequestReady || item.Replayed || item.HasSubmittedId || item.HasSavedReadyId)) throw Invalid();
        }
        var all = reply.Members.All(x => x.CurrentReady && !x.Replayed);
        if (reply.CurrentReady != (all && !reply.Deadline && !reply.Canceled)) throw Invalid();
        if (reply.CurrentReady) return CliApplication.Success;
        if (reply.Canceled || reply.Deadline || reply.Members.Any(x => x.Outcome is BatchReadinessOutcome.Canceled or BatchReadinessOutcome.Deadline or
            BatchReadinessOutcome.Interrupted or BatchReadinessOutcome.Unavailable || x.Stop is BatchReadinessStop.Canceled or BatchReadinessStop.RequestDeadline))
            return CliApplication.TransportError;
        return reply.Members.Any(x => x.Outcome == BatchReadinessOutcome.InvalidRequest) ? CliApplication.UsageError : CliApplication.Rejected;
        static InvalidDataException Invalid() => new("Invalid complete batch readiness response.");
    }
    internal static CliBatchData Data(GenerationBatchReadinessReply reply) => new(reply.RequestId, reply.CurrentReady, reply.Canceled, reply.Deadline,
        reply.Members.Select(x => new CliBatchSummary(x.Member.DeploymentId, x.Member.Generation, x.Member.RequestId,
            x.Outcome.ToString(), x.CurrentReady, x.OriginalRequestReady, x.Replayed, x.HasSubmittedId ? x.SubmittedId : null,
            x.HasSavedReadyId ? x.SavedReadyId : null, x.Stop.ToString())).ToArray(), reply.Total);
}
internal sealed partial class CliJsonPresenter
{
    internal void PrintBatch(GenerationBatchReadinessReply reply) => Write("deployment generation wait-batch",
        reply.InvalidRequest ? "invalidRequest" : reply.CurrentReady ? "currentReady" : "incomplete", BatchObservationPresentation.Data(reply),
        NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliBatchData);
}
internal sealed partial class CliTextPresenter
{
    internal void PrintBatch(GenerationBatchReadinessReply reply)
    {
        _output.WriteLine($"Batch RequestId: {reply.RequestId}; current Ready: {reply.CurrentReady}; total: {reply.Total}; canceled: {reply.Canceled}; deadline: {reply.Deadline}");
        foreach (var member in BatchObservationPresentation.Data(reply).Members)
            _output.WriteLine($"{member.DeploymentId}/{member.Generation}; RequestId: {member.RequestId}; {member.Outcome}; current Ready: {member.CurrentReady}; original request Ready: {member.OriginalRequestReady}; replayed: {member.Replayed}; Submitted: {member.SubmittedId}; SavedReady: {member.SavedReadyId}; stop: {member.Stop}");
        _output.WriteLine("Observation only. Batch keys correlate members; identical repeats may replay interrupted history and do not guarantee resume.");
    }
}
