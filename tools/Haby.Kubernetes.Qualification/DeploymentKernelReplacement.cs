using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

// Only the K14/K24 qualification fixtures, never a production recovery path.
internal static class DeploymentKernelReplacement
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    // At most one immediate read plus one per interval within the existing 150s parent budget.
    internal const int MaximumReads = 1 + 150_000 / 250;

    internal static async Task RunAsync(Kubernetes admin, string namespaceName, string name, string originalUid,
        V1Deployment replacement, CancellationToken token, Action<string, string> phase, Action<bool, int> observation,
        Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        token.ThrowIfCancellationRequested();
        phase("uid-delete", "start");
        await admin.AppsV1.DeleteNamespacedDeploymentAsync(name, namespaceName,
            new V1DeleteOptions { Preconditions = new() { Uid = originalUid }, PropagationPolicy = "Orphan" }, cancellationToken: token);
        phase("uid-delete", "completed");
        phase("absence-poll", "start");
        var absent = false; var attempts = 0;
        // Consume the remaining scenario token; do not start a fresh wait deadline.
        try
        {
            for (var n = 0; n < MaximumReads; n++)
            {
                token.ThrowIfCancellationRequested();
                attempts++;
                V1Deployment current;
                try
                {
                    current = await admin.AppsV1.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: token);
                }
                catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound)
                { absent = true; break; }
                token.ThrowIfCancellationRequested();
                if (!string.Equals(current.Metadata?.Uid, originalUid, StringComparison.Ordinal))
                    throw new InvalidOperationException("Replacement fixture identity changed before deletion.");
                if (attempts < MaximumReads)
                    await (pause?.Invoke(PollInterval, token) ?? Task.Delay(PollInterval, token));
            }
            if (!absent) throw new InvalidOperationException("Original Deployment absence was not confirmed within the parent read bound.");
        }
        catch
        {
            // Diagnostic failure must not replace the original HTTP/cancellation/identity error.
            try { observation(absent, attempts); } catch { }
            throw;
        }
        observation(absent, attempts);
        token.ThrowIfCancellationRequested();
        phase("replacement-create", "start");
        token.ThrowIfCancellationRequested();
        await admin.AppsV1.CreateNamespacedDeploymentAsync(replacement, namespaceName, cancellationToken: token);
        phase("replacement-create", "completed");
    }
    internal static string FailureEvidence(string fixture, string phase, Exception failure)
    {
        var category = failure switch
        {
            HttpOperationException => "http",
            OperationCanceledException => "canceled",
            NullReferenceException => "null-reference",
            Haby.Kubernetes.Contracts.WorkloadContractException => "contract",
            InvalidOperationException => "invalid-operation",
            _ => "unknown"
        };
        var status = failure is HttpOperationException http ? (int?)http.Response?.StatusCode : null;
        var safeStatus = status is >= 100 and <= 599 ? status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
        return $"Q243 kernel {fixture} {phase} exception {category} {safeStatus}";
    }
}
