using System.Diagnostics;
using System.Globalization;

namespace Haby.PostgreSql.IntegrationTests;

// Test-only allowlisted phase measurements. Never accept identifiers, SQL or values.
internal sealed class TestTiming : IDisposable
{
    private static readonly object Gate = new();
    private readonly string _phase;
    private readonly Stopwatch _timer = Stopwatch.StartNew();

    private TestTiming(string phase) => _phase = phase;

    public static TestTiming ClusterReady() => new("cluster-ready");
    public static TestTiming ClusterCleanup() => new("cluster-cleanup");
    public static TestTiming CatalogCreate() => new("operator-catalog-create");
    public static TestTiming HostStartMigrate() => new("operator-host-start-migrate");

    public void Dispose()
    {
        var directory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY");
        if (directory is null) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "setup.csv"),
                    _phase + "," + _timer.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Measurement must never replace a primary test/cleanup failure.
                Console.Error.WriteLine("Test setup timing unavailable; output withheld.");
            }
        }
    }
}
