using System.Text.Json;
using Haby.Cli;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliWorkloadPresentationTests
{
    [Fact]
    public void Desired_Workload_snapshot_is_not_submitted_or_ready_and_uses_generated_JSON()
    {
        var review = new WorkloadReview { Status = "available" };
        review.Items.Add(new WorkloadReviewItem { ComponentId = "api", WorkloadId = "main", Contract = "sample.workload/v1", TargetName = "orders-api-main", ImageReference = "registry.example/app:v1" });
        var reply = new CommitReply
        {
            Status = CommitStatus.Found,
            DesiredGeneration = new DesiredGenerationSummary
            { DeploymentId = "deployment", Generation = 2, DesiredPresence = "Present", Reference = new RevisionReference(), WorkloadDescription = review },
            WorkloadStage = new() { Submission = "notSubmitted", AppliedGeneration = 1 }
        };
        using var text = new StringWriter(); using var json = new StringWriter();
        new CliTextPresenter(text).PrintCommit("deployment generation get", reply);
        new CliJsonPresenter(json).PrintCommit("deployment generation get", reply);
        Assert.Contains("notSubmitted", text.ToString());
        Assert.Contains("Readiness is not established", text.ToString());
        Assert.Contains("orders-api-main", text.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        var desired = document.RootElement.GetProperty("data"u8).GetProperty("desiredGeneration"u8);
        Assert.Equal("notSubmitted", document.RootElement.GetProperty("data"u8).GetProperty("workloadStage"u8).GetProperty("submission"u8).GetString());
        Assert.Equal(1, document.RootElement.GetProperty("data"u8).GetProperty("workloadStage"u8).GetProperty("appliedGeneration"u8).GetInt64());
        Assert.Equal("available", desired.GetProperty("workloadDescription"u8).GetProperty("status"u8).GetString());
    }
}
