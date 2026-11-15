using Haby.Domain;

namespace Haby.Application;

/// <summary>Server admission of supported desired state. Never executes accepted work.</summary>
public interface IOperatorDeploymentAdmission
{
    ValueTask<DurablePlanResult> CreatePlanAsync(DeploymentChangeSetId changeSet, string actor, CancellationToken token);
    ValueTask<DurablePlanCurrentStatus> CheckPlanAsync(DeploymentPlanId plan, CancellationToken token);
    ValueTask<DurableCommitResult> CommitAsync(DeploymentChangeSetId changeSet, DeploymentPlanId plan, string actor, CancellationToken token);
    ValueTask<OperatorGenerationStage?> ReadStageAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, CancellationToken token);
}

/// <summary>Catalog-only admission evidence; no live readiness or external health assertion.</summary>
public enum OperatorWorkloadSubmission { NotSubmitted, Unknown }
public sealed record OperatorGenerationStage(long? AppliedGeneration, OperatorWorkloadSubmission Submission);
