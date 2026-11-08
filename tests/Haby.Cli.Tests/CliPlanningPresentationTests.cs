using System.Text.Json;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliPlanningPresentationTests
{
    [Fact]
    public void Mixed_plan_text_and_generated_JSON_expose_new_work_and_original_reuse_together()
    {
        var reply = DescriptionReply(new PlanDescription { Status = PlanDescriptionStatus.OperationGraph });
        reply.Plan.Operations.Clear();
        reply.Plan.Operations.Add(new PlannedOperationSummary { OperationId = "extra-create", ResourceId = "extra", ActionContract = "sample.create/v1" });
        reply.Plan.ReusedResources.Add(new ReusedResourceSummary { ResourceId = "old", ManagementId = Guid.NewGuid().ToString(), SourceGeneration = 1 });
        using var json = new StringWriter(); using var text = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(json).PrintPlanning("plan get", reply));
        Assert.Equal(0, new CliTextPresenter(text).PrintPlanning("plan get", reply));
        using var document = JsonDocument.Parse(json.ToString());
        var plan = document.RootElement.GetProperty("data"u8).GetProperty("plan"u8);
        Assert.Equal("extra", Assert.Single(plan.GetProperty("operations"u8).EnumerateArray()).GetProperty("resourceId"u8).GetString());
        Assert.Equal(1, Assert.Single(plan.GetProperty("reusedResources"u8).EnumerateArray()).GetProperty("sourceGeneration"u8).GetInt64());
        Assert.Contains("Operation: extra-create", text.ToString()); Assert.Contains("Reuse Resource: old", text.ToString());
        Assert.DoesNotContain("unsupported", text.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_exposes_operation_identity_ownership_and_dependencies_without_payloads()
    {
        var reply = new PlanningReply
        {
            Status = PlanningStatus.Planned,
            Plan = new DurablePlanSummary
            {
                Reference = new RevisionReference(),
                Operations = { new PlannedOperationSummary { OperationId = "db-create", ResourceId = "db",
                ActionContract = "postgresql.database.create/v1alpha1", Dependencies = { "earlier" } } }
            }
        };
        using var json = new StringWriter(); using var text = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(json).PrintPlanning("plan create", reply));
        Assert.Equal(0, new CliTextPresenter(text).PrintPlanning("plan create", reply));
        using var document = System.Text.Json.JsonDocument.Parse(json.ToString());
        var operation = document.RootElement.GetProperty("data").GetProperty("plan").GetProperty("operations")[0];
        Assert.Equal("db-create", operation.GetProperty("operationId").GetString());
        Assert.Equal("db", operation.GetProperty("resourceId").GetString());
        Assert.Equal("earlier", operation.GetProperty("dependencies")[0].GetString());
        Assert.Contains("db-create", text.ToString());
        Assert.False(operation.TryGetProperty("input", out _));
    }

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
    [InlineData(PlanningStatus.ConfigurationRefused, "configurationRefused")]
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

    [Fact]
    public void Supported_graph_is_truthful_in_text_and_JSON_without_fabricating_an_action()
    {
        var reply = DescriptionReply(new PlanDescription { Status = PlanDescriptionStatus.OperationGraph });
        using var json = new StringWriter();
        using var text = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(json).PrintPlanning("plan get", reply));
        Assert.Equal(0, new CliTextPresenter(text).PrintPlanning("plan get", reply));
        using var document = JsonDocument.Parse(json.ToString());
        var plan = document.RootElement.GetProperty("data"u8).GetProperty("plan"u8);
        Assert.Equal("operationGraph", plan.GetProperty("intendedWork"u8).GetProperty("status"u8).GetString());
        Assert.False(plan.GetProperty("intendedWork"u8).TryGetProperty("action"u8, out _));
        Assert.Contains("supported operation graph; single-action detail is not applicable", text.ToString());
        Assert.DoesNotContain("unsupported", text.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PlanDescriptionStatus.RevisionNotFound, "revisionNotFound")]
    [InlineData(PlanDescriptionStatus.InputCorrupt, "inputCorrupt")]
    [InlineData(PlanDescriptionStatus.UnsupportedShape, "unsupportedShape")]
    [InlineData(PlanDescriptionStatus.PinnedInputMismatch, "pinnedInputMismatch")]
    [InlineData(PlanDescriptionStatus.ActionDescriberUnavailable, "actionDescriberUnavailable")]
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
        Assert.Contains("Single-action detail unavailable: " + expected, text.ToString());
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

    [Theory]
    [InlineData("plan")]
    [InlineData("change")]
    [InlineData("unknown-code")]
    [InlineData("wrong-outcome")]
    [InlineData("excess")]
    public void Invalid_configuration_refusal_replies_fail_before_any_output(string mode)
    {
        var reply = new PlanningReply { Status = PlanningStatus.ConfigurationRefused };
        if (mode == "plan") reply.Plan = new() { TechnicalActorId = "private-value" };
        if (mode == "change") reply.ChangeSet = new() { TechnicalActorId = "private-value" };
        reply.ConfigurationErrorCodes.Add(mode == "unknown-code" ? "private-value" : "profile.secret-projections.unsupported");
        if (mode == "wrong-outcome") reply.Status = PlanningStatus.Planned;
        if (mode == "excess") for (var i = 0; i < 100; i++) reply.ConfigurationErrorCodes.Add("profile.slot.not-found");
        using var json = new StringWriter(); using var text = new StringWriter();
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(json).PrintPlanning("plan create", reply));
        Assert.Throws<InvalidDataException>(() => new CliTextPresenter(text).PrintPlanning("plan create", reply));
        Assert.Equal("", json.ToString()); Assert.Equal("", text.ToString());
    }

    private static PlanningReply DescriptionReply(PlanDescription description) => new()
    {
        Status = PlanningStatus.Expired,
        Plan = new DurablePlanSummary
        { PlanId = "exact-plan", Reference = new RevisionReference(), IntendedWork = description }
    };
}
