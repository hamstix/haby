using Haby.Configuration.Client;
using Npgsql;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class RuntimeSettingExpectationsTests
{
    [Theory]
    [InlineData("200", "30", "disable", true)]
    [InlineData("15", "30", "disable", false)]
    [InlineData("200", "31", "disable", false)]
    [InlineData("200", "30", "require", false)]
    [InlineData(null, null, null, true)]
    public void Independently_parsed_received_driver_settings_detect_each_mismatch(string? timeout, string? command, string? ssl, bool matches)
    {
        // Literal received data, independent of the Haby renderer/options mapper.
        var received = new NpgsqlConnectionStringBuilder("Host=localhost;Username=probe;Password=private-sentinel;Timeout=200;Command Timeout=30;SSL Mode=Disable");
        var expected = RuntimeSettingExpectations.Parse(timeout, command, ssl);
        if (matches) expected.AssertMatches(received);
        else
        {
            var failure = Assert.Throws<RuntimeSettingExpectationException>(() => expected.AssertMatches(received));
            Assert.Equal("Received configuration settings did not match expectations.", failure.Message);
            Assert.DoesNotContain("private-sentinel", failure.ToString());
        }
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("0", null, null)]
    [InlineData("301", null, null)]
    [InlineData(" 15", null, null)]
    [InlineData("+15", null, null)]
    [InlineData("1e2", null, null)]
    [InlineData(null, "3601", null)]
    [InlineData(null, null, "0")]
    [InlineData(null, null, "private-sentinel")]
    public void Malformed_expectations_have_a_fixed_value_free_error(string? timeout, string? command, string? ssl)
    {
        var failure = Assert.Throws<RuntimeSettingExpectationException>(() => RuntimeSettingExpectations.Parse(timeout, command, ssl));
        Assert.Equal("Invalid runtime setting expectations.", failure.Message);
        Assert.DoesNotContain("private-sentinel", failure.ToString());
    }
}
