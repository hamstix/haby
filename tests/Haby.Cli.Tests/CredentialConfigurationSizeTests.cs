using System.Diagnostics;
using System.Text;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public async Task Credential_setup_enforces_normalized_UTF8_reader_budget_before_publication(int targetBytes, bool accepted)
    {
        var binary = AotBinary();
        foreach (var native in binary is null ? new[] { false } : new[] { false, true })
        {
            var folder = Path.Combine(Path.GetTempPath(), "credential-size-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "original.yaml");
            var baseline = Path.Combine(folder, "baseline.yaml");
            var destination = Path.Combine(folder, "normalized.yaml");
            var credential = OperatorToken();
            var unicodePath = "/tmp/" + string.Concat(Enumerable.Repeat("\u76ee\u5f55\u76ee\u5f55/", 120)) + "operator.token";
            var paths = Enumerable.Repeat(unicodePath, 20).ToArray();
            string Input() => "# preserve original formatting\nformatVersion: 1\ninstances:\n" +
                string.Concat(paths.Select((path, index) => $"  r{index + 1}:\n    endpoint: https://registry.example\n    tokenFile: '{path}'\n")) + "contexts: {}\n";
            async Task<(int Exit, string Out, string Error)> Run(string file)
            {
                var args = new[] { "config", "set-credential", "r1", "--stdin", "--normalize", "--new-config-file", file, "--config", source };
                if (!native)
                {
                    using var output = new StringWriter(); using var error = new StringWriter();
                    var exit = await new CliApplication(new EnvironmentEndpointSource(), new GrpcRegistryClientFactory(), output, error,
                        secretTerminal: new OperatorInput(credential + "\n")).RunAsync(args);
                    return (exit, output.ToString(), error.ToString());
                }
                var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.StandardInput.WriteLineAsync(credential); process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                return (process.ExitCode, await stdout, await stderr);
            }
            try
            {
                // Grow opaque unrelated paths by ASCII bytes from an observed valid result,
                // giving an exact byte boundary without duplicating the production YAML encoder.
                await File.WriteAllTextAsync(source, Input(), new UTF8Encoding(false));
                Assert.Equal(0, (await Run(baseline)).Exit);
                var extra = targetBytes - new FileInfo(baseline).Length;
                Assert.InRange(extra, 1, 19 * (2048 - unicodePath.Length));
                for (var index = 1; index < paths.Length; index++)
                {
                    var count = (int)Math.Min(extra, 2048 - unicodePath.Length);
                    paths[index] += new string('x', count); extra -= count;
                }
                Assert.Equal(0, extra); File.Delete(baseline);
                var original = Encoding.UTF8.GetBytes(Input());
                Assert.True(original.Length < CliConfigurationService.MaxBytes);
                await File.WriteAllBytesAsync(source, original);
                var result = await Run(destination);
                Assert.Equal(accepted ? 0 : CliApplication.UsageError, result.Exit);
                Assert.Equal(original, await File.ReadAllBytesAsync(source));
                Assert.DoesNotContain(credential, result.Out + result.Error);
                if (accepted)
                {
                    Assert.Empty(result.Error); Assert.Equal(targetBytes, new FileInfo(destination).Length);
                    var loaded = await new CliConfigurationService(new CliConfigEnvironment()).LoadAsync(destination, default);
                    Assert.Equal(credential, loaded.Instances["r1"].Token);
                    for (var index = 1; index < paths.Length; index++) Assert.Equal(paths[index], loaded.Instances[$"r{index + 1}"].TokenFile);
                }
                else
                {
                    Assert.Empty(result.Out); Assert.Contains("Credential setup failed.", result.Error);
                    Assert.Equal(source, Assert.Single(Directory.EnumerateFiles(folder)));
                }
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
