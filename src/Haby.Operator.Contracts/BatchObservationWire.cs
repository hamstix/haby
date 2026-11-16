using Google.Protobuf;

namespace Haby.Operator.Contracts;

/// <summary>Complete-list bounds shared by the generated RPC consumer and Host.</summary>
public static class BatchObservationWire
{
    public const int RequestBytes = 131072;
    public const int ReplyBytes = 2097152;
    public static void Validate(GenerationBatchWaitRequest request)
    {
        if (request.CalculateSize() > RequestBytes || request.Members.Count is < 1 or > 200)
            throw new ArgumentException("Invalid batch size.");
        var seen = new HashSet<(string, long)>();
        foreach (var member in request.Members)
        {
            var canonical = BatchObservationKeys.CanonicalDeployment(member.DeploymentId);
            if (canonical != member.DeploymentId || !seen.Add((canonical, member.Generation)) ||
                member.RequestId != BatchObservationKeys.Member(request.RequestId, canonical, member.Generation))
                throw new ArgumentException("Invalid exact batch member.");
        }
        if (WorstReply(request).CalculateSize() > ReplyBytes) throw new ArgumentException("Batch summaries exceed the reply bound.");
    }
    public static GenerationBatchReadinessReply WorstReply(GenerationBatchWaitRequest request)
    {
        var value = new GenerationBatchReadinessReply { RequestId = request.RequestId, CurrentReady = true, CurrentApplied = true, Total = request.Members.Count, InvalidRequest = true, Canceled = true, Deadline = true };
        foreach (var member in request.Members)
            value.Members.Add(new GenerationBatchSummary
            {
                Member = member.Clone(),
                Outcome = BatchReadinessOutcome.BudgetExhausted,
                CurrentReady = true,
                OriginalRequestReady = true,
                Replayed = true,
                CurrentApplied = true,
                OriginalRequestApplied = true,
                RetirementCompleted = true,
                AppliedGeneration = long.MaxValue,
                CompletionReason = "current-authority-unavailable",
                RetirementId = "ffffffff-ffff-ffff-ffff-ffffffffffff",
                SubmittedId = "ffffffff-ffff-ffff-ffff-ffffffffffff",
                SavedReadyId = "ffffffff-ffff-ffff-ffff-ffffffffffff",
                Stop = BatchReadinessStop.Interrupted
            });
        return value;
    }
}
