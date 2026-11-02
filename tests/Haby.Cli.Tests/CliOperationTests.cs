using System.Text.Json;
using Google.Protobuf;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliOperationTests
{
    [Fact]
    public void Operation_identity_uses_a_distinct_wire_field_and_rejects_removed_identity_fields()
    {
        var current = new OperationRequest { DeploymentId = "deployment", Generation = 1, OperationId = "db-create" };
        Assert.Equal(5, OperationRequest.Descriptor.FindFieldByName("operation_id").FieldNumber);
        Assert.Null(OperationRequest.Descriptor.FindFieldByNumber(3));
        Assert.Null(OperationRequest.Descriptor.FindFieldByNumber(4));
        Assert.Equal(current, OperationRequest.Parser.ParseFrom(current.ToByteArray()));
        using var bytes = new MemoryStream();
        using (var wire = new CodedOutputStream(bytes, leaveOpen: true))
        {
            new OperationRequest { DeploymentId = current.DeploymentId, Generation = current.Generation }.WriteTo(wire);
            wire.WriteTag(3, WireFormat.WireType.LengthDelimited); wire.WriteString(current.OperationId);
            wire.WriteTag(4, WireFormat.WireType.Varint); wire.WriteEnum(1);
        }
        var unsupported = OperationRequest.Parser.ParseFrom(bytes.ToArray());
        Assert.Equal("", unsupported.OperationId);
    }

    [Theory]
    [InlineData(OperationStatus.InvalidRequest, "invalidRequest")]
    [InlineData(OperationStatus.UnsupportedAction, "unsupportedAction")]
    [InlineData(OperationStatus.UnsupportedShape, "unsupportedShape")]
    [InlineData(OperationStatus.OperationNotFound, "operationNotFound")]
    [InlineData(OperationStatus.DispatchUncertain, "dispatchUncertain")]
    public void Bounded_rejections_are_distinct_in_text_and_generated_json(OperationStatus status, string outcome)
    {
        using var json = new StringWriter(); using var text = new StringWriter();
        var reply = new OperationReply { Status = status };
        Assert.Equal(1, new CliJsonPresenter(json).PrintOperation("operation execute", reply));
        Assert.Equal(1, new CliTextPresenter(text).PrintOperation("operation execute", reply));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(outcome, document.RootElement.GetProperty("outcome"u8).GetString());
        Assert.Contains(outcome, text.ToString());
    }

    [Theory]
    [InlineData(OperationStatus.Found, OperationState.Dispatching, 0)]
    [InlineData(OperationStatus.Succeeded, OperationState.Succeeded, 0)]
    [InlineData(OperationStatus.Failed, OperationState.Failed, 1)]
    [InlineData(OperationStatus.RetryableFailure, OperationState.Preparing, 1)]
    [InlineData(OperationStatus.NeedsAttention, OperationState.NeedsAttention, 1)]
    [InlineData(OperationStatus.DispatchUncertain, OperationState.Dispatching, 1)]
    public void State_and_history_are_safe_and_preserve_uncertainty(OperationStatus status, OperationState state, int exit)
    {
        var reply = Snapshot(status, state);
        using var bytes = new MemoryStream();
        using (var wire = new CodedOutputStream(bytes, leaveOpen: true))
        {
            reply.WriteTo(wire); wire.WriteTag(100, WireFormat.WireType.LengthDelimited);
            wire.WriteString("secret-payload-sentinel");
        }
        var received = OperationReply.Parser.ParseFrom(bytes.ToArray());
        using var json = new StringWriter(); using var text = new StringWriter();
        Assert.Equal(exit, new CliJsonPresenter(json).PrintOperation("operation get", received));
        Assert.Equal(exit, new CliTextPresenter(text).PrintOperation("operation get", received));
        using var document = JsonDocument.Parse(json.ToString());
        var data = document.RootElement.GetProperty("data"u8);
        Assert.Equal("db", data.GetProperty("operation"u8).GetProperty("key"u8).GetProperty("operationId"u8).GetString());
        Assert.Equal("uncertain", data.GetProperty("attempts"u8)[0].GetProperty("outcome"u8).GetString());
        Assert.Contains("attempt-id", text.ToString());
        Assert.DoesNotContain("secret-payload-sentinel", json.ToString() + text);
        if (state is OperationState.Dispatching or OperationState.NeedsAttention)
            Assert.Contains("do not blindly retry", text.ToString());
    }

    [Theory]
    [InlineData(2, "secretUnavailable")]
    [InlineData(5, "connectivity")]
    [InlineData(6, "conflict")]
    [InlineData(7, "serverRejected")]
    [InlineData(8, "uncertainAfterDispatch")]
    [InlineData(9, "inputInvalid")]
    [InlineData(10, "inputUnavailable")]
    [InlineData(11, "handlerUnavailable")]
    public void Current_reasons_have_explicit_text_and_json_mapping(int number, string spelling)
    {
        Assert.Equal(spelling, char.ToLowerInvariant(((OperationReason)number).ToString()[0]) + ((OperationReason)number).ToString()[1..]);
        // Decode independently of the generated enum name.
        var historical = DurableOperationSummary.Parser.ParseFrom(new byte[] { 24, (byte)number });
        Assert.Equal((OperationReason)number, historical.Reason);
        var reply = Snapshot(OperationStatus.Found, OperationState.Failed);
        reply.Operation.Reason = historical.Reason;
        using var json = new StringWriter(); using var text = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(json).PrintOperation("operation get", reply));
        Assert.Equal(0, new CliTextPresenter(text).PrintOperation("operation get", reply));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(spelling, document.RootElement.GetProperty("data")
            .GetProperty("operation").GetProperty("reason").GetString());
        Assert.Contains(spelling, text.ToString());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("snapshot")]
    [InlineData("state")]
    [InlineData("reason")]
    [InlineData("attempt")]
    public void Unknown_or_incomplete_wire_results_fail_before_output(string field)
    {
        var reply = Snapshot(OperationStatus.Found, OperationState.Pending);
        switch (field)
        {
            case "status": reply.Status = (OperationStatus)999; break;
            case "snapshot": reply.Operation = null; break;
            case "state": reply.Operation.State = (OperationState)999; break;
            case "reason": reply.Operation.Reason = (OperationReason)999; break;
            case "attempt": reply.Attempts[0].Outcome = (OperationAttemptOutcome)999; break;
        }
        using var json = new StringWriter(); using var text = new StringWriter();
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(json).PrintOperation("operation get", reply));
        Assert.Throws<InvalidDataException>(() => new CliTextPresenter(text).PrintOperation("operation get", reply));
        Assert.Equal("", json.ToString()); Assert.Equal("", text.ToString());
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("operation execute")]
    [InlineData("operation get deployment 1")]
    [InlineData("operation execute deployment")]
    [InlineData("operation execute deployment latest db")]
    [InlineData("operation execute deployment 1 db --action delete")]
    [InlineData("operation execute deployment 1 db --action create --password secret-sentinel")]
    public async Task Incomplete_or_unsupported_commands_never_contact_registry(string command)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(new EnvironmentEndpointSource(), new GrpcRegistryClientFactory(), output, error);
        Assert.Equal(2, await cli.RunAsync(command.Split(' ')));
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("secret-sentinel", error.ToString());
    }

    internal static OperationReply Snapshot(OperationStatus status, OperationState state) => new()
    {
        Status = status,
        Operation = new DurableOperationSummary
        {
            Key = new OperationRequest { DeploymentId = "deployment", Generation = 1, OperationId = "db" },
            State = state,
            Reason = OperationReason.UncertainAfterDispatch,
            Reference = new RevisionReference { ApplicationRecordId = "application", RevisionNumber = 1 },
            EnvironmentRecordId = "environment",
            ApplicationRecordId = "application",
            ProviderInstanceId = "provider",
            ConfigurationRevision = 1,
            ImplementationId = "implementation",
            AttemptNumber = 1,
            AttemptId = "attempt-id"
        },
        Attempts = { new OperationAttempt { AttemptId = "attempt-id", AttemptNumber = 1, StartedAtUtc = "2026-10-28T12:00:00Z", Outcome = OperationAttemptOutcome.Uncertain } }
    };
}
