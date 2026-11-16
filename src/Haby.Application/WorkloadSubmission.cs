using Haby.Domain;

namespace Haby.Application;

public enum WorkloadSubmissionStage { Documents, Identity, Publication, Deployment }
public sealed record WorkloadSubmissionMember(string Component, string Workload, string Documents, string Identity,
    string Publication, string Submission, string RuntimeAuthority, string? OperationId = null);
public sealed record WorkloadSubmissionProgress(string Deployment, long Generation, long DesiredGeneration, long? AppliedGeneration,
    string Submission, Guid? ResourcesReady, Guid? Submitted, IReadOnlyList<WorkloadSubmissionMember> Members)
{
    public int Completed => Members.Count(x => x.Submission == "confirmed");
    public int Total => Members.Count;
}
public sealed record WorkloadSubmissionResult(string Outcome, WorkloadSubmissionProgress? Progress = null,
    string? Stage = null, string? Subject = null, string? Reason = null, bool RequiresInspection = false);
public sealed record WorkloadSubmissionStep(string? Reason = null, string? Subject = null, bool RequiresInspection = false);

/// <summary>Only exact catalog identities and a trusted server principal cross this boundary.</summary>
public interface IOperatorWorkloadSubmission
{
    ValueTask<WorkloadSubmissionResult> SubmitAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, string actor, CancellationToken token);
    ValueTask<WorkloadSubmissionProgress?> ReadAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, CancellationToken token);
}

/// <summary>Typed generation-scoped steps; implementations retain their durable authority and dispatch barriers.</summary>
internal interface IWorkloadSubmissionSteps
{
    ValueTask<WorkloadSubmissionStep> InspectAsync(CancellationToken token);
    ValueTask<WorkloadSubmissionStep> RecoverAsync(CancellationToken token);
    ValueTask<WorkloadSubmissionStep> PrepareAsync(WorkloadSubmissionStage stage, CancellationToken token);
    ValueTask<WorkloadSubmissionProgress> ReadAsync(CancellationToken token);
}
internal sealed class SubmitWorkloadGeneration(IWorkloadSubmissionSteps steps)
{
    internal async ValueTask<WorkloadSubmissionResult> ExecuteAsync(CancellationToken token)
    {
        var stage = "Inspection";
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await steps.InspectAsync(token);
            if (result.Reason is not null) return await Stop(result);
            stage = "Recovery";
            token.ThrowIfCancellationRequested();
            result = await steps.RecoverAsync(token);
            if (result.Reason is not null) return await Stop(result);
            var saved = await steps.ReadAsync(token);
            if (saved.Submission == "submitted") return new("submitted", saved);
            // Safe read projection is not mutation authority. Unknown proof blocks all NEW stages.
            if (saved.Submission is not ("partial" or "notSubmitted"))
                return new("stopped", saved, stage, Reason: "submission-proof-unavailable", RequiresInspection: true);
            foreach (var next in Enum.GetValues<WorkloadSubmissionStage>())
            {
                stage = next.ToString(); token.ThrowIfCancellationRequested();
                result = await steps.PrepareAsync(next, token);
                if (result.Reason is not null) return await Stop(result);
            }
            var progress = await steps.ReadAsync(token);
            return new(progress.Submission == "submitted" ? "submitted" : "stopped", progress,
                Reason: progress.Submission == "submitted" ? null : "submission-proof-unavailable");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new("cancelled", Stage: stage, Reason: "cancelled", RequiresInspection: true); }
        catch (InvalidDataException) { return new("stopped", Stage: stage, Reason: "input-unavailable", RequiresInspection: true); }
        catch (IOException) { return new("unavailable", Stage: stage, Reason: "storage-unavailable", RequiresInspection: true); }

        async ValueTask<WorkloadSubmissionResult> Stop(WorkloadSubmissionStep stopped) =>
            new(stopped.Reason switch { "generation-not-found" => "notFound", "unsupported-shape" => "unsupportedShape", _ => "stopped" },
                stage == "Inspection" ? null : await steps.ReadAsync(token), stage, stopped.Subject, stopped.Reason, stopped.RequiresInspection);
    }
}
