using System.Text.Json;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliPlanningPresentationTests
{
    [Theory]
    [InlineData(PlanningStatus.EnvironmentNotFound, "environmentNotFound")]
    [InlineData(PlanningStatus.RevisionNotFound, "revisionNotFound")]
    [InlineData(PlanningStatus.RevisionNotOwnedByApplication, "revisionNotOwnedByApplication")]
    [InlineData(PlanningStatus.InvalidRequest, "invalidRequest")]
    [InlineData(PlanningStatus.UnsupportedPrecondition, "unsupportedPrecondition")]
    [InlineData(PlanningStatus.ChangeSetNotFound, "changeSetNotFound")]
    [InlineData(PlanningStatus.ChangeSetStale, "changeSetStale")]
    [InlineData(PlanningStatus.AliasNotResolved, "aliasNotResolved")]
    [InlineData(PlanningStatus.ProviderRevisionUnavailable, "providerRevisionUnavailable")]
    [InlineData(PlanningStatus.ProviderCapabilityMismatch, "providerCapabilityMismatch")]
    [InlineData(PlanningStatus.UnsupportedShape, "unsupportedShape")]
    [InlineData(PlanningStatus.PlanNotFound, "planNotFound")]
    [InlineData(PlanningStatus.Expired, "expired")]
    [InlineData(PlanningStatus.PlanStale, "planStale")]
    [InlineData(PlanningStatus.PlanInputUnavailable, "planInputUnavailable")]
    [InlineData(PlanningStatus.Closed, "closed")]
    public void Check_preserves_typed_rejections_and_exit_codes_in_both_formats(PlanningStatus status, string outcome)
    {
        var reply = new PlanningReply { Status = status };
        using var json = new StringWriter();
        using var text = new StringWriter();
        Assert.Equal(1, new CliJsonPresenter(json).PrintPlanning("plan check", reply));
        Assert.Equal(1, new CliTextPresenter(text).PrintPlanning("plan check", reply));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(outcome, document.RootElement.GetProperty("outcome"u8).GetString());
        Assert.Equal(outcome + Environment.NewLine, text.ToString());
        Assert.Empty(document.RootElement.GetProperty("diagnostics"u8).EnumerateArray());
    }

    [Theory]
    [InlineData(PlanningStatus.Unspecified)]
    [InlineData((PlanningStatus)999)]
    public void Unset_and_unknown_status_fail_closed_without_output(PlanningStatus status)
    {
        var reply = new PlanningReply { Status = status };
        using var json = new StringWriter();
        using var text = new StringWriter();
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(json).PrintPlanning("plan get", reply));
        Assert.Throws<InvalidDataException>(() => new CliTextPresenter(text).PrintPlanning("plan get", reply));
        Assert.Equal("", json.ToString());
        Assert.Equal("", text.ToString());
    }

    [Theory]
    [InlineData(PlanningStatus.Closed)]
    [InlineData(PlanningStatus.Expired)]
    [InlineData(PlanningStatus.PlanStale)]
    public void Reading_existing_Plan_succeeds_while_check_rejects(PlanningStatus status)
    {
        var reply = new PlanningReply { Status = status, Plan = new DurablePlanSummary { Reference = new RevisionReference() } };
        using var output = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(output).PrintPlanning("plan get", reply));
        Assert.Equal(1, new CliJsonPresenter(output).PrintPlanning("plan check", reply));
    }

    [Theory]
    [InlineData(PlanDescriptionStatus.RevisionNotFound, "revisionNotFound")]
    [InlineData(PlanDescriptionStatus.InputCorrupt, "inputCorrupt")]
    [InlineData(PlanDescriptionStatus.UnsupportedShape, "unsupportedShape")]
    [InlineData(PlanDescriptionStatus.PinnedInputMismatch, "pinnedInputMismatch")]
    public void Description_unavailability_preserves_plan_identity_and_status(PlanDescriptionStatus status, string expected)
    {
        var reply = DescriptionReply(new PlanDescription { Status = status });
        using var json = new StringWriter();
        using var text = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(json).PrintPlanning("plan get", reply));
        Assert.Equal(0, new CliTextPresenter(text).PrintPlanning("plan get", reply));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal("expired", document.RootElement.GetProperty("outcome"u8).GetString());
        var plan = document.RootElement.GetProperty("data"u8).GetProperty("plan"u8);
        Assert.Equal("exact-plan", plan.GetProperty("planId"u8).GetString());
        Assert.Equal(expected, plan.GetProperty("intendedWork"u8).GetProperty("status"u8).GetString());
        Assert.Contains("Intended action unavailable: " + expected, text.ToString());
        Assert.DoesNotContain("target:", text.ToString());
    }

    [Theory]
    [InlineData("unknownStatus")]
    [InlineData("unspecifiedStatus")]
    [InlineData("missingAction")]
    [InlineData("unknownKind")]
    [InlineData("unspecifiedKind")]
    [InlineData("missingTarget")]
    [InlineData("unavailableWithAction")]
    public void Unknown_or_inconsistent_description_fails_before_either_formatter_writes(string fault)
    {
        var description = new PlanDescription
        {
            Status = PlanDescriptionStatus.Available,
            Action = new IntendedPlanAction
            {
                Kind = IntendedActionKind.Create,
                ResourceId = "database",
                ResourceType = "postgresql.database/v1alpha1",
                Target = new IntendedActionTarget { Name = "sample_db" }
            }
        };
        if (fault == "unknownStatus") description.Status = (PlanDescriptionStatus)999;
        if (fault == "unspecifiedStatus") description.Status = PlanDescriptionStatus.Unspecified;
        if (fault == "missingAction") description.Action = null;
        if (fault == "unknownKind") description.Action!.Kind = (IntendedActionKind)999;
        if (fault == "unspecifiedKind") description.Action!.Kind = IntendedActionKind.Unspecified;
        if (fault == "missingTarget") description.Action!.Target = null;
        if (fault == "unavailableWithAction") description.Status = PlanDescriptionStatus.InputCorrupt;
        var reply = DescriptionReply(description);
        using var json = new StringWriter();
        using var text = new StringWriter();
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(json).PrintPlanning("plan get", reply));
        Assert.Throws<InvalidDataException>(() => new CliTextPresenter(text).PrintPlanning("plan get", reply));
        Assert.Equal("", json.ToString());
        Assert.Equal("", text.ToString());
    }

    [Theory]
    [InlineData("queue.topic/v1alpha1", "orders")]
    [InlineData("object-storage.bucket/v1alpha1", "archive")]
    public void Intended_targets_render_without_Provider_specific_contract_knowledge(string resourceType, string targetName)
    {
        var reply = DescriptionReply(new PlanDescription
        {
            Status = PlanDescriptionStatus.Available,
            Action = new IntendedPlanAction
            {
                Kind = IntendedActionKind.Create,
                ResourceId = "resource",
                ResourceType = resourceType,
                Target = new IntendedActionTarget { Name = targetName }
            }
        });
        using var json = new StringWriter();
        using var text = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(json).PrintPlanning("plan get", reply));
        Assert.Equal(0, new CliTextPresenter(text).PrintPlanning("plan get", reply));
        Assert.Contains($"Resource: resource; type: {resourceType}; target: {targetName}", text.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        var action = document.RootElement.GetProperty("data"u8).GetProperty("plan"u8)
            .GetProperty("intendedWork"u8).GetProperty("action"u8);
        Assert.Equal(resourceType, action.GetProperty("resourceType"u8).GetString());
        Assert.Equal(targetName, action.GetProperty("target"u8).GetProperty("name"u8).GetString());
    }

    private static PlanningReply DescriptionReply(PlanDescription description) => new()
    {
        Status = PlanningStatus.Expired,
        Plan = new DurablePlanSummary
        { PlanId = "exact-plan", Reference = new RevisionReference(), IntendedWork = description }
    };
}
