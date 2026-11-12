using System.Diagnostics;
using System.Text;

namespace Haby.PostgreSql.IntegrationTests;

// Only the fixture's short-lived CLI clients are terminated here, never WSL,
// its daemon, an arbitrary process tree or a PostgreSQL server process.
internal class WslcPostgreSqlProcess(string privateDirectory)
{
    private int _sequence;
    private readonly string _invocation = Guid.NewGuid().ToString("N");
    internal sealed record Result(int? ExitCode, string Output, bool Started, bool TimedOut, bool Complete, string Error = "");

    internal virtual async Task<Result> RunAsync(string executable, IReadOnlyList<string> arguments, string operation,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(privateDirectory);
        var prefix = Path.Combine(privateDirectory, _invocation + "-" + Interlocked.Increment(ref _sequence).ToString("D4") + "-" + operation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        // A Windows PowerShell child must resolve its own modules when invoked
        // from PowerShell Core or a Linux parent; this changes only this child.
        process.StartInfo.Environment.Remove("PSModulePath");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        var started = false;
        var timedOut = false;
        var output = Task.FromResult("");
        var error = Task.FromResult("");
        var watch = Stopwatch.StartNew();
        try
        {
            started = process.Start();
            if (!started) return new(null, "", false, false, false);
            output = ReadBoundedAsync(process.StandardOutput, prefix + ".stdout", deadline.Token);
            error = ReadBoundedAsync(process.StandardError, prefix + ".stderr", deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).WaitAsync(deadline.Token);
            return new(process.ExitCode, await output, true, false, true, await error);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            timedOut = deadline.IsCancellationRequested;
            deadline.Cancel();
            if (started)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception drainFailure) when (drainFailure is not OutOfMemoryException) { }
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
            }
            // Both readers have cancellation and a separate bounded drain. A
            // descendant retaining an inherited pipe cannot indefinitely block us.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception drainFailure) when (drainFailure is not OutOfMemoryException) { }
            await File.WriteAllTextAsync(prefix + ".failure-private.txt", failure.ToString(), CancellationToken.None);
            return new(started && process.HasExited ? process.ExitCode : null, "", started, timedOut, false);
        }
        finally
        {
            // No arguments, raw output, exception text or environment in safe
            // lifecycle evidence. The private streams retain original evidence.
            var exit = started && process.HasExited ? process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
            await File.WriteAllTextAsync(prefix + ".lifecycle.txt",
                $"operation={operation}\nstarted={started}\ntimedOut={timedOut}\nexitCode={exit}\nseconds={watch.Elapsed.TotalSeconds:R}\n",
                CancellationToken.None);
        }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, string path, CancellationToken token)
    {
        const int limit = 8 * 1024 * 1024;
        await using var file = new StreamWriter(path, false, new UTF8Encoding(false));
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) break;
            if (text.Length + count > limit) throw new InvalidOperationException("Fixture command output limit exceeded.");
            text.Append(buffer, 0, count);
            await file.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return text.ToString();
    }
}
