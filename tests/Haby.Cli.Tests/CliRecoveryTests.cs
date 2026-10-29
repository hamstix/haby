using System.Text.Json;
using Google.Protobuf;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliRecoveryTests
{
    internal const string CanonicalAttempt = "01a1838d-981a-7913-9e18-b42480d65e11";
    [Theory]
    [InlineData(RecoveryStatus.Rejected, 1)]
    [InlineData(RecoveryStatus.TargetAbsent, 1)]
    [InlineData(RecoveryStatus.TargetUnavailable, 1)]
    [InlineData(RecoveryStatus.OperationNotFound, 1)]
    [InlineData(RecoveryStatus.InvalidRequest, 1)]
    [InlineData(RecoveryStatus.UnsupportedRecoveryAction, 1)]
    [InlineData(RecoveryStatus.Found, 0)]
    [InlineData(RecoveryStatus.Assigned, 0)]
    [InlineData(RecoveryStatus.AlreadyAssigned, 0)]
    public void Recovery_results_are_explicit_safe_text_and_source_generated_json(RecoveryStatus status, int exit)
    {
        var reply = Reply(status);
        using var bytes = new MemoryStream();
        using (var wire = new CodedOutputStream(bytes, leaveOpen: true))
        { reply.WriteTo(wire); wire.WriteTag(100, WireFormat.WireType.LengthDelimited); wire.WriteString("protected-note-sentinel"); }
        reply = RecoveryReply.Parser.ParseFrom(bytes.ToArray());
        using var text = new StringWriter(); using var json = new StringWriter();
        Assert.Equal(exit, new CliTextPresenter(text).PrintRecovery("operation recovery inspect", reply));
        Assert.Equal(exit, new CliJsonPresenter(json).PrintRecovery("operation recovery inspect", reply));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(RecoveryPresentation.Outcome(status), document.RootElement.GetProperty("outcome"u8).GetString());
        Assert.DoesNotContain("protected-note-sentinel", text.ToString() + json);
    }

    [Fact]
    public void Historical_database_name_reason_remains_renderable_in_recovery_inspection()
    {
        var reply = Reply(RecoveryStatus.Found);
        reply.Preview.Operation.State = OperationState.Failed;
        reply.Preview.Operation.Reason = (OperationReason)4;
        using var json = new StringWriter(); using var text = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(json).PrintRecovery("operation recovery inspect", reply));
        Assert.Equal(0, new CliTextPresenter(text).PrintRecovery("operation recovery inspect", reply));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal("databaseNameInvalid", document.RootElement.GetProperty("data")
            .GetProperty("preview").GetProperty("operation").GetProperty("reason").GetString());
        Assert.Contains("reason: databaseNameInvalid", text.ToString());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("preview")]
    [InlineData("state")]
    [InlineData("receipt")]
    [InlineData("reason")]
    public void Unknown_or_incomplete_recovery_wire_results_fail_before_output(string field)
    {
        var reply = Reply(RecoveryStatus.Found);
        switch (field)
        {
            case "status": reply.Status = (RecoveryStatus)999; break;
            case "preview": reply.Preview = null; break;
            case "state": reply.Preview.Operation.State = (OperationState)999; break;
            case "receipt": reply.Status = RecoveryStatus.Assigned; reply.Receipt = null; break;
            case "reason": reply.Receipt.Reason = (RecoveryReason)999; break;
        }
        using var text = new StringWriter(); using var json = new StringWriter();
        Assert.Throws<InvalidDataException>(() => new CliTextPresenter(text).PrintRecovery("operation recovery inspect", reply));
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(json).PrintRecovery("operation recovery inspect", reply));
        Assert.Equal("", text.ToString()); Assert.Equal("", json.ToString());
    }

    internal static RecoveryReply Reply(RecoveryStatus status)
    {
        var operation = CliOperationTests.Snapshot(OperationStatus.Found, OperationState.NeedsAttention).Operation;
        operation.AttemptId = CanonicalAttempt;
        return new RecoveryReply
        {
            Status = status,
            Preview = new RecoveryPreview
            {
                Operation = operation,
                TargetContract = "postgresql.database/v1alpha1",
                TargetName = "example_db",
                UncertainAttemptId = CanonicalAttempt
            },
            Receipt = new RecoveryReceipt
            {
                Key = operation.Key.Clone(),
                DecisionId = "decision-id",
                AttemptId = CanonicalAttempt,
                Reason = RecoveryReason.OperatorAcceptedExistingDatabase,
                TechnicalActor = "host",
                DecidedAtUtc = "2026-10-28T12:00:00Z"
            }
        };
    }
}
