using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class PostgreSqlProgressTests
{
    private const string Source = "1111111111111111111111111111111111111111";
    private static readonly string Hash = new('a', 64), Ledger = new('b', 64);
    private sealed class OwnedDirectory : IDisposable
    {
        internal readonly string Path = Directory.CreateTempSubdirectory("haby-progress-").FullName;
        internal byte[] Bytes => File.ReadAllBytes(System.IO.Path.Combine(Path, "progress.json"));
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void Interrupted_case_and_fixture_scope_remain_started_without_fabricated_completion()
    {
        using var directory = new OwnedDirectory();
        var journal = new PostgreSqlProgressJournal(directory.Path, Source, Ledger, [Hash]);
        var first = journal.StartCase(Hash); var phase = journal.StartPhase(ProgressPhaseKind.ClusterCleanup);
        var interrupted = PostgreSqlProgressJournal.Read(directory.Bytes);
        Assert.Equal(ProgressStatus.Recording, interrupted.Status);
        Assert.Equal(1, interrupted.CasesStarted); Assert.Equal(0, interrupted.CasesFinished);
        Assert.Equal(first, Assert.Single(interrupted.ActiveCases).Id);
        Assert.Equal(phase, Assert.Single(interrupted.ActivePhases).Id); Assert.Equal(0, interrupted.PhasesExited);
        // No end/assembly callback simulates interruption. Bytes already exist atomically.
        Assert.Equal(interrupted, PostgreSqlProgressJournal.Read(directory.Bytes) with
        {
            ActiveCases = interrupted.ActiveCases,
            ActivePhases = interrupted.ActivePhases,
            RecentFinishedCases = interrupted.RecentFinishedCases,
            RecentExitedPhases = interrupted.RecentExitedPhases
        });
        journal.ExitPhase(phase); journal.FinishCase(first); journal.AssemblyFinished();
        var ended = PostgreSqlProgressJournal.Read(directory.Bytes);
        Assert.Equal(ProgressStatus.AssemblyFinished, ended.Status); Assert.Empty(ended.ActiveCases); Assert.Empty(ended.ActivePhases);
        Assert.Single(ended.RecentFinishedCases); Assert.Single(ended.RecentExitedPhases);
        Assert.Null(ended.Resources); // Assembly events do not manufacture fixture cleanup.
    }

    [Fact]
    public void Unknown_case_overflow_and_duplicate_completion_fail_closed_and_remain_bounded()
    {
        using var unknown = new OwnedDirectory();
        var journal = new PostgreSqlProgressJournal(unknown.Path, Source, Ledger, [Hash]);
        Assert.Equal(0, journal.StartCase(new string('c', 64)));
        Assert.Equal(ProgressStatus.UnknownCase, PostgreSqlProgressJournal.Read(unknown.Bytes).Status);
        using var overflow = new OwnedDirectory(); journal = new(overflow.Path, Source, Ledger, [Hash]);
        for (var i = 0; i < PostgreSqlProgressJournal.ActiveLimit; i++) Assert.NotEqual(0, journal.StartCase(Hash));
        Assert.Equal(0, journal.StartCase(Hash)); var full = PostgreSqlProgressJournal.Read(overflow.Bytes);
        Assert.Equal(ProgressStatus.Overflow, full.Status); Assert.Equal(64, full.ActiveCases.Length);
        Assert.True(overflow.Bytes.Length < PostgreSqlProgressJournal.ByteLimit);
        using var duplicate = new OwnedDirectory(); journal = new(duplicate.Path, Source, Ledger, [Hash]);
        var id = journal.StartCase(Hash); journal.FinishCase(id); journal.FinishCase(id);
        Assert.Equal(ProgressStatus.LifecycleMismatch, PostgreSqlProgressJournal.Read(duplicate.Bytes).Status);
        using var ring = new OwnedDirectory(); journal = new(ring.Path, Source, Ledger, [Hash]);
        for (var i = 0; i < 80; i++) journal.FinishCase(journal.StartCase(Hash));
        var rolled = PostgreSqlProgressJournal.Read(ring.Bytes);
        Assert.Equal(80, rolled.CasesStarted); Assert.Equal(80, rolled.CasesFinished);
        Assert.Equal(32, rolled.RecentFinishedCases.Length); Assert.Equal(49, rolled.RecentFinishedCases[0].Id);
        for (var i = 0; i < 80; i++) journal.ExitPhase(journal.StartPhase(ProgressPhaseKind.ClusterCleanup));
        for (var i = 0; i < 64; i++) { journal.StartCase(Hash); journal.StartPhase(ProgressPhaseKind.ClusterReady); }
        var maximum = PostgreSqlProgressJournal.Read(ring.Bytes);
        Assert.Equal(64, maximum.ActiveCases.Length); Assert.Equal(64, maximum.ActivePhases.Length);
        Assert.Equal(32, maximum.RecentFinishedCases.Length); Assert.Equal(32, maximum.RecentExitedPhases.Length);
        Assert.True(ring.Bytes.Length < PostgreSqlProgressJournal.ByteLimit);
        Assert.Equal(0, journal.StartPhase(ProgressPhaseKind.ClusterCleanup));
        Assert.Equal(ProgressStatus.Overflow, PostgreSqlProgressJournal.Read(ring.Bytes).Status);
    }

    [Fact]
    public void Strict_progress_schema_rejects_unknown_fields_codes_hashes_and_inconsistent_counters()
    {
        using var directory = new OwnedDirectory();
        var journal = new PostgreSqlProgressJournal(directory.Path, Source, Ledger, [Hash]); journal.StartCase(Hash);
        var valid = Encoding.UTF8.GetString(directory.Bytes);
        foreach (var changed in new Func<JsonObject, JsonObject>[]
        {
            x => { x["protected-secret"] = "protected-secret"; return x; },
            x => { x["status"] = 99; return x; }, x => { x["casesStarted"] = -1; return x; },
            x => { x["casesStarted"] = 1000001; return x; },
            x => { x["sequence"] = 1.5; return x; }, x => { x["activeCases"]![0]!["case"] = "protected-secret"; return x; },
            x => { x["casesFinished"] = 1; return x; }, x => { x["activeCases"]![0]!["extra"] = true; return x; }
        })
            Assert.ThrowsAny<Exception>(() => PostgreSqlProgressJournal.Read(Encoding.UTF8.GetBytes(changed(JsonNode.Parse(valid)!.AsObject()).ToJsonString())));
        Assert.ThrowsAny<Exception>(() => PostgreSqlProgressJournal.Read(Encoding.UTF8.GetBytes(valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"))));
        Assert.ThrowsAny<Exception>(() => PostgreSqlProgressJournal.Read(new byte[PostgreSqlProgressJournal.ByteLimit + 1]));
    }

    [Fact]
    public void Failure_before_atomic_replace_preserves_last_complete_file_and_withholds_protected_message()
    {
        using var directory = new OwnedDirectory(); var fail = false;
        var journal = new PostgreSqlProgressJournal(directory.Path, Source, Ledger, [Hash], _ =>
        { if (fail) throw new IOException("protected-password-connection-SQL"); });
        var retained = directory.Bytes; fail = true;
        Assert.NotEqual(0, journal.StartCase(Hash)); Assert.Equal(retained, directory.Bytes);
        Assert.DoesNotContain("protected-password", Encoding.UTF8.GetString(directory.Bytes));
        Assert.Equal(0, PostgreSqlProgressJournal.Read(retained).CasesStarted);
        fail = false; journal.StartPhase(ProgressPhaseKind.ClusterReady);
        var recovered = PostgreSqlProgressJournal.Read(directory.Bytes);
        Assert.Single(recovered.ActiveCases); Assert.Single(recovered.ActivePhases);
        Assert.True(recovered.Sequence > PostgreSqlProgressJournal.Read(retained).Sequence);
    }

    [Fact]
    public void Message_observer_forwards_exact_runner_result_and_retains_only_case_hash()
    {
        using var directory = new OwnedDirectory();
        var method = new TestMethod(new TestClass(new TestCollection(new TestAssembly(Reflector.Wrap(typeof(PostgreSqlProgressTests).Assembly), null, null),
            null, "protected-collection"), Reflector.Wrap(typeof(PostgreSqlProgressTests))),
            Reflector.Wrap(typeof(PostgreSqlProgressTests).GetMethod(nameof(Message_observer_forwards_exact_runner_result_and_retains_only_case_hash))!));
        var testCase = new XunitTestCase(new Sink(true), TestMethodDisplay.ClassAndMethod, TestMethodDisplayOptions.None, method);
        var test = new XunitTest(testCase, "protected-display-arguments");
        var fullMethod = typeof(PostgreSqlProgressTests).FullName + "." + method.Method.Name;
        var hash = PostgreSqlProgressJournal.CaseHash(fullMethod, test.DisplayName);
        var journal = new PostgreSqlProgressJournal(directory.Path, Source, Ledger, [hash]); var inner = new Sink(false);
        var observer = new ProgressMessageSink(inner, journal); var start = new TestStarting(test);
        Assert.False(observer.OnMessage(start)); Assert.Same(start, inner.Last);
        Assert.Equal(hash, Assert.Single(PostgreSqlProgressJournal.Read(directory.Bytes).ActiveCases).Case);
        var finish = new TestFinished(test, 0, "protected-stdout-exception-connection");
        Assert.False(observer.OnMessage(finish)); Assert.Same(finish, inner.Last);
        Assert.Empty(PostgreSqlProgressJournal.Read(directory.Bytes).ActiveCases);
        Assert.DoesNotContain("protected", Encoding.UTF8.GetString(directory.Bytes)); Assert.Equal(2, inner.Count);
    }
    private sealed class Sink(bool result) : LongLivedMarshalByRefObject, IMessageSink
    {
        internal IMessageSinkMessage? Last; internal int Count;
        public bool OnMessage(IMessageSinkMessage message) { Last = message; Count++; return result; }
    }
}
