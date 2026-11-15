using Haby.Application;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    private async Task<WorkloadReview?> WorkloadSummaryAsync(DurablePlanningInputs inputs, CancellationToken token)
    {
        var description = await workloadDescriptions.ExecuteAsync(inputs, token);
        if (description.Status == WorkloadPlanDescriptionStatus.NotApplicable) return null;
        var result = new WorkloadReview { Status = description.Status == WorkloadPlanDescriptionStatus.Available ? "available" : "unavailable" };
        foreach (var item in description.Items)
        {
            var summary = new WorkloadReviewItem
            {
                ComponentId = item.Component,
                WorkloadId = item.Workload,
                Contract = item.Contract,
                TargetName = item.TargetName,
                ImageReference = item.ImageReference
            };
            summary.Documents.AddRange(item.Documents);
            result.Items.Add(summary);
        }
        return result;
    }
}
