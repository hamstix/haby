using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.PostgreSql.IntegrationTests;

// Test instrumentation only. A finished scope is not proof its operation succeeded.
internal enum ProgressStatus { Recording, AssemblyFinished, UnknownCase, Overflow, LifecycleMismatch }
internal enum ProgressPhaseKind { ClusterReady, ClusterCleanup, CatalogCreate, HostStartMigrate }
internal sealed record ProgressCase(long Id, string Case);
internal sealed record ProgressPhase(long Id, ProgressPhaseKind Kind);
internal sealed record ProgressDocument(int SchemaVersion, string Source, string Ledger, long Sequence, ProgressStatus Status,
    long CasesStarted, long CasesFinished, long PhasesStarted, long PhasesExited,
    ProgressCase[] ActiveCases, ProgressCase[] RecentFinishedCases, ProgressPhase[] ActivePhases, ProgressPhase[] RecentExitedPhases,
    ResourceCounts? Resources);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProgressDocument))]
internal partial class ProgressJson : JsonSerializerContext;

internal sealed class PostgreSqlProgressJournal
{
    internal const int ActiveLimit = 64, RecentLimit = 32, ByteLimit = 65536;
    private const long CounterLimit = 1000000;
    private readonly object gate = new();
    private readonly HashSet<string> allowed;
    private readonly string source, ledger, path;
    private readonly Action<string>? beforeReplace;
    private readonly Dictionary<long, ProgressCase> cases = new();
    private readonly Dictionary<long, ProgressPhase> phases = new();
    private readonly Queue<ProgressCase> recentCases = new();
    private readonly Queue<ProgressPhase> recentPhases = new();
    private long sequence, started, finished, phaseStarted, phaseExited;
    private ProgressStatus status;
    private ResourceCounts? resources;

    internal PostgreSqlProgressJournal(string directory, string source, string ledger, IEnumerable<string> allowed,
        Action<string>? beforeReplace = null)
    {
        this.source = source; this.ledger = ledger; this.allowed = new(allowed, StringComparer.Ordinal);
        this.beforeReplace = beforeReplace; path = Path.Combine(directory, "progress.json");
        if (!Hex(source, 40) || !Hex(ledger, 64) || this.allowed.Count == 0 || this.allowed.Any(x => !Hex(x, 64)))
            throw new InvalidDataException("Progress identity unavailable.");
        Publish();
    }

    internal static string CaseHash(string method, string display) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + display))).ToLowerInvariant();

    internal long StartCase(string hash)
    {
        lock (gate)
        {
            if (status != ProgressStatus.Recording) return 0;
            if (!allowed.Contains(hash)) { Fault(ProgressStatus.UnknownCase); return 0; }
            if (cases.Count == ActiveLimit || started == CounterLimit) { Fault(ProgressStatus.Overflow); return 0; }
            var id = ++started; cases.Add(id, new(id, hash)); Publish(); return id;
        }
    }
    internal void FinishCase(long id)
    {
        lock (gate)
        {
            if (status != ProgressStatus.Recording) return;
            if (!cases.Remove(id, out var value)) { Fault(ProgressStatus.LifecycleMismatch); return; }
            finished++; recentCases.Enqueue(value); if (recentCases.Count > RecentLimit) recentCases.Dequeue(); Publish();
        }
    }
    internal long StartPhase(ProgressPhaseKind kind)
    {
        lock (gate)
        {
            if (status != ProgressStatus.Recording) return 0;
            if (!Enum.IsDefined(kind)) { Fault(ProgressStatus.LifecycleMismatch); return 0; }
            if (phases.Count == ActiveLimit || phaseStarted == CounterLimit) { Fault(ProgressStatus.Overflow); return 0; }
            var id = ++phaseStarted; phases.Add(id, new(id, kind)); Publish(); return id;
        }
    }
    internal void ExitPhase(long id)
    {
        lock (gate)
        {
            if (status != ProgressStatus.Recording) return;
            if (!phases.Remove(id, out var value)) { Fault(ProgressStatus.LifecycleMismatch); return; }
            phaseExited++; recentPhases.Enqueue(value); if (recentPhases.Count > RecentLimit) recentPhases.Dequeue(); Publish();
        }
    }
    internal void ResourceSnapshot(ResourceCounts value) { lock (gate) { resources = value; Publish(); } }
    internal void AssemblyFinished()
    {
        lock (gate)
        {
            if (status != ProgressStatus.Recording) return;
            status = cases.Count == 0 && phases.Count == 0 ? ProgressStatus.AssemblyFinished : ProgressStatus.LifecycleMismatch;
            Publish();
        }
    }
    private void Fault(ProgressStatus value) { status = value; Publish(); }
    private void Publish()
    {
        try
        {
            if (sequence == CounterLimit) return;
            var value = new ProgressDocument(1, source, ledger, ++sequence, status, started, finished, phaseStarted, phaseExited,
                cases.Values.OrderBy(x => x.Id).ToArray(), recentCases.ToArray(), phases.Values.OrderBy(x => x.Id).ToArray(), recentPhases.ToArray(), resources);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProgressJson.Default.ProgressDocument);
            _ = Read(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            beforeReplace?.Invoke(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine("Bounded PostgreSQL progress unavailable; primary result retained."); }
    }
    internal static ProgressDocument ReadFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > ByteLimit) throw new InvalidDataException("Progress exceeds bound.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Progress changed beyond bound.");
        return Read(bytes);
    }
    internal static ProgressDocument Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ByteLimit) throw new InvalidDataException("Progress exceeds bound.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 12 });
        static void Unique(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in node.EnumerateObject())
                { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate progress field."); Unique(property.Value); }
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Unique(child);
        }
        Unique(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 14)
            throw new InvalidDataException("Progress shape unavailable.");
        var value = JsonSerializer.Deserialize(bytes, ProgressJson.Default.ProgressDocument) ?? throw new InvalidDataException("Progress missing.");
        if (value.SchemaVersion != 1 || !Hex(value.Source, 40) || !Hex(value.Ledger, 64) || !Enum.IsDefined(value.Status) ||
            value.Sequence is < 1 or > CounterLimit || value.CasesStarted is < 0 or > CounterLimit || value.CasesFinished < 0 || value.CasesFinished > value.CasesStarted ||
            value.PhasesStarted is < 0 or > CounterLimit || value.PhasesExited < 0 || value.PhasesExited > value.PhasesStarted ||
            value.ActiveCases is null || value.RecentFinishedCases is null || value.ActivePhases is null || value.RecentExitedPhases is null ||
            value.ActiveCases.Length > ActiveLimit || value.ActivePhases.Length > ActiveLimit || value.RecentFinishedCases.Length > RecentLimit || value.RecentExitedPhases.Length > RecentLimit)
            throw new InvalidDataException("Progress fields invalid.");
        if (value.ActiveCases.Concat(value.RecentFinishedCases).Any(x => x is null || x.Id < 1 || x.Id > value.CasesStarted || !Hex(x.Case, 64)) ||
            value.ActivePhases.Concat(value.RecentExitedPhases).Any(x => x is null || x.Id < 1 || x.Id > value.PhasesStarted || !Enum.IsDefined(x.Kind)) ||
            value.ActiveCases.Concat(value.RecentFinishedCases).Select(x => x.Id).Distinct().Count() != value.ActiveCases.Length + value.RecentFinishedCases.Length ||
            value.ActivePhases.Concat(value.RecentExitedPhases).Select(x => x.Id).Distinct().Count() != value.ActivePhases.Length + value.RecentExitedPhases.Length ||
            value.CasesStarted - value.CasesFinished != value.ActiveCases.Length || value.PhasesStarted - value.PhasesExited != value.ActivePhases.Length ||
            value.Status == ProgressStatus.AssemblyFinished && (value.ActiveCases.Length != 0 || value.ActivePhases.Length != 0))
            throw new InvalidDataException("Progress lifecycle invalid.");
        if (value.Resources is { } resource)
        {
            int[] counters = [resource.Limit, resource.Active, resource.PeakReservedServers, resource.StartupAttempts, resource.Ready, resource.Cleaned,
                resource.StartupFailed, resource.CleanupFailed, resource.CatalogDatabasesCreated, resource.CatalogDatabasesDropped, resource.CapacityRefused,
                resource.PeakSuiteReservedServers, resource.PeakControlReservedServers, resource.ReadyActive, resource.PeakReadyServers, resource.PeakReadySuiteServers,
                resource.PeakReadyControlServers, resource.HostGroupLimit, resource.HostGroupsActive, resource.PeakHostGroups, resource.HostCapacityRefused];
            if (counters.Any(x => x < 0 || x > CounterLimit) || resource.Limit is < 2 or > 8 || resource.Active > resource.Limit ||
                resource.HostGroupLimit < 1 || resource.HostGroupLimit >= resource.Limit || resource.HostGroupsActive > resource.HostGroupLimit ||
                resource.PeakReservedServers > resource.Limit || resource.PeakSuiteReservedServers > resource.Limit || resource.PeakControlReservedServers > resource.Limit ||
                resource.PeakReadyServers > resource.Limit || resource.PeakReadySuiteServers > resource.Limit || resource.PeakReadyControlServers > resource.Limit ||
                resource.PeakHostGroups > resource.HostGroupLimit || resource.ReadyActive > resource.Active || resource.ReadyActive > resource.Ready ||
                resource.Cleaned > resource.StartupAttempts || resource.Ready > resource.StartupAttempts || resource.StartupFailed > resource.StartupAttempts ||
                resource.Active != resource.StartupAttempts - resource.Cleaned || resource.CatalogDatabasesDropped > resource.CatalogDatabasesCreated)
                throw new InvalidDataException("Progress resource counters invalid.");
        }
        return value;
    }
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class PostgreSqlProgress
{
    private static readonly Lazy<PostgreSqlProgressJournal?> Journal = new(Create);
    internal static PostgreSqlProgressJournal? Current => Journal.Value;
    internal static (string Ledger, HashSet<string> Allowed) Identity()
    {
        using var stream = typeof(PostgreSqlProgress).Assembly.GetManifestResourceStream("Haby.PostgreSql.IntegrationTests.progress-ledger.json")
            ?? throw new InvalidDataException("Progress ledger unavailable.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        using var json = JsonDocument.Parse(bytes);
        var allowed = json.RootElement.GetProperty("entries"u8).EnumerateArray().Where(x => x.GetProperty("gate"u8).GetString() == "postgresql-integration")
            .Select(x => x.GetProperty("case"u8).GetString()!).ToHashSet(StringComparer.Ordinal);
        return (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), allowed);
    }
    private static PostgreSqlProgressJournal? Create()
    {
        var directory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY");
        var source = Environment.GetEnvironmentVariable("HABY_TEST_PROGRESS_SOURCE");
        if (directory is null || source is null) return null;
        try
        {
            var identity = Identity();
            return new(directory, source, identity.Ledger, identity.Allowed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        { Console.Error.WriteLine("Bounded PostgreSQL progress identity unavailable; output withheld."); return null; }
    }
}
