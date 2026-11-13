using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.PostgreSql.IntegrationTests;

internal static class PostgreSqlResources
{
    private static readonly object Gate = new();
    private static int active, peak, attempts, ready, cleaned, startupFailed, cleanupFailed, databasesCreated, databasesDropped, capacityRefused;
    private static int controlActive, suitePeak, controlPeak;
    private static int readyActive, readyControlActive, readyPeak, readySuitePeak, readyControlPeak;
    internal static int Limit => Parse(Environment.GetEnvironmentVariable("HABY_TEST_POSTGRESQL_SERVERS"));
    internal static int Active { get { lock (Gate) return active; } }

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

    internal static void Reserve(bool control)
    {
        lock (Gate)
        {
            // Fail closed if a new fixture bypasses collection ownership. Never wait
            // for another fixture's disposal while occupying an xUnit slot.
            if (active >= Limit)
            {
                capacityRefused++; Write();
                throw new InvalidOperationException("Docker fixture capacity exceeded; check collection ownership.");
            }
            active++; attempts++; peak = Math.Max(peak, active);
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
    internal static void Cleaned(bool control, bool wasReady)
    {
        lock (Gate)
        {
            cleaned++; active--; if (control) controlActive--;
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
                readyActive, readyPeak, readySuitePeak, readyControlPeak);
            File.WriteAllText(Path.Combine(directory, "resources.json"), JsonSerializer.Serialize(value, ResourceJson.Default.ResourceCounts));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Allowlisted resource counts unavailable; output withheld."); }
    }
}

internal sealed record ResourceCounts(int Limit, int Active, int PeakReservedServers, int StartupAttempts,
    int Ready, int Cleaned, int StartupFailed, int CleanupFailed, int CatalogDatabasesCreated, int CatalogDatabasesDropped, int CapacityRefused,
    int PeakSuiteReservedServers, int PeakControlReservedServers,
    int ReadyActive, int PeakReadyServers, int PeakReadySuiteServers, int PeakReadyControlServers);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResourceCounts))]
internal partial class ResourceJson : JsonSerializerContext;
