using Haby.PostgreSql.IntegrationTests;

// Synthetic reporting control only: no database, Host, Kubernetes or network.
internal static class PostgreSqlProgressControls
{
    private const string Case = "fe7a4fc727e325c637598371e2ab801d2c400e1d101b34a9bd6202b724e07916";
    internal static async Task Run()
    {
        var journal = PostgreSqlProgress.Current ?? throw new InvalidDataException("Progress control identity unavailable.");
        if (journal.StartCase(Case) == 0) throw new InvalidDataException("Progress control case unavailable.");
        using var cleanup = TestTiming.ClusterCleanup();
        // Exercise the existing numeric observer hooks in this isolated child only.
        // These are synthetic counters: no container/server is created by this control.
        PostgreSqlResources.Reserve(control: false, heavyHost: false);
        PostgreSqlResources.Ready(control: false);
        Console.WriteLine("PROGRESS-CONTROL-READY");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    internal static void Verify()
    {
        var directory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY") ?? throw new InvalidDataException("Progress control directory unavailable.");
        var value = PostgreSqlProgressJournal.ReadFile(Path.Combine(directory, "progress.json"));
        var identity = PostgreSqlProgress.Identity();
        if (value.Source != Environment.GetEnvironmentVariable("HABY_TEST_PROGRESS_SOURCE") || value.Ledger != identity.Ledger ||
            value.ActiveCases.Concat(value.RecentFinishedCases).Any(x => !identity.Allowed.Contains(x.Case)) ||
            value.Status != ProgressStatus.Recording || value.CasesStarted != 1 || value.CasesFinished != 0 || value.ActiveCases.Length != 1 ||
            value.ActiveCases[0].Case != Case || value.PhasesStarted != 1 || value.PhasesExited != 0 || value.ActivePhases.Length != 1 ||
            value.ActivePhases[0].Kind != ProgressPhaseKind.ClusterCleanup || value.Resources is not { Active: 1, ReadyActive: 1 })
            throw new InvalidDataException("Progress interruption proof unavailable.");
        Console.WriteLine("PROGRESS-CONTROL-PARTIAL-VERIFIED");
    }
}
