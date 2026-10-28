using System.Text.Json;
using Google.Protobuf;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliCommitPresentationTests
{
    [Theory]
    [InlineData(CommitStatus.InvalidRequest, "invalidRequest")]
    [InlineData(CommitStatus.ChangeSetNotFound, "changeSetNotFound")]
    [InlineData(CommitStatus.PlanNotFound, "planNotFound")]
    [InlineData(CommitStatus.PlanDoesNotBelongToChangeSet, "planDoesNotBelongToChangeSet")]
    [InlineData(CommitStatus.Closed, "closed")]
    [InlineData(CommitStatus.Expired, "expired")]
    [InlineData(CommitStatus.ChangeSetStale, "changeSetStale")]
    [InlineData(CommitStatus.PlanStale, "planStale")]
    [InlineData(CommitStatus.PlanInputUnavailable, "planInputUnavailable")]
    [InlineData(CommitStatus.UnsupportedPrecondition, "unsupportedPrecondition")]
    [InlineData(CommitStatus.UnsupportedShape, "unsupportedShape")]
    [InlineData(CommitStatus.CommitNotFound, "commitNotFound")]
    [InlineData(CommitStatus.GenerationNotFound, "generationNotFound")]
    public void Rejections_preserve_typed_outcomes_and_actual_generation(CommitStatus status, string outcome)
    {
        var reply = new CommitReply { Status = status };
        if (status == CommitStatus.ChangeSetStale) reply.ActualGeneration = 1;
        using var json = new StringWriter();
        using var text = new StringWriter();
        Assert.Equal(1, new CliJsonPresenter(json).PrintCommit("changeset commit", reply));
        Assert.Equal(1, new CliTextPresenter(text).PrintCommit("changeset commit", reply));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(outcome, document.RootElement.GetProperty("outcome"u8).GetString());
        Assert.Contains(outcome, text.ToString());
        Assert.Empty(document.RootElement.GetProperty("diagnostics"u8).EnumerateArray());
        if (status == CommitStatus.ChangeSetStale)
            Assert.Equal(1, document.RootElement.GetProperty("data"u8).GetProperty("actualGeneration"u8).GetInt64());
    }

    [Theory]
    [InlineData(CommitStatus.Unspecified)]
    [InlineData((CommitStatus)999)]
    [InlineData(CommitStatus.Committed)]
    [InlineData(CommitStatus.Found)]
    public void Unknown_or_missing_success_snapshot_fails_without_output(CommitStatus status)
    {
        using var json = new StringWriter();
        using var text = new StringWriter();
        var reply = new CommitReply { Status = status };
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(json).PrintCommit("changeset commit", reply));
        Assert.Throws<InvalidDataException>(() => new CliTextPresenter(text).PrintCommit("changeset commit", reply));
        Assert.Equal("", json.ToString());
        Assert.Equal("", text.ToString());
    }

    [Fact]
    public void Successful_snapshot_has_desired_only_meaning_and_drops_unknown_wire_payload()
    {
        var reply = new CommitReply
        {
            Status = CommitStatus.Committed,
            Commit = new DurableCommitSummary { ChangeSetId = "change", PlanId = "original-plan", DeploymentId = "deployment", Generation = 1 }
        };
        using var bytes = new MemoryStream();
        using (var wire = new CodedOutputStream(bytes, leaveOpen: true))
        {
            reply.WriteTo(wire);
            wire.WriteTag(100, WireFormat.WireType.LengthDelimited);
            wire.WriteString("secret-payload-sentinel");
        }
        var received = CommitReply.Parser.ParseFrom(bytes.ToArray());
        using var json = new StringWriter();
        using var text = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(json).PrintCommit("changeset commit", received));
        Assert.Equal(0, new CliTextPresenter(text).PrintCommit("changeset commit", received));
        Assert.DoesNotContain("secret-payload-sentinel", json.ToString());
        Assert.DoesNotContain("secret-payload-sentinel", text.ToString());
        Assert.Contains("does not confirm applied infrastructure", text.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        var data = document.RootElement.GetProperty("data"u8);
        Assert.Equal("desiredStateOnly", data.GetProperty("stateMeaning"u8).GetString());
        Assert.Equal("original-plan", data.GetProperty("commit"u8).GetProperty("planId"u8).GetString());
        Assert.False(data.TryGetProperty("appliedGeneration"u8, out _));
    }

    [Theory]
    [InlineData("changeset commit")]
    [InlineData("changeset commit change")]
    [InlineData("changeset commit --plan plan")]
    [InlineData("changeset commit get")]
    [InlineData("deployment generation get deployment")]
    public async Task Explicit_identifiers_are_required_before_transport(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new CliApplication(new EnvironmentEndpointSource(), new GrpcRegistryClientFactory(), output, error);
        Assert.Equal(2, await cli.RunAsync(command.Split(' ')));
        Assert.Equal("", output.ToString());
    }
}
