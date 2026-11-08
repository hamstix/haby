using System.Text;
using System.Text.Json;

namespace Haby.Cli;

internal sealed partial class CliCommandContext
{
    internal async Task<int> SetCredentialAsync(string instanceName, string? tokenFile, bool stdin, string? newConfigFile, bool normalize, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            if ((tokenFile is null) == !stdin || newConfigFile is null || !normalize) throw new InvalidDataException();
            var config = await configuration.LoadAsync(options().File, token);
            if (!config.Instances.TryGetValue(instanceName, out var instance)) throw new InvalidDataException();
            string? inline = null;
            if (stdin)
            {
                var bytes = await SecretInput.ReadAsync(secretTerminal, true, error, token);
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    inline = await CliOperatorCredential.ReadAsync(stream, token);
                }
                finally { Array.Clear(bytes); }
            }
            else
            {
                tokenFile = Path.GetFullPath(tokenFile!);
                _ = await CliOperatorCredential.ResolveAsync(new(instance.Endpoint, TokenFile: tokenFile), token);
            }
            var instances = new SortedDictionary<string, CliInstance>(StringComparer.Ordinal);
            foreach (var pair in config.Instances) instances.Add(pair.Key, pair.Value);
            instances[instanceName] = instance with { Token = inline, TokenFile = tokenFile };
            var normalized = config with { Instances = instances };
            var contents = NormalizedConfiguration(normalized);
            if (Encoding.UTF8.GetByteCount(contents) > CliConfigurationService.MaxBytes) throw new InvalidDataException();
            _ = CliConfigurationService.Parse(contents);
            // Write a new file. Never rewrite the original or silently select the result.
            var path = Path.GetFullPath(newConfigFile);
            if (File.Exists(path)) throw new IOException();
            temporary = Path.Combine(Path.GetDirectoryName(path)!, ".registry-config-" + Guid.NewGuid().ToString("N"));
            var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, settings))
            {
                var bytes = Encoding.UTF8.GetBytes(contents);
                try { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
                finally { Array.Clear(bytes); }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false); temporary = null;
            output.WriteLine("Credential configuration written to the new file. Select it explicitly with --config. The original remains unchanged.");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException or InvalidDataException)
        { error.WriteLine("Credential setup failed. Use a known instance, exactly one --token-file or --stdin, --normalize and --new-config-file <new-path>."); return CliApplication.UsageError; }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error.WriteLine("Temporary configuration cleanup failed; inspect the protected destination directory."); }
            }
        }
    }
    private static string NormalizedConfiguration(CliConfiguration config)
    {
        static string Q(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";
        var s = new StringBuilder("formatVersion: 1\r\n");
        if (config.CurrentContext is { } current) s.Append("currentContext: ").Append(Q(current)).Append("\r\n");
        s.Append(config.Instances.Count == 0 ? "instances: {}\r\n" : "instances:\r\n");
        foreach (var (name, instance) in config.Instances)
        {
            s.Append("  ").Append(Q(name)).Append(":\r\n    endpoint: ").Append(Q(instance.Endpoint)).Append("\r\n");
            if (instance.Token is { } value) s.Append("    token: ").Append(Q(value)).Append("\r\n");
            if (instance.TokenFile is { } file) s.Append("    tokenFile: ").Append(Q(file)).Append("\r\n");
            if (instance.AllowInsecureHttp) s.Append("    allowInsecureHttp: true\r\n");
        }
        s.Append(config.Contexts.Count == 0 ? "contexts: {}\r\n" : "contexts:\r\n");
        foreach (var (name, context) in config.Contexts)
            s.Append("  ").Append(Q(name)).Append(":\r\n    instance: ").Append(Q(context.Instance)).Append("\r\n    environmentId: ").Append(Q(context.EnvironmentId)).Append("\r\n");
        return s.ToString();
    }
}
