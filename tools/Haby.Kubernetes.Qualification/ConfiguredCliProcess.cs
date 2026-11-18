// Configured-fixture process ownership only. Cancellation never counts as a
// completed CLI call, even if owned termination and output draining succeed.
internal static class ConfiguredCliProcess
{
    internal sealed class Incomplete(string reason, Exception? primary = null) : IOException("Configured CLI " + reason + " incomplete.", primary)
    {
        internal string Reason => reason;
    }
    internal static async Task<T> WithOwnedConfigurationAsync<T>(Func<Task<T>> work, Action cleanup, Action complete)
    {
        T result = default!;
        Exception? primary = null;
        try { result = await work(); }
        catch (Exception error) { primary = error; }
        try { cleanup(); }
        catch (Exception error)
        {
            if (primary is null) throw;
            throw ConfiguredLifecycle.Combine(primary, [error]);
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        complete();
        return result;
    }
    internal static async Task<(string Output, string Error)> CollectAsync(Task exited, Task<string> output, Task<string> error,
        Action killOwnedTree, CancellationToken token, TimeProvider clock)
    {
        try { await exited.WaitAsync(token); }
        catch (Exception primary)
        {
            try { killOwnedTree(); }
            catch { throw new Incomplete("Termination", primary); }
            using var tail = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            try { await exited.WaitAsync(tail.Token); }
            catch { throw new Incomplete("Termination", primary); }
            try { await Task.WhenAll(output, error).WaitAsync(tail.Token); }
            catch { throw new Incomplete("Output", primary); }
            throw;
        }
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var outputBudget = CancellationTokenSource.CreateLinkedTokenSource(token, drain.Token);
        try { await Task.WhenAll(output, error).WaitAsync(outputBudget.Token); }
        catch (Exception primary)
        {
            try { killOwnedTree(); }
            catch { throw new Incomplete("Termination", primary); }
            throw new Incomplete("Output", primary);
        }
        token.ThrowIfCancellationRequested();
        return (await output, await error);
    }
}
