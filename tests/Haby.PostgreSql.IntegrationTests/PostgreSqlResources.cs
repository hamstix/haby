using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.PostgreSql.IntegrationTests;

internal static class PostgreSqlResources
{
    private static readonly object Gate = new();
    private static int active, peak, attempts, ready, cleaned, startupFailed, cleanupFailed, databasesCreated, databasesDropped, capacityRefused;
    private static int controlActive, suitePeak, controlPeak;
    private static int readyActive, readyControlActive, readyPeak, readySuitePeak, readyControlPeak;
    private static int hostActive, hostPeak, hostRefused;
    internal static int Limit => Parse(Environment.GetEnvironmentVariable("HABY_TEST_POSTGRESQL_SERVERS"));
    internal static int HostLimit => ParseHost(Environment.GetEnvironmentVariable("HABY_TEST_HOST_GROUPS"), Limit);
    internal static int Active { get { lock (Gate) return active; } }
    internal static int HostActive { get { lock (Gate) return hostActive; } }

    internal static int ParseHost(string? value, int servers)
    {
        if (value is null) return Math.Min(2, servers - 1);
        if (value.Length != 1 || value[0] < '1' || value[0] > '7' || value[0] - '0' >= servers)
            throw new InvalidOperationException("HABY_TEST_HOST_GROUPS must be from 1 through server-limit minus 1.");
        return value[0] - '0';
    }

    internal static int Parse(string? value) => value switch
    {
        null => 4,
        "2" => 2,
        "3" => 3,
        "4" => 4,
        "5" => 5,
        "6" => 6,
        "7" => 7,
        "8" => 8,
        _ => throw new InvalidOperationException("HABY_TEST_POSTGRESQL_SERVERS must be an integer from 2 through 8.")
    };

    internal static void Reserve(bool control, bool heavyHost)
    {
        lock (Gate)
        {
            if (heavyHost && hostActive >= HostLimit)
            {
                hostRefused++; Write();
                throw new InvalidOperationException("Heavy Host group capacity exceeded; check collection ownership.");
            }
            // Fail closed if a new fixture bypasses collection ownership. Never wait
            // for another fixture's disposal while occupying an xUnit slot.
            if (active >= Limit)
            {
                capacityRefused++; Write();
                throw new InvalidOperationException("Docker fixture capacity exceeded; check collection ownership.");
            }
            active++; attempts++; peak = Math.Max(peak, active);
            if (heavyHost) { hostActive++; hostPeak = Math.Max(hostPeak, hostActive); }
            if (control) controlActive++;
            suitePeak = Math.Max(suitePeak, active - controlActive);
            controlPeak = Math.Max(controlPeak, controlActive);
            Write();
        }
    }
    internal static void Ready(bool control)
    {
        lock (Gate)
        {
            ready++; readyActive++; if (control) readyControlActive++;
            readyPeak = Math.Max(readyPeak, readyActive);
            readySuitePeak = Math.Max(readySuitePeak, readyActive - readyControlActive);
            readyControlPeak = Math.Max(readyControlPeak, readyControlActive);
            Write();
        }
    }
    internal static void StartupFailed() { lock (Gate) { startupFailed++; Write(); } }
    internal static void Cleaned(bool control, bool wasReady, bool heavyHost)
    {
        lock (Gate)
        {
            cleaned++; active--; if (control) controlActive--;
            if (heavyHost) hostActive--;
            if (wasReady) { readyActive--; if (control) readyControlActive--; }
            Write();
        }
    }
    internal static void CleanupFailed() { lock (Gate) { cleanupFailed++; Write(); } }
    internal static void DatabaseCreated() { lock (Gate) { databasesCreated++; Write(); } }
    internal static void DatabaseDropped() { lock (Gate) { databasesDropped++; Write(); } }

    private static void Write()
    {
        var directory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY");
        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            var value = new ResourceCounts(Limit, active, peak, attempts, ready, cleaned, startupFailed, cleanupFailed, databasesCreated, databasesDropped, capacityRefused, suitePeak, controlPeak,
                readyActive, readyPeak, readySuitePeak, readyControlPeak, HostLimit, hostActive, hostPeak, hostRefused);
            File.WriteAllText(Path.Combine(directory, "resources.json"), JsonSerializer.Serialize(value, ResourceJson.Default.ResourceCounts));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Allowlisted resource counts unavailable; output withheld."); }
    }
}

internal sealed record ResourceCounts(int Limit, int Active, int PeakReservedServers, int StartupAttempts,
    int Ready, int Cleaned, int StartupFailed, int CleanupFailed, int CatalogDatabasesCreated, int CatalogDatabasesDropped, int CapacityRefused,
    int PeakSuiteReservedServers, int PeakControlReservedServers,
    int ReadyActive, int PeakReadyServers, int PeakReadySuiteServers, int PeakReadyControlServers,
    int HostGroupLimit, int HostGroupsActive, int PeakHostGroups, int HostCapacityRefused);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResourceCounts))]
internal partial class ResourceJson : JsonSerializerContext;
