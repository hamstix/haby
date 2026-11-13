namespace Haby.Tests;

internal static class DeploymentReplacementObservation
{
    internal static async Task WaitAsync(string originalUid, Func<CancellationToken, Task<string?>> readUid,
        Func<CancellationToken, Task> pause, CancellationToken token)
    {
        // Match the existing stateless replacement observation budget without extending
        // the caller's deadline. Only a NotFound-derived null permits continuation.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var uid = await readUid(token);
            token.ThrowIfCancellationRequested();
            if (uid is null) return;
            if (!string.Equals(uid, originalUid, StringComparison.Ordinal))
                throw new InvalidOperationException("Replacement fixture identity changed before deletion.");
            if (attempt < 49) await pause(token);
        }
        throw new TimeoutException("Original Deployment absence was not observed within the fixture read budget.");
    }
}
