using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class LinuxRelayIdentityTests(ITestOutputHelper output)
{
    private const string Run = "a1111111111111111111111111111111";
    private const string Fixture = "b2222222222222222222222222222222";
    private static string Executable => Path.GetFullPath("identity-control-python");
    private static string Script => Path.GetFullPath("identity-control-relay.py");
    private static LinuxRelayIdentity Identity() => new(1, 123, "11111111-2222-3333-4444-555555555555", 9007199254740993,
        "pid:[12345]", Executable, [Executable, Script, "--run-id", Run, "--fixture-id", Fixture]);

    [Fact]
    public void Native_integer_identity_round_trips_without_wall_clock_authority()
    {
        var identity = Identity();
        var state = new WslcPostgreSqlState(2, Run, Fixture, "control", "control-data", "127.0.0.1", 0,
            RelayPid: identity.Pid, RelayStartTicks: 1, LinuxRelay: identity);
        var text = JsonSerializer.Serialize(state, WslcPostgreSqlJson.Default.WslcPostgreSqlState);
        var loaded = JsonSerializer.Deserialize(text, WslcPostgreSqlJson.Default.WslcPostgreSqlState)!;
        Assert.Equal(9007199254740993UL, loaded.LinuxRelay!.StartTime);
        var otherClock = loaded with { RelayStartTicks = long.MaxValue };
        new Observations(otherClock.LinuxRelay!).RequireMatch(loaded.LinuxRelay, identity.Pid, Executable, Script, Run, Fixture);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("boot")]
    [InlineData("start")]
    [InlineData("namespace")]
    [InlineData("version")]
    [InlineData("executable")]
    [InlineData("argv-executable")]
    [InlineData("script")]
    [InlineData("run")]
    [InlineData("fixture")]
    [InlineData("substring")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("extra-argument")]
    public void Revalidation_refuses_changed_identity_or_command(string change)
    {
        var expected = Identity();
        var actual = expected with { Arguments = expected.Arguments.ToArray() };
        actual = change switch
        {
            "pid" => actual with { Pid = 124 },
            "boot" => actual with { BootId = "22222222-2222-3333-4444-555555555555" },
            "start" => actual with { StartTime = expected.StartTime + 1 },
            "namespace" => actual with { PidNamespace = "pid:[54321]" },
            "version" => actual with { FormatVersion = 0 },
            "executable" => actual with { Executable = Executable + "-unrelated" },
            "duplicate" => actual with { Arguments = [.. actual.Arguments, "--run-id", Run] },
            "missing" => actual with { Arguments = actual.Arguments[..^1] },
            "extra-argument" => actual with { Arguments = [.. actual.Arguments, "unexpected"] },
            _ => actual
        };
        if (change == "argv-executable") actual.Arguments[0] += "-unrelated";
        if (change == "script") actual.Arguments[1] += "-unrelated";
        if (change == "run") actual.Arguments[3] = Fixture;
        if (change == "fixture") actual.Arguments[5] = Run;
        if (change == "substring") actual.Arguments[3] = "prefix" + Run;
        if (change == "empty") actual.Arguments[0] = "";
        Assert.Throws<InvalidDataException>(() => new Observations(actual).RequireMatch(expected, expected.Pid, Executable, Script, Run, Fixture));
    }

    [Theory]
    [InlineData("9007199254740993")]
    [InlineData("18446744073709551615")]
    public void Stat_parser_keeps_exact_unsigned_integer_and_parenthesized_comm(string start)
    {
        Assert.Equal(ulong.Parse(start, CultureInfo.InvariantCulture), LinuxRelayIdentityReader.ParseStat(123, Stat(start)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e4")]
    [InlineData("18446744073709551616")]
    [InlineData("")]
    public void Stat_parser_refuses_invalid_birth_units(string start) =>
        Assert.Throws<InvalidDataException>(() => LinuxRelayIdentityReader.ParseStat(123, Stat(start)));

    [Theory]
    [InlineData("pid")]
    [InlineData("short")]
    [InlineData("zombie")]
    [InlineData("malformed-number")]
    [InlineData("comm")]
    public void Stat_parser_refuses_incomplete_or_nonlive_observations(string change)
    {
        var text = Stat("42");
        text = change switch
        {
            "pid" => text.Replace("123 (", "124 (", StringComparison.Ordinal),
            "short" => "123 (comm) S 1 2 3",
            "zombie" => text.Replace(") S ", ") Z ", StringComparison.Ordinal),
            "malformed-number" => text + " invalid",
            _ => "123 malformed"
        };
        Assert.Throws<InvalidDataException>(() => LinuxRelayIdentityReader.ParseStat(123, text));
    }

    [Fact]
    public void Unreadable_or_disappeared_process_is_unknown_and_never_accepted()
    {
        var expected = Identity();
        Assert.Throws<IOException>(() => new Observations(null).RequireMatch(expected, expected.Pid, Executable, Script, Run, Fixture));
    }

    [Fact]
    public async Task Native_reader_observes_one_owned_child_then_refuses_natural_disappearance()
    {
        if (!OperatingSystem.IsLinux()) return;
        var nonce = Guid.NewGuid().ToString("N");
        using var child = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            ArgumentList = { "-c", "read line", "owned-identity-control", "--run-id", nonce, "--fixture-id", Fixture },
            RedirectStandardInput = true
        })!;
        try
        {
            var reader = new LinuxRelayIdentityReader();
            var identity = reader.Read(child.Id);
            var next = reader.Read(child.Id);
            Assert.Equal(identity.StartTime, next.StartTime);
            Assert.Equal(identity.BootId, next.BootId);
            Assert.Equal(identity.PidNamespace, next.PidNamespace);
            Assert.Equal(identity.Arguments, next.Arguments);
            await child.StandardInput.WriteLineAsync(nonce);
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, child.ExitCode);
            var phase = "before-read";
            var accepted = false;
            var rejection = Record.Exception(() =>
            {
                phase = "native-revalidation";
                reader.Read(child.Id);
                accepted = true;
                phase = "identity-available";
            });
            Assert.True(rejection is InvalidDataException or FileNotFoundException or DirectoryNotFoundException,
                "Expected unavailable namespace/stat rejection during native revalidation.");
            Assert.False(accepted);
            Assert.Equal("native-revalidation", phase);
            output.WriteLine($"NATIVE_IDENTITY_CONTROL phase={phase} outcome=UNKNOWN_REFUSED signals=0 naturalExit={child.ExitCode} rejection={rejection!.GetType().Name}");
        }
        finally
        {
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static string Stat(string start)
    {
        var fields = Enumerable.Repeat("0", 50).ToArray();
        fields[0] = "S"; fields[19] = start; fields[22] = ulong.MaxValue.ToString(CultureInfo.InvariantCulture);
        return "123 (name with ) and ( parentheses) " + string.Join(' ', fields) + "\n";
    }

    private sealed class Observations(LinuxRelayIdentity? identity) : LinuxRelayIdentityReader
    {
        internal override LinuxRelayIdentity Read(int pid) => identity ?? throw new IOException("Controlled unavailable identity.");
    }
}
