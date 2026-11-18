using System.Diagnostics;
using Haby.PostgreSql.IntegrationTests;

internal static class ConfiguredNativeCli
{
    internal static async Task<string> Run(ConfiguredLifecycle lifetime, string privateDirectory, string binary, string endpoint, string[] args,
        int expectedExit = 0, string format = "json", string environment = "pilot", string? credential = null)
    {
        using var phase = lifetime.Start(ConfiguredLifecycle.Kind.NativeCli);
        // The launcher's existing owned private directory is removed even when
        // the child is hard-killed before owned configuration cleanup can run.
        var config = Path.Combine(privateDirectory, "configured-cli-" + Guid.NewGuid().ToString("N") + ".yaml");
        return await ConfiguredCliProcess.WithOwnedConfigurationAsync(async () =>
        {
            try
            {
                await OperatorHostIntegrationTests.WriteNativeCommitConfiguration(config, endpoint, environment, credential);
                phase.Token.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in args.Concat(new[] { "--config", config, "-o", format })) start.ArgumentList.Add(argument);
                start.Environment["HABY_OPERATOR_SKIP_TLS_VALIDATION"] = "true";
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Configured CLI launch failed.");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                var result = await ConfiguredCliProcess.CollectAsync(process.WaitForExitAsync(), output, error,
                    () => { if (!process.HasExited) process.Kill(entireProcessTree: true); }, phase.Token, lifetime.Clock);
                if (process.ExitCode != expectedExit || result.Error.Length != 0 || result.Output.Contains("commit-secret-sentinel", StringComparison.Ordinal) ||
                    result.Output.Contains("never-connect", StringComparison.Ordinal)) throw new InvalidOperationException("Configured CLI result invalid; output withheld.");
                return result.Output;
            }
            catch (ConfiguredCliProcess.Incomplete error)
            {
                phase.NoteIncomplete(error.Reason);
                Console.WriteLine("Q243 configured cli-tail " + error.Reason);
                throw;
            }
        }, () => { if (File.Exists(config)) File.Delete(config); }, phase.Complete);
    }
}
