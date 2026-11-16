using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliWorkloadItem(string ComponentId, string WorkloadId, string Contract, string TargetName, string ImageReference, string[] Documents);
internal sealed record CliWorkloadReview(string Status, CliWorkloadItem[] Items);

internal static partial class PlanningPresentation
{
    internal static CliWorkloadReview? WorkloadReview(WorkloadReview? value) => value is null ? null :
        new(value.Status, value.Items.Select(x => new CliWorkloadItem(x.ComponentId, x.WorkloadId, x.Contract, x.TargetName, x.ImageReference, x.Documents.ToArray())).ToArray());
}

internal sealed partial class CliTextPresenter
{
    private void PrintWorkloadReview(CliWorkloadReview? review)
    {
        if (review is null) return;
        _output.WriteLine($"Workload description: {review.Status}; intended desired state only.");
        foreach (var item in review.Items)
            _output.WriteLine($"Workload: {item.ComponentId}/{item.WorkloadId}; contract: {item.Contract}; target: {item.TargetName}; image: {item.ImageReference}; documents: {string.Join(", ", item.Documents)}");
    }
}
