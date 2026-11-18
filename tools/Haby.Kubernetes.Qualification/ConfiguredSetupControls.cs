using Haby.PostgreSql.IntegrationTests;
using Xunit;

// No database/API is opened: only the real post-initialization setup path is exercised.
internal static class ConfiguredSetupControls
{
    private sealed class CleanupFailure : Exception;
    private sealed class Fixture(CancellationTokenSource cancellation, bool cancel, bool cleanupFailure) : PostgreSqlFixture, IAsyncLifetime
    {
        internal int Initialized, Disposed;
        Task IAsyncLifetime.InitializeAsync()
        {
            Initialized++;
            if (cancel) cancellation.Cancel();
            return Task.CompletedTask;
        }
        Task IAsyncLifetime.DisposeAsync()
        {
            Disposed++;
            return cleanupFailure ? Task.FromException(new CleanupFailure()) : Task.CompletedTask;
        }
    }

    internal static async Task Run()
    {
        await ConfiguredSubmissionProbeControls.Run();
        var failures = 0;
        foreach (var cancel in new[] { false, true })
        foreach (var cleanupFailure in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "haby223-setup-control-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "pod-host-address"), "192.0.2.2");
                await File.WriteAllTextAsync(Path.Combine(directory, "consumer-image"), "fixture-only");
                if (!cancel) Directory.CreateDirectory(Path.Combine(directory, "delivery.pfx"));
                using var cancellation = new CancellationTokenSource();
                var fixture = new Fixture(cancellation, cancel, cleanupFailure);
                Exception? observed = null;
                using var lifetime = new ConfiguredLifecycle(cancellation.Token);
                try { await ConfiguredDeploymentCases.Run(null!, new Uri("https://192.0.2.3/"), directory, lifetime, fixture); }
                catch (Exception error) { observed = error; }
                var errors = observed is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() : observed is null ? [] : new[] { observed };
                var hasPrimary = errors.Count(error => cancel ? error is OperationCanceledException : error is UnauthorizedAccessException or IOException) == 1;
                var hasCleanup = errors.Count(error => error is CleanupFailure) == (cleanupFailure ? 1 : 0);
                var passed = fixture.Initialized == 1 && fixture.Disposed == 1 && hasPrimary && hasCleanup && errors.Length == (cleanupFailure ? 2 : 1);
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} configured-setup-control cancel={cancel}; cleanup-failure={cleanupFailure}; initialized={fixture.Initialized}; disposed={fixture.Disposed}; primary={hasPrimary}; cleanup={hasCleanup}");
                if (!passed) failures++;
            }
            finally { Directory.Delete(directory, true); }
        }
        if (failures != 0) throw new InvalidOperationException("Configured setup lifetime controls failed.");
    }
}
