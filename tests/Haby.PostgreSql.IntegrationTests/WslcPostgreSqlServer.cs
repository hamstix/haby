using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Haby.PostgreSql.IntegrationTests;

// One external fixture owns one server, one labelled volume and (on WSL) one
// transparent relay. This is deliberately not an endpoint pool or a broker.
internal sealed class WslcPostgreSqlServer
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(20);
    private readonly WslcPostgreSqlRun _run;
    private readonly string _directory;
    private readonly WslcPostgreSqlProcess _process;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stopping = new();
    private WslcPostgreSqlState _state;
    private Process? _relay;
    private CancellationTokenSource? _relayPipes;
    private Task<string>? _relayOutput;
    private Task<string>? _relayError;
    private bool _privateVerified;
    private string? _connection;

    internal WslcPostgreSqlServer(WslcPostgreSqlRun run, WslcPostgreSqlProcess? process = null)
    {
        run.Validate();
        _run = run;
        var fixture = Guid.NewGuid().ToString("N");
        _directory = Path.Combine(run.LocalDirectory, fixture);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, ".fixture-id"), fixture);
        _process = process ?? new(_directory);
        var name = "haby-pg-" + run.RunId[..8] + "-" + fixture;
        _state = new(1, run.RunId, fixture, name, name + "-data", run.HostAddress, 0);
        Persist(); // Both nonces exist before any allocation or process launch.
    }

    internal WslcPostgreSqlServer(WslcPostgreSqlRun run, WslcPostgreSqlState state, WslcPostgreSqlProcess? process = null)
    {
        run.Validate();
        if (state.FormatVersion != 1 || state.RunId != run.RunId || !Guid.TryParseExact(state.FixtureId, "N", out _) ||
            state.Name != "haby-pg-" + run.RunId[..8] + "-" + state.FixtureId ||
            state.VolumeName != state.Name + "-data" || state.HostAddress != run.HostAddress)
            throw new InvalidOperationException("Owned fixture recovery identity is invalid.");
        _run = run; _state = state;
        _directory = Path.Combine(run.LocalDirectory, state.FixtureId);
        if (File.ReadAllText(Path.Combine(_directory, ".fixture-id")) != state.FixtureId)
            throw new InvalidOperationException("Owned fixture recovery nonce is invalid.");
        _process = process ?? new(_directory);
    }

    internal string ConnectionString => _connection ?? throw new InvalidOperationException("Owned fixture is not ready.");

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(150));
        await _gate.WaitAsync(startup.Token);
        try
        {
            await VerifyHostAsync(startup.Token);
            _privateVerified = true;
            if (File.Exists(Path.Combine(_run.LocalDirectory, ".cleanup-incomplete")))
                throw new InvalidOperationException("A previous owned fixture cleanup is incomplete.");
            var port = await WindowsAsync(
                "$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse(" + Quote(_run.HostAddress) + "),0);" +
                "$l.Start();$p=$l.LocalEndpoint.Port;$l.Stop();Write-Output $p", "choose-port", startup.Token);
            if (!int.TryParse(port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort) || hostPort is < 1 or > 65535)
                throw new InvalidDataException();
            _state = _state with { HostPort = hostPort, VolumeAttempted = true }; Persist();
            var labels = new[] { "--label", "haby.test=postgresql-fixture", "--label", "haby.run=" + _state.RunId, "--label", "haby.fixture=" + _state.FixtureId };
            await RequiredAsync(["volume", "create", .. labels, _state.VolumeName], "volume-create", MetadataTimeout, startup.Token);
            WslcPostgreSqlInspection.Volume(await RequiredAsync(["volume", "inspect", _state.VolumeName], "volume-inspect", MetadataTimeout, startup.Token), _state);
            _state = _state with { VolumeConclusive = true }; Persist();
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var env = Path.Combine(_directory, "postgres.env");
            await File.WriteAllTextAsync(env, "POSTGRES_PASSWORD=" + password + "\n", new UTF8Encoding(false), startup.Token);
            _state = _state with { StartAttempted = true }; Persist();
            WslcPostgreSqlProcess.Result created;
            try
            {
                created = await _process.RunAsync(_run.WslcPath,
                    ["run", "--detach", "--rm", "--name", _state.Name, .. labels,
                     "--cidfile", _run.CliPath(_state.FixtureId, "container.cid"),
                     "--publish", _state.HostAddress + ":" + hostPort.ToString(CultureInfo.InvariantCulture) + ":5432",
                     "--volume", _state.VolumeName + ":/var/lib/postgresql/data",
                     "--env-file", _run.CliPath(_state.FixtureId, "postgres.env"), "postgres:17.6-alpine"],
                    "server-create", TimeSpan.FromSeconds(45), startup.Token);
            }
            finally { File.Delete(env); }
            var inspected = await RecoverContainerAsync(startup.Token);
            if (inspected is null || created.ExitCode != 0 || created.TimedOut || !created.Complete)
                throw new InvalidOperationException("Owned server creation did not complete successfully.");
            var cidPath = Path.Combine(_directory, "container.cid");
            if (!File.Exists(cidPath) || new FileInfo(cidPath).Length > 128 || File.ReadAllText(cidPath).Trim() != inspected)
                throw new InvalidOperationException("Owned server CID file is missing or inconsistent; recovery retained.");
            _state = _state with { ContainerId = inspected, StartConclusive = true }; Persist();
            await WaitForServerAsync(startup.Token);
            await VerifyListenerAsync(startup.Token);
            var system = (await RequiredAsync(["exec", inspected, "psql", "-U", "postgres", "-d", "postgres", "-At", "-c",
                "SELECT system_identifier::text FROM pg_control_system()"], "server-identity", TimeSpan.FromSeconds(5), startup.Token)).Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(system, @"\A[0-9]{1,20}\z")) throw new InvalidDataException();
            var clientPort = hostPort;
            if (_run.UseLoopbackRelay) clientPort = await StartRelayAsync(startup.Token);
            var connection = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = clientPort,
                Database = "postgres",
                Username = "postgres",
                Password = password,
                Pooling = false
            };
            foreach (var host in new[] { "127.0.0.1", "localhost" }) await VerifyTcpAsync(connection, host, system, startup.Token);
            // Probe-only Timeout/CommandTimeout never enter the test connection.
            _connection = connection.ConnectionString;
            _state = _state with { Ready = true, SystemIdentifier = system }; Persist();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            _state = _state with { FailureCode = "StartupFailed" };
            try { Persist(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await RecordFailureAsync("startup-failure-private.txt", failure);
            try { await CleanupCoreAsync(); }
            catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
            {
                MarkIncomplete();
                await RecordFailureAsync("startup-cleanup-failure-private.txt", cleanupFailure);
                throw new InvalidOperationException("Owned PostgreSQL fixture startup failed and cleanup is incomplete; private ownership records retained.");
            }
            throw new InvalidOperationException("Owned PostgreSQL fixture startup failed; output withheld and owned resources cleaned.");
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> UtilityAsync(IReadOnlyList<string> arguments, bool logs = false, CancellationToken token = default)
    {
        if (!_state.Ready || _state.Cleaned || _state.ContainerId is null) throw new InvalidOperationException("Owned fixture is not ready.");
        var verified = await RequiredAsync(["inspect", _state.ContainerId], "utility-ownership", MetadataTimeout, token);
        WslcPostgreSqlInspection.Container(verified, _state);
        if (!logs) return await RequiredAsync(["exec", _state.ContainerId, .. arguments], "client-utility", TimeSpan.FromSeconds(120), token);
        var result = await _process.RunAsync(_run.WslcPath, ["logs", _state.ContainerId], "server-logs", MetadataTimeout, token);
        if (result.ExitCode != 0 || result.TimedOut || !result.Complete) throw new InvalidOperationException("Owned server log capture failed; output withheld.");
        return result.Output + result.Error;
    }

    internal async Task DisposeAsync()
    {
        _stopping.Cancel();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        await _gate.WaitAsync(deadline.Token);
        try { await CleanupCoreAsync(); }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            MarkIncomplete();
            throw new InvalidOperationException("Owned PostgreSQL fixture cleanup is incomplete; exact ownership records retained.");
        }
        finally { _gate.Release(); }
    }

    private async Task CleanupCoreAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cleanup.Token;
        Exception? relayFailure = null;
        try { await StopRelayAsync(token); }
        catch (Exception failure) when (failure is not OutOfMemoryException) { relayFailure = failure; }
        var id = _state.StartAttempted ? await RecoverContainerAsync(token) : null;
        if (_state.StartAttempted && !_state.StartConclusive && id is null)
            throw new InvalidOperationException("Server creation outcome is uncertain; exact name and labelled volume retained for supervisor recovery.");
        if (id is not null)
        {
            var stopped = await _process.RunAsync(_run.WslcPath, ["stop", "--time", "5", id], "server-stop", TimeSpan.FromSeconds(60), token);
            if (stopped.ExitCode != 0 || stopped.TimedOut || !stopped.Complete) throw new InvalidOperationException("Owned server stop failed; original outcome retained.");
        }
        var containers = await RequiredAsync(["ps", "-a", "--format", "json"], "server-absence", MetadataTimeout, token);
        if (WslcPostgreSqlInspection.InventoryContains(containers, "Names", _state.Name)) throw new InvalidDataException();
        if (_state.VolumeAttempted)
        {
            var volumes = await RequiredAsync(["volume", "ls", "--format", "json"], "volume-inventory", MetadataTimeout, token);
            if (WslcPostgreSqlInspection.InventoryContains(volumes, "Name", _state.VolumeName))
            {
                WslcPostgreSqlInspection.Volume(await RequiredAsync(["volume", "inspect", _state.VolumeName], "volume-cleanup-proof", MetadataTimeout, token), _state);
                await RequiredAsync(["volume", "remove", _state.VolumeName], "volume-remove", MetadataTimeout, token);
                var remaining = await RequiredAsync(["volume", "ls", "--format", "json"], "volume-absence", MetadataTimeout, token);
                if (WslcPostgreSqlInspection.InventoryContains(remaining, "Name", _state.VolumeName)) throw new InvalidDataException();
            }
            else if (!_state.VolumeConclusive)
                throw new InvalidOperationException("Owned volume allocation outcome is uncertain; exact ownership record retained.");
        }
        File.Delete(Path.Combine(_directory, "postgres.env"));
        _state = _state with { Ready = false, Cleaned = relayFailure is null }; _connection = null; Persist();
        if (relayFailure is not null) throw new InvalidOperationException("Owned relay cleanup failed; PostgreSQL resources were reconciled.", relayFailure);
    }

    private async Task<string?> RecoverContainerAsync(CancellationToken token)
    {
        // Listing is read-only. Only this pre-recorded exact unique name can be
        // selected, followed by mandatory run/fixture labels, bind and volume proof.
        var listed = await RequiredAsync(["ps", "-a", "--format", "json"], "owned-name-readback", MetadataTimeout, token);
        if (!WslcPostgreSqlInspection.InventoryContains(listed, "Names", _state.Name)) return null;
        var json = await RequiredAsync(["inspect", _state.Name], "owned-server-inspect", MetadataTimeout, token);
        var id = WslcPostgreSqlInspection.Container(json, _state);
        _state = _state with { ContainerId = id, StartConclusive = true }; Persist();
        return id;
    }

    private async Task WaitForServerAsync(CancellationToken token)
    {
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
        ready.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            var result = await _process.RunAsync(_run.WslcPath, ["exec", _state.ContainerId!, "pg_isready", "-h", "127.0.0.1", "-U", "postgres"],
                "tcp-readiness", TimeSpan.FromSeconds(5), ready.Token);
            if (result.ExitCode == 0 && !result.TimedOut && result.Complete) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ready.Token);
        }
    }

    private async Task VerifyHostAsync(CancellationToken token)
    {
        var root = Quote(_run.CliDirectory);
        var proof = await WindowsAsync("$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User;$a=Get-Acl -LiteralPath " + root +
            ";if(-not $a.AreAccessRulesProtected -or @($a.Access|Where-Object {$_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -ne $sid.Value}).Count -or @($a.Access).Count -ne 1){throw 'private root'};" +
            (_run.HostAddress == "127.0.0.1" ? "" : "$n=@(Get-NetIPAddress -AddressFamily IPv4|Where-Object {$_.IPAddress -eq " + Quote(_run.HostAddress) + " -and $_.InterfaceAlias -match 'WSL'});if($n.Count -ne 1){throw 'private interface'};") +
            "Write-Output 'OWNED_PRIVATE_ROOT_AND_ROUTE'", "private-root-proof", token);
        if (proof.Trim() != "OWNED_PRIVATE_ROOT_AND_ROUTE") throw new InvalidDataException();
        if (OperatingSystem.IsLinux())
        {
            var mapped = await _process.RunAsync("wslpath", ["-w", _run.LocalDirectory], "private-path-map", TimeSpan.FromSeconds(5), token);
            if (mapped.ExitCode != 0 || !mapped.Complete || !string.Equals(mapped.Output.Trim().TrimEnd('\\'), _run.CliDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
        }
        else if (!string.Equals(Path.GetFullPath(_run.LocalDirectory), Path.GetFullPath(_run.CliDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
    }

    private async Task VerifyListenerAsync(CancellationToken token)
    {
        var proof = await WindowsAsync("$x=@(Get-NetTCPConnection -State Listen -LocalPort " + _state.HostPort.ToString(CultureInfo.InvariantCulture) +
            " -ErrorAction SilentlyContinue|Where-Object {$_.LocalAddress -eq " + Quote(_run.HostAddress) +
            "});if($x.Count -ne 1){throw 'listener'};Write-Output 'EXACT_OWNED_LISTENER'", "listener-proof", token);
        if (proof.Trim() != "EXACT_OWNED_LISTENER") throw new InvalidDataException();
    }

    private static async Task VerifyTcpAsync(NpgsqlConnectionStringBuilder original, string host, string expected, CancellationToken token)
    {
        var probe = new NpgsqlConnectionStringBuilder(original.ConnectionString) { Host = host, Timeout = 5, CommandTimeout = 5 };
        await using (var connection = new NpgsqlConnection(probe.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var query = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", connection);
            if ((string?)await query.ExecuteScalarAsync(token) != expected) throw new InvalidDataException();
        }
        probe.Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            await using var wrong = new NpgsqlConnection(probe.ConnectionString);
            await wrong.OpenAsync(token);
        }
        catch (PostgresException failure) when (failure.SqlState == "28P01") { return; }
        throw new InvalidDataException();
    }

    private async Task<int> StartRelayAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException();
        var script = Path.Combine(AppContext.BaseDirectory, "postgresql-loopback-relay.py");
        var ready = Path.Combine(_directory, "relay-ready.json");
        _relayPipes = new CancellationTokenSource();
        _relay = new Process
        {
            StartInfo = new ProcessStartInfo(_run.PythonPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        foreach (var argument in new[] { script, "--run-id", _state.RunId, "--fixture-id", _state.FixtureId,
            "--upstream-host", _state.HostAddress, "--upstream-port", _state.HostPort.ToString(CultureInfo.InvariantCulture), "--ready", ready,
            "--events", Path.Combine(_directory, "relay-events.jsonl") })
            _relay.StartInfo.ArgumentList.Add(argument);
        if (!_relay.Start()) throw new InvalidOperationException();
        RegisterRelay(_relay);
        _relayOutput = WslcPostgreSqlProcess.ReadBoundedAsync(_relay.StandardOutput, Path.Combine(_directory, "relay.stdout"), _relayPipes.Token);
        _relayError = WslcPostgreSqlProcess.ReadBoundedAsync(_relay.StandardError, Path.Combine(_directory, "relay.stderr"), _relayPipes.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(ready))
        {
            if (_relay.HasExited) throw new InvalidDataException();
            await Task.Delay(50, deadline.Token);
        }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ready, deadline.Token));
        var root = document.RootElement;
        if (root.GetProperty("runId"u8).GetString() != _state.RunId || root.GetProperty("fixtureId"u8).GetString() != _state.FixtureId ||
            root.GetProperty("pid"u8).GetInt32() != _relay.Id) throw new InvalidDataException();
        var port = root.GetProperty("port"u8).GetInt32();
        if (port is < 1 or > 65535) throw new InvalidDataException();
        return port;
    }

    // Also permits an explicitly owned child in lifecycle fault controls.
    internal void RegisterRelay(Process relay)
    {
        _relay = relay;
        _state = _state with { RelayPid = relay.Id, RelayStartTicks = relay.StartTime.ToUniversalTime().Ticks }; Persist();
    }

    private async Task StopRelayAsync(CancellationToken token)
    {
        if (_state.RelayPid is null) return;
        Process? process = _relay;
        var absenceVerified = false;
        try
        {
            process ??= Process.GetProcessById(_state.RelayPid.Value);
            if (process.HasExited)
            {
                absenceVerified = true;
                if (_relay is null)
                {
                    RecordRelayExit(null, "ExitedUnverified");
                    throw new InvalidOperationException("Recovered relay is absent; its exit result is unverified.");
                }
                RecordRelayExit(process.ExitCode, process.ExitCode == 0 ? "ExitedSuccessfully" : "AbnormalExit");
                if (process.ExitCode != 0) throw new InvalidOperationException("Owned relay exited abnormally before cleanup.");
                ClearRelay();
                return;
            }
            if (!OperatingSystem.IsLinux() || process.StartTime.ToUniversalTime().Ticks != _state.RelayStartTicks ||
                !File.ReadAllText($"/proc/{process.Id}/cmdline").Contains(_state.RunId, StringComparison.Ordinal) ||
                !File.ReadAllText($"/proc/{process.Id}/cmdline").Contains(_state.FixtureId, StringComparison.Ordinal)) throw new InvalidDataException();
            var stopped = await _process.RunAsync("/bin/kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)], "relay-stop", TimeSpan.FromSeconds(5), token);
            if (stopped.ExitCode != 0 || stopped.TimedOut || !stopped.Complete) throw new InvalidDataException();
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            absenceVerified = true;
            if (_relay is null)
            {
                RecordRelayExit(null, "StoppedExitUnverified");
                throw new InvalidOperationException("Recovered relay stopped; its exit result is unverified.");
            }
            RecordRelayExit(process.ExitCode, process.ExitCode == 0 ? "StoppedSuccessfully" : "AbnormalExit");
            if (process.ExitCode != 0) throw new InvalidDataException();
            ClearRelay();
        }
        catch (ArgumentException) when (_relay is null)
        {
            absenceVerified = true;
            RecordRelayExit(null, "AbsentExitUnverified");
            throw new InvalidOperationException("Recorded relay is absent; its exit result is unverified.");
        }
        finally
        {
            _relayPipes?.Cancel();
            if (absenceVerified) File.Delete(Path.Combine(_directory, "relay-ready.json.tmp"));
            if (_relayOutput is not null)
            {
                try { await Task.WhenAll(_relayOutput, _relayError!).WaitAsync(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            if (_relay is null) process?.Dispose();
        }
    }

    private void RecordRelayExit(int? exitCode, string status)
    {
        // A later recovery cannot erase an already observed child exit result.
        if (exitCode is null && _state.RelayExitCode is not null) return;
        _state = _state with { RelayExitCode = exitCode, RelayExitStatus = status }; Persist();
    }

    private void ClearRelay()
    {
        _state = _state with { RelayPid = null, RelayStartTicks = null }; Persist();
    }

    private async Task<string> RequiredAsync(string[] arguments, string operation, TimeSpan timeout, CancellationToken token)
    {
        var result = await _process.RunAsync(_run.WslcPath, arguments, operation, timeout, token);
        if (result.ExitCode != 0 || result.TimedOut || !result.Complete) throw new InvalidOperationException("Owned fixture command failed; output remains private.");
        return result.Output;
    }

    private async Task<string> WindowsAsync(string script, string operation, CancellationToken token)
    {
        var code = "$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" + script;
        var result = await _process.RunAsync(_run.WindowsPowerShellPath,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(code))], operation, MetadataTimeout, token);
        if (result.ExitCode != 0 || result.TimedOut || !result.Complete) throw new InvalidDataException();
        return result.Output;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private void Persist()
    {
        var path = Path.Combine(_directory, "fixture.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(_state, WslcPostgreSqlJson.Default.WslcPostgreSqlState));
        File.Move(path + ".new", path, true);
    }
    private void MarkIncomplete()
    {
        _state = _state with { FailureCode = "CleanupIncomplete" };
        try
        {
            Persist();
            File.WriteAllText(Path.Combine(_run.LocalDirectory, ".cleanup-incomplete"), "Owned fixture cleanup requires reconciliation.");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task RecordFailureAsync(string name, Exception failure)
    {
        if (!_privateVerified) return;
        try { await File.WriteAllTextAsync(Path.Combine(_directory, name), failure.ToString()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static async Task RecoverRunAsync(WslcPostgreSqlRun run)
    {
        run.Validate();
        var attempted = new List<string>();
        var unreconciled = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(run.LocalDirectory))
        {
            attempted.Add(Path.GetFileName(directory));
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                    new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException();
                var file = Path.Combine(directory, "fixture.json");
                if (!File.Exists(file))
                {
                    if (Directory.EnumerateFiles(directory).Any()) throw new InvalidDataException();
                    continue;
                }
                var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(file), WslcPostgreSqlJson.Default.WslcPostgreSqlState)
                    ?? throw new InvalidDataException();
                if (Path.GetFileName(directory) != state.FixtureId) throw new InvalidDataException();
                var server = new WslcPostgreSqlServer(run, state);
                await server.DisposeAsync();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException) { unreconciled.Add(Path.GetFileName(directory)); }
        }
        await File.WriteAllTextAsync(Path.Combine(run.LocalDirectory, "supervisor-recovery-result.json"),
            JsonSerializer.Serialize(new(1, run.RunId, [.. attempted], [.. unreconciled]), WslcPostgreSqlJson.Default.WslcPostgreSqlRecoveryResult));
        if (unreconciled.Count != 0) throw new InvalidOperationException("Owned fixture run cleanup is incomplete; private records retained.");
    }
}

// Explicit test-supervisor entry point, never reflection into private test code.
public static class PostgreSqlFixtureRunCleanup
{
    public static Task RunAsync()
    {
        var run = WslcPostgreSqlRun.ReadEnvironment() ?? throw new InvalidOperationException("Owned fixture run required.");
        return WslcPostgreSqlServer.RecoverRunAsync(run);
    }
}
