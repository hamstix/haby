using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.PostgreSql.IntegrationTests;

internal sealed record WslcPostgreSqlRun(int FormatVersion, string RunId, string LocalDirectory,
    string CliDirectory, string WslcPath, string WindowsPowerShellPath, string HostAddress,
    bool UseLoopbackRelay, string PythonPath)
{
    internal static WslcPostgreSqlRun? ReadEnvironment()
    {
        var mode = Environment.GetEnvironmentVariable("HABY_POSTGRESQL_EPHEMERAL_EXTERNAL");
        var oldConnection = Environment.GetEnvironmentVariable("HABY_POSTGRESQL_EPHEMERAL_CONNECTION");
        var oldContainer = Environment.GetEnvironmentVariable("HABY_POSTGRESQL_EPHEMERAL_CONTAINER");
        if (oldConnection is not null || oldContainer is not null)
            throw new InvalidOperationException("Shared external PostgreSQL configuration is obsolete. Create an owned fixture run; supplied targets are refused.");
        var path = Environment.GetEnvironmentVariable("HABY_POSTGRESQL_EPHEMERAL_RUN");
        if (mode is null or "0")
        {
            if (path is not null) throw new InvalidOperationException("Owned fixture configuration requires explicit external opt-in.");
            return null;
        }
        if (mode != "1" || string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Owned fixture run configuration is required.");
        try
        {
            if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 16384) throw new InvalidDataException();
            var run = JsonSerializer.Deserialize(File.ReadAllText(path), WslcPostgreSqlJson.Default.WslcPostgreSqlRun)
                ?? throw new InvalidDataException();
            run.Validate();
            return run;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            throw new InvalidOperationException("Owned fixture configuration is invalid; supplied values withheld.");
        }
    }

    internal void Validate()
    {
        if (FormatVersion != 1 || !Guid.TryParseExact(RunId, "N", out _) ||
            !Path.IsPathFullyQualified(LocalDirectory) || !Directory.Exists(LocalDirectory) ||
            Path.GetFileName(Path.TrimEndingDirectorySeparator(LocalDirectory)) != RunId ||
            File.ReadAllText(Path.Combine(LocalDirectory, ".run-id")).Trim() != RunId ||
            string.IsNullOrWhiteSpace(WslcPath) || string.IsNullOrWhiteSpace(WindowsPowerShellPath) ||
            !System.Net.IPAddress.TryParse(HostAddress, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            HostAddress is "0.0.0.0" or "255.255.255.255" ||
            !PrivateAddress(address) ||
            OperatingSystem.IsWindows() && (UseLoopbackRelay || HostAddress != "127.0.0.1") ||
            !OperatingSystem.IsWindows() && (!OperatingSystem.IsLinux() || !UseLoopbackRelay || string.IsNullOrWhiteSpace(PythonPath)))
            throw new InvalidDataException();
        if (!System.Text.RegularExpressions.Regex.IsMatch(CliDirectory, @"\A[A-Za-z]:[\\/].+[\\/]" + RunId + @"\z"))
            throw new InvalidDataException();
    }

    private static bool PrivateAddress(System.Net.IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] is 127 or 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168);
    }

    internal string CliPath(string fixture, string name) => CliDirectory.TrimEnd('\\', '/') + "\\" + fixture + "\\" + name;
}

internal sealed record WslcPostgreSqlState(int FormatVersion, string RunId, string FixtureId,
    string Name, string VolumeName, string HostAddress, int HostPort, string? ContainerId = null,
    bool VolumeAttempted = false, bool VolumeConclusive = false, bool StartAttempted = false, bool StartConclusive = false,
    int? RelayPid = null, long? RelayStartTicks = null, bool Ready = false, bool Cleaned = false,
    string? FailureCode = null, string? SystemIdentifier = null,
    int? RelayExitCode = null, string? RelayExitStatus = null);

internal sealed record WslcPostgreSqlRecoveryResult(int FormatVersion, string RunId,
    string[] AttemptedFixtureDirectories, string[] UnreconciledFixtureDirectories);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true)]
[JsonSerializable(typeof(WslcPostgreSqlRun))]
[JsonSerializable(typeof(WslcPostgreSqlState))]
[JsonSerializable(typeof(WslcPostgreSqlRecoveryResult))]
internal sealed partial class WslcPostgreSqlJson : JsonSerializerContext;
