using System.Text.Json.Nodes;
using Xunit;

namespace Haby.Application.Tests;

public sealed class RolloutObservationContractTests
{
    [Theory]
    [InlineData("calls")]
    [InlineData("rounds")]
    [InlineData("read")]
    [InlineData("poll")]
    [InlineData("request")]
    public void Readiness_call_limits_refuse_invalid_bounds_instead_of_unbounded_wait(string field)
    {
        var options = field switch
        {
            "calls" => new RolloutOptions(MaximumCalls: 513),
            "rounds" => new(MaximumRounds: 0),
            "read" => new(ReadTimeoutMilliseconds: 10001),
            "poll" => new(PollMilliseconds: 0),
            _ => new(RequestMilliseconds: 300001)
        };
        Assert.Throws<ArgumentException>(options.Validate);
        new RolloutOptions(512, 512, 100, 100, 1).Validate();
    }
    [Theory]
    [InlineData("enum")]
    [InlineData("code")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public void Stored_observation_results_are_closed_and_unknown_state_never_becomes_success(string corruption)
    {
        var value = new RolloutResult(false, null, null, RolloutStop.Complete,
            [new("Workload/api/main", null, null, RolloutBudget.Active, new(RolloutOutcome.NotSubmitted, "not-submitted"))]);
        var json = RolloutJson.Encode(value);
        if (corruption == "duplicate") Assert.ThrowsAny<Exception>(() => RolloutJson.Decode("{\"Ready\":true," + json[1..]));
        else
        {
            var node = JsonNode.Parse(json)!;
            if (corruption == "enum") node["Members"]![0]!["Observation"]!["Outcome"] = 99;
            if (corruption == "code") node["Members"]![0]!["Observation"]!["Code"] = "unsafe raw message";
            if (corruption == "unknown") node["Extra"] = true;
            Assert.ThrowsAny<Exception>(() => RolloutJson.Decode(node.ToJsonString()));
            if (corruption == "unknown")
            {
                Assert.Throws<InvalidDataException>(() => RolloutJson.Decode(RolloutJson.Encode(value with { Applied = true, CompletionReason = "applied" })));
                Assert.Throws<InvalidDataException>(() => RolloutJson.Decode(RolloutJson.Encode(value with { CompletionReason = "future-success" })));
            }
        }
    }
}
