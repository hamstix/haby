using System.Text.Json;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class WslcPostgreSqlLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Recovery_reports_every_fixture_after_multiple_command_failures()
    {
        using var context = new OwnedControl();
        var run = context.Run with { WslcPath = Path.Combine(context.Run.LocalDirectory, "missing-owned-cli-" + Guid.NewGuid().ToString("N")) };
        for (var index = 0; index < 3; index++) _ = new WslcPostgreSqlServer(run);
        var expected = Directory.GetDirectories(run.LocalDirectory).Select(Path.GetFileName).Order().ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => WslcPostgreSqlServer.RecoverRunAsync(run));
        var text = await File.ReadAllTextAsync(Path.Combine(run.LocalDirectory, "supervisor-recovery-result.json"));
        output.WriteLine("RECOVERY_FAILURE_CONTROL " + text);
        var result = JsonSerializer.Deserialize(text, WslcPostgreSqlJson.Default.WslcPostgreSqlRecoveryResult)!;
        Assert.Equal(expected, result.AttemptedFixtureDirectories.Order());
        Assert.Equal(expected, result.UnreconciledFixtureDirectories.Order());
        Assert.Equal(3, Directory.GetFiles(run.LocalDirectory, "*.lifecycle.txt", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(0, true)]
    public async Task Owned_relay_exit_before_disposal_preserves_failure_and_continues_backend_cleanup(int exitCode, bool recoverWithoutChild)
    {
        using var context = new OwnedControl();
        var cli = new ControlledCli(context, null, wrongLabel: false, existingResources: true);
        _ = new WslcPostgreSqlServer(context.Run, cli);
        var allocated = context.State with
        {
            HostPort = 1234,
            ContainerId = new string('a', 64),
            StartAttempted = true,
            StartConclusive = true,
            VolumeAttempted = true,
            VolumeConclusive = true
        };
        context.Persist(allocated);
        var server = new WslcPostgreSqlServer(context.Run, allocated, cli);
        var signal = Path.Combine(context.Run.LocalDirectory, "exit.signal");
        var script = Path.Combine(context.Run.LocalDirectory, "owned-child.ps1");
        File.WriteAllText(script, "param([string]$Signal,[int]$Code);while(-not (Test-Path -LiteralPath $Signal)){Start-Sleep -Milliseconds 20};exit $Code");
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
            { UseShellExecute = false, CreateNoWindow = true }
        };
        string[] arguments = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-NonInteractive", "-File", script, signal, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), context.Run.RunId, context.State.FixtureId]
            : ["-c", "while [ ! -f \"$1\" ]; do sleep 0.02; done; exit \"$2\"", "owned-relay-control", signal, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), context.Run.RunId, context.State.FixtureId];
        foreach (var argument in arguments) child.StartInfo.ArgumentList.Add(argument);
        Assert.True(child.Start());
        var childDisposed = false;
        try
        {
            server.RegisterRelay(child);
            output.WriteLine("RELAY_BEFORE " + JsonSerializer.Serialize(context.State, WslcPostgreSqlJson.Default.WslcPostgreSqlState));
            File.WriteAllText(signal, "exit");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(exitCode, child.ExitCode);
            var pid = child.Id;
            if (recoverWithoutChild)
            {
                child.Dispose();
                childDisposed = true;
                server = new WslcPostgreSqlServer(context.Run, context.State, cli);
            }
            var failure = await Record.ExceptionAsync(server.DisposeAsync);
            output.WriteLine($"RELAY_EXIT_CONTROL pid={pid} exit={exitCode} recovered={recoverWithoutChild} failed={failure is not null} cleaned={context.State.Cleaned} backendStops={cli.Stops} volumeRemovals={cli.VolumeRemovals}");
            output.WriteLine("RELAY_AFTER " + JsonSerializer.Serialize(context.State, WslcPostgreSqlJson.Default.WslcPostgreSqlState));
            Assert.Equal(1, cli.Stops);
            Assert.Equal(1, cli.VolumeRemovals);
            if (recoverWithoutChild)
            {
                Assert.Null(context.State.RelayExitCode);
                Assert.Contains("Unverified", context.State.RelayExitStatus, StringComparison.Ordinal);
            }
            else Assert.Equal(exitCode, context.State.RelayExitCode);
            if (exitCode != 0 || recoverWithoutChild)
            {
                Assert.IsType<InvalidOperationException>(failure);
                Assert.False(context.State.Cleaned);
                Assert.Equal(pid, context.State.RelayPid);
            }
            else
            {
                Assert.Null(failure);
                Assert.True(context.State.Cleaned);
                Assert.Null(context.State.RelayPid);
            }
        }
        finally
        {
            if (!childDisposed)
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_launch_recovers_only_proven_resources_and_disposal_is_repeatable(bool cancel)
    {
        using var context = new OwnedControl();
        using var cancellation = new CancellationTokenSource();
        var cli = new ControlledCli(context, cancel ? cancellation : null, wrongLabel: false);
        var server = new WslcPostgreSqlServer(context.Run, cli);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(cancellation.Token));
        Assert.True(context.State.Cleaned);
        Assert.Equal(1, cli.Stops);
        Assert.Equal(1, cli.VolumeRemovals);
        await server.DisposeAsync();
        Assert.Equal(1, cli.Stops);
        Assert.Equal(1, cli.VolumeRemovals);
    }

    [Fact]
    public async Task Foreign_labels_after_partial_launch_never_authorize_stop_or_volume_removal()
    {
        using var context = new OwnedControl();
        var cli = new ControlledCli(context, null, wrongLabel: true);
        var server = new WslcPostgreSqlServer(context.Run, cli);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
        Assert.Contains("cleanup is incomplete", failure.Message, StringComparison.Ordinal);
        Assert.False(context.State.Cleaned);
        Assert.True(File.Exists(Path.Combine(context.Run.LocalDirectory, ".cleanup-incomplete")));
        Assert.Equal(0, cli.Stops);
        Assert.Equal(0, cli.VolumeRemovals);
    }

    private sealed class OwnedControl : IDisposable
    {
        internal WslcPostgreSqlRun Run { get; }
        internal WslcPostgreSqlState State => JsonSerializer.Deserialize(
            File.ReadAllText(Directory.GetFiles(Run.LocalDirectory, "fixture.json", SearchOption.AllDirectories).Single()),
            WslcPostgreSqlJson.Default.WslcPostgreSqlState)!;

        internal void Persist(WslcPostgreSqlState state) => File.WriteAllText(
            Directory.GetFiles(Run.LocalDirectory, "fixture.json", SearchOption.AllDirectories).Single(),
            JsonSerializer.Serialize(state, WslcPostgreSqlJson.Default.WslcPostgreSqlState));

        internal OwnedControl()
        {
            var id = Guid.NewGuid().ToString("N");
            var root = Path.Combine(Path.GetTempPath(), id);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".run-id"), id);
            Run = new(1, id, root, OperatingSystem.IsWindows() ? root : "C:\\owned\\" + id,
                "controlled-cli", "controlled-windows-pwsh", "127.0.0.1", OperatingSystem.IsLinux(), "controlled-python");
        }

        public void Dispose() => Directory.Delete(Run.LocalDirectory, true);
    }

    // Structured command controls exercise the real startup/recovery/disposal
    // code. They never launch a container or invoke private members reflectively.
    private sealed class ControlledCli(OwnedControl context, CancellationTokenSource? cancel, bool wrongLabel, bool existingResources = false)
        : WslcPostgreSqlProcess(context.Run.LocalDirectory)
    {
        private bool _container = existingResources;
        private bool _volume = existingResources;
        internal int Stops { get; private set; }
        internal int VolumeRemovals { get; private set; }

        internal override Task<Result> RunAsync(string executable, IReadOnlyList<string> arguments, string operation,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = context.State;
            var text = "";
            switch (operation)
            {
                case "private-root-proof": text = "OWNED_PRIVATE_ROOT_AND_ROUTE"; break;
                case "private-path-map": text = context.Run.CliDirectory; break;
                case "choose-port": text = "1234"; break;
                case "volume-create": _volume = true; break;
                case "volume-inspect":
                case "volume-cleanup-proof": text = Volume(state); break;
                case "server-create":
                    Assert.True(state.StartAttempted);
                    Assert.True(state.VolumeConclusive);
                    Assert.True(File.Exists(Path.Combine(context.Run.LocalDirectory, ".run-id")));
                    _container = true;
                    cancel?.Cancel();
                    // The CLI outcome is lost after allocation: no CID output/file.
                    return Task.FromResult(new Result(null, "", true, true, false));
                case "owned-name-readback":
                case "server-absence": text = _container ? "{\"Names\":\"" + state.Name + "\"}" : ""; break;
                case "owned-server-inspect": text = Container(state); break;
                case "server-stop":
                    Assert.Equal(new string('a', 64), arguments[^1]);
                    Stops++; _container = false; break;
                case "volume-inventory":
                case "volume-absence": text = _volume ? "{\"Name\":\"" + state.VolumeName + "\"}" : ""; break;
                case "volume-remove": Assert.Equal(state.VolumeName, arguments[^1]); VolumeRemovals++; _volume = false; break;
                default: throw new InvalidOperationException("Unexpected controlled operation: " + operation);
            }
            return Task.FromResult(new Result(0, text, true, false, true));
        }

        private static string Volume(WslcPostgreSqlState state) => $$$"""
            {"Name":"{{{state.VolumeName}}}","Labels":{"haby.test":"postgresql-fixture","haby.run":"{{{state.RunId}}}","haby.fixture":"{{{state.FixtureId}}}"}}
            """;

        private string Container(WslcPostgreSqlState state) => $$$"""
            {"Id":"{{{new string('a', 64)}}}","Name":"{{{state.Name}}}",
             "Labels":{"haby.test":"postgresql-fixture","haby.run":"{{{state.RunId}}}","haby.fixture":"{{{(wrongLabel ? "foreign" : state.FixtureId)}}}"},
             "Ports":{"5432/tcp":[{"HostIp":"127.0.0.1","HostPort":1234}]},
             "Mounts":[{"Type":"volume","Name":"{{{state.VolumeName}}}","Destination":"/var/lib/postgresql/data"}]}
            """;
    }
}
