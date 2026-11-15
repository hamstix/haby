namespace Haby.Persistence.PostgreSql;

internal static class RolloutDeadlines
{
    internal static bool Expired(DateTimeOffset now, DateTimeOffset deadline, bool retainedExpiry) => retainedExpiry || now >= deadline;
    internal static bool CanReadActive(TimeProvider clock, long started, TimeSpan budget, DateTimeOffset deadline) =>
        clock.GetUtcNow() < deadline && clock.GetElapsedTime(started) < budget;
}
