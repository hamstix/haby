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
}
