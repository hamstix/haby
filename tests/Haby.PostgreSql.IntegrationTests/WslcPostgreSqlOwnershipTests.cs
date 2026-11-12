using System.Diagnostics;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class WslcPostgreSqlOwnershipTests
{
    [Fact]
    public async Task Command_capture_retains_both_server_log_streams()
    {
        var directory = Path.Combine(Path.GetTempPath(), "haby-owned-streams-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var process = new WslcPostgreSqlProcess(directory);
            var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('out');[Console]::Error.Write('err')"]
                : ["-c", "printf out; printf err >&2"];
            var result = await process.RunAsync(executable, arguments, "owned-streams", TimeSpan.FromSeconds(5));
            Assert.True(result.Complete);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("out", result.Output);
            Assert.Equal("err", result.Error);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Ambiguous_inventory_cannot_authorize_recovery()
    {
        Assert.Throws<InvalidDataException>(() => WslcPostgreSqlInspection.InventoryContains(
            "[{\"Names\":\"owned\"},{\"Names\":\"owned\"}]", "Names", "owned"));
    }

    [Fact]
    public void Formatted_inventory_preserves_exact_names()
    {
        Assert.True(WslcPostgreSqlInspection.InventoryContains("[\n {\"Names\":\"owned\"}\n]", "Names", "owned"));
        Assert.False(WslcPostgreSqlInspection.InventoryContains("{\"Names\":\"owned-other\"}", "Names", "owned"));
    }

    [Theory]
    [InlineData("fixture", "0.0.0.0", "data")]
    [InlineData("foreign", "127.0.0.1", "data")]
    [InlineData("fixture", "127.0.0.1", "foreign")]
    public void Container_requires_matching_fixture_bind_and_volume(string fixture, string address, string volume)
    {
        var state = new WslcPostgreSqlState(1, "run", "fixture", "owned", "data", "127.0.0.1", 1234);
        var json = $$"""
            {"Id":"{{new string('a', 64)}}","Name":"/owned",
             "Labels":{"haby.test":"postgresql-fixture","haby.run":"run","haby.fixture":"{{fixture}}"},
             "Ports":{"5432/tcp":[{"HostIp":"{{address}}","HostPort":1234}]},
             "Mounts":[{"Type":"volume","Name":"{{volume}}","Destination":"/var/lib/postgresql/data"}]}
            """;
        Assert.Throws<InvalidDataException>(() => WslcPostgreSqlInspection.Container(json, state));
    }

    [Fact]
    public async Task Command_timeout_returns_incomplete_with_a_bounded_drain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "haby-owned-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var process = new WslcPostgreSqlProcess(directory);
            var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]
                : ["-c", "exec sleep 30"];
            var watch = Stopwatch.StartNew();
            var result = await process.RunAsync(executable, arguments, "owned-timeout", TimeSpan.FromMilliseconds(500));
            Assert.True(result.Started);
            Assert.True(result.TimedOut);
            Assert.False(result.Complete);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally { Directory.Delete(directory, true); }
    }
}
