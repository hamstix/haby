using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<GenerationBatchReadinessReply> WaitGenerationBatch(GenerationBatchWaitRequest request, ServerCallContext context)
    {
        try
        {
            BatchObservationWire.Validate(request); // Full ordered worst-case summary reservation, before authority or observation.
            var members = request.Members.Select(x => new BatchObservationMember(ApplicationDeploymentId.Parse(x.DeploymentId),
                DeploymentGeneration.Create(x.Generation), x.RequestId)).ToArray();
            var s = request.Settings; var m = request.MemberSettings;
            var settings = s is null ? null : new BatchObservationSettings(s.HasConcurrency ? s.Concurrency : null,
                s.HasMaximumReads ? s.MaximumReads : null, s.HasRequestMilliseconds ? s.RequestMilliseconds : null,
                s.HasStartsPerSecond ? s.StartsPerSecond : null);
            var memberSettings = m is null ? null : new Haby.Application.GenerationObservationSettings(m.HasMaximumCalls ? m.MaximumCalls : null,
                m.HasMaximumRounds ? m.MaximumRounds : null, m.HasReadTimeoutMilliseconds ? m.ReadTimeoutMilliseconds : null,
                m.HasPollMilliseconds ? m.PollMilliseconds : null, m.HasRequestMilliseconds ? m.RequestMilliseconds : null);
            var result = await rolloutObservation.WaitBatchAsync(request.RequestId, Actor, members, settings, memberSettings,
                ObservationDeadline(context), context.CancellationToken);
            var reply = new GenerationBatchReadinessReply { RequestId = result.RequestId, CurrentReady = result.CurrentReady, CurrentApplied = result.CurrentApplied, Total = result.Members.Count, Canceled = result.Canceled, Deadline = result.Deadline };
            foreach (var member in result.Members)
            {
                var summary = new GenerationBatchSummary
                {
                    Member = new() { DeploymentId = member.Member.Deployment.ToString(), Generation = member.Member.Generation.Value, RequestId = member.Member.RequestId },
                    Outcome = BatchOutcome(member.Outcome),
                    CurrentReady = member.CurrentReady,
                    OriginalRequestReady = member.OriginalRequestReady,
                    CurrentApplied = member.CurrentApplied,
                    RetirementCompleted = member.Retirement is not null,
                    OriginalRequestApplied = member.OriginalRequestApplied,
                    CompletionReason = member.CompletionReason,
                    Replayed = member.Replayed,
                    Stop = member.Stop switch
                    {
                        null => BatchReadinessStop.Unspecified,
                        "Complete" => BatchReadinessStop.Complete,
                        "CallLimit" => BatchReadinessStop.CallLimit,
                        "RequestDeadline" => BatchReadinessStop.RequestDeadline,
                        "Canceled" => BatchReadinessStop.Canceled,
                        "Interrupted" => BatchReadinessStop.Interrupted,
                        _ => throw new InvalidDataException("Invalid batch stop.")
                    }
                };
                if (member.Submitted is { } submitted) summary.SubmittedId = submitted.ToString("D");
                if (member.Retirement is { } retirement) summary.RetirementId = retirement.ToString("D");
                if (member.AppliedGeneration is { } applied) summary.AppliedGeneration = applied;
                if (member.SavedReady is { } ready) summary.SavedReadyId = ready.ToString("D");
                reply.Members.Add(summary);
            }
            if (reply.CalculateSize() > BatchObservationWire.ReplyBytes || reply.Members.Count != request.Members.Count)
                throw new InvalidDataException("Invalid full batch summary.");
            return reply;
        }
        catch (ArgumentException) { return new() { RequestId = SafeObservationRequest(request.RequestId), InvalidRequest = true }; }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new RpcException(new Status(StatusCode.Unavailable, "Batch observation unavailable. Preserve the exact request keys.")); }
    }
    private static BatchReadinessOutcome BatchOutcome(GenerationReadinessOutcome value) => value switch
    {
        GenerationReadinessOutcome.Observed => BatchReadinessOutcome.Observed,
        GenerationReadinessOutcome.InvalidRequest => BatchReadinessOutcome.InvalidRequest,
        GenerationReadinessOutcome.NotFound => BatchReadinessOutcome.NotFound,
        GenerationReadinessOutcome.UnsupportedShape => BatchReadinessOutcome.UnsupportedShape,
        GenerationReadinessOutcome.Unknown => BatchReadinessOutcome.Unknown,
        GenerationReadinessOutcome.Conflict => BatchReadinessOutcome.Conflict,
        GenerationReadinessOutcome.Refused => BatchReadinessOutcome.Refused,
        GenerationReadinessOutcome.Canceled => BatchReadinessOutcome.Canceled,
        GenerationReadinessOutcome.Deadline => BatchReadinessOutcome.Deadline,
        GenerationReadinessOutcome.Unavailable => BatchReadinessOutcome.Unavailable,
        GenerationReadinessOutcome.NotStartedBudget => BatchReadinessOutcome.NotStartedBudget,
        GenerationReadinessOutcome.Interrupted => BatchReadinessOutcome.Interrupted,
        GenerationReadinessOutcome.BudgetExhausted => BatchReadinessOutcome.BudgetExhausted,
        _ => throw new InvalidDataException("Invalid batch outcome.")
    };
}
