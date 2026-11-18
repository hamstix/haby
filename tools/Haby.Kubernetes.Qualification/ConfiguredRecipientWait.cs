using Haby.Application;

internal static class ConfiguredRecipientWait
{
    // The caller owns one absolute budget shared with both normal lifecycle waits.
    // Observations never renew that budget or submit a lifecycle operation.
    internal static async Task UntilAbsentAsync(Func<CancellationToken, Task<RecipientObservation>> observe,
        Func<RecipientObservation, bool> validate, int maximumRounds, int pollMilliseconds,
        Action expired, CancellationToken token)
    {
        try
        {
            for (var round = 0; round < maximumRounds; round++)
            {
                token.ThrowIfCancellationRequested();
                var sample = await observe(token);
                token.ThrowIfCancellationRequested();
                if (sample.Absent && validate(sample)) return;
                if (round + 1 < maximumRounds) await Task.Delay(pollMilliseconds, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { expired(); throw; }
        expired();
        throw new TimeoutException("Complete previous recipient absence was not observed within the fixture bound.");
    }
}
