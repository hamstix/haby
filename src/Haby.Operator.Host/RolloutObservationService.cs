using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<GenerationReadinessReply> WaitGeneration(GenerationWaitRequest request, ServerCallContext context)
    {
        if (request.CalculateSize() > 131072) return InvalidObservation(request.RequestId);
        try
        {
            var deployment = ApplicationDeploymentId.Parse(request.DeploymentId);
            var generation = DeploymentGeneration.Create(request.Generation);
            _ = OperationId.Create(request.RequestId);
            var settings = request.Settings;
            var narrowed = settings is null ? null : new Haby.Application.GenerationObservationSettings(
                settings.HasMaximumCalls ? settings.MaximumCalls : null, settings.HasMaximumRounds ? settings.MaximumRounds : null,
                settings.HasReadTimeoutMilliseconds ? settings.ReadTimeoutMilliseconds : null,
                settings.HasPollMilliseconds ? settings.PollMilliseconds : null, settings.HasRequestMilliseconds ? settings.RequestMilliseconds : null);
            var result = await OperationCall(() => rolloutObservation.WaitAsync(deployment,
                generation, request.RequestId, Actor, narrowed, ObservationDeadline(context), context.CancellationToken), context);
            return ObservationReply(result);
        }
        catch (ArgumentException) { return InvalidObservation(request.RequestId); }
    }
    public override async Task<GenerationReadinessReply> ResumeGeneration(GenerationResumeRequest request, ServerCallContext context)
    {
        if (request.CalculateSize() > 131072 || request.Members.Count is < 1 or > 512) return InvalidObservation(request.RequestId);
        try
        {
            var deployment = ApplicationDeploymentId.Parse(request.DeploymentId);
            var generation = DeploymentGeneration.Create(request.Generation);
            _ = OperationId.Create(request.RequestId);
            var members = request.Members.Select(x => new GenerationObservationMember(x.Subject, Guid.Parse(x.ExpectedAttemptId))).ToArray();
            var result = await OperationCall(() => rolloutObservation.ResumeAsync(deployment,
                generation, request.RequestId, Actor, members, ObservationDeadline(context), context.CancellationToken), context);
            return ObservationReply(result);
        }
        catch (Exception error) when (error is ArgumentException or FormatException) { return InvalidObservation(request.RequestId); }
    }
    private static DateTimeOffset? ObservationDeadline(ServerCallContext context) =>
        context.Deadline == DateTime.MaxValue ? null : new DateTimeOffset(context.Deadline.ToUniversalTime());
    private static string SafeObservationRequest(string request)
    {
        try { return OperationId.Create(request).Value; }
        catch (ArgumentException) { return ""; }
    }
    private static GenerationReadinessReply InvalidObservation(string request) =>
        new() { Outcome = "invalidRequest", RequestId = SafeObservationRequest(request), CompletionReason = "not-ready" };
    private static GenerationReadinessReply ObservationReply(GenerationReadinessResult value)
    {
        var reply = new GenerationReadinessReply
        {
            Outcome = value.Outcome switch
            {
                GenerationReadinessOutcome.Observed => "observed",
                GenerationReadinessOutcome.Resumed => "resumed",
                GenerationReadinessOutcome.InvalidRequest => "invalidRequest",
                GenerationReadinessOutcome.NotFound => "notFound",
                GenerationReadinessOutcome.UnsupportedShape => "unsupportedShape",
                GenerationReadinessOutcome.Unknown => "unknown",
                GenerationReadinessOutcome.Conflict => "conflict",
                GenerationReadinessOutcome.Refused => "refused",
                GenerationReadinessOutcome.Canceled => "canceled",
                GenerationReadinessOutcome.Deadline => "deadline",
                GenerationReadinessOutcome.Unavailable => "unavailable",
                _ => throw new InvalidDataException("Unknown readiness outcome.")
            },
            RequestId = SafeObservationRequest(value.RequestId),
            CurrentReady = value.CurrentReady,
            OriginalRequestReady = value.OriginalRequestReady,
            Replayed = value.Replayed,
            CurrentApplied = value.CurrentApplied,
            RetirementCompleted = value.Retirement is not null,
            OriginalRequestApplied = value.OriginalRequestApplied,
            CompletionReason = value.CompletionReason
        };
        if (value.Retirement is { } retirement) reply.RetirementId = retirement.ToString("D");
        if (value.AppliedGeneration is { } applied) reply.AppliedGeneration = applied;
        if (value.Submitted is { } submitted) reply.SubmittedId = submitted.ToString("D");
        if (value.SavedReady is { } saved) reply.SavedReadyId = saved.ToString("D");
        if (value.Stop is { } stop) reply.Stop = stop;
        foreach (var member in value.Members ?? [])
        {
            var item = new Haby.Operator.Contracts.GenerationReadinessMember
            { Subject = member.Subject, Budget = member.Budget, Outcome = member.Outcome, Code = member.Code, Temporary = member.Temporary };
            if (member.Attempt is { } attempt) item.AttemptId = attempt.ToString("D");
            if (member.Deadline is { } deadline) item.DeadlineUtc = Utc(deadline);
            reply.Members.Add(item);
        }
        reply.Total = reply.Members.Count;
        if (reply.Members.Count > 512 || reply.CalculateSize() > 2097152)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Readiness response exceeds its bound."));
        return reply;
    }
}
