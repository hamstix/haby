using System.Text.Json;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class WslcPostgreSqlLifecycleTests
{
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
    private sealed class ControlledCli(OwnedControl context, CancellationTokenSource? cancel, bool wrongLabel)
        : WslcPostgreSqlProcess(context.Run.LocalDirectory)
    {
        private bool _container;
        private bool _volume;
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
