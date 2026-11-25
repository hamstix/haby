using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Cli;

namespace Haby.Cli.Composition.Sample;

// This adapter owns a complete closed model. JSON syntax is a YAML subset;
// no reflection serializer or arbitrary YAML editing engine is used.
public sealed class SampleConfiguration(CliConfigurationPaths paths) : ICliContextResolver, ICliConfigurationAdapter
{
    public CliConfigurationCapabilities Capabilities => CliConfigurationCapabilities.View | CliConfigurationCapabilities.CurrentContext | CliConfigurationCapabilities.SetCredential;
    public async ValueTask<CliResolvedContext> ResolveAsync(CliContextSelection selection, CancellationToken cancellationToken)
    {
        var document = await ReadAsync(selection, cancellationToken);
        if ((selection.ContextName ?? document.CurrentContext) != document.CurrentContext) throw new InvalidDataException();
        return new(new Uri(document.Endpoint), document.Environment, new Credentials(document.TokenFile), document.AllowInsecureHttp);
    }
    public async ValueTask<string?> GetCurrentContextAsync(CliContextSelection selection, CancellationToken cancellationToken)
    {
        var document = await ReadAsync(selection, cancellationToken);
        if (selection.ContextName is not null && selection.ContextName != document.CurrentContext) throw new InvalidDataException();
        return document.CurrentContext;
    }
    public async ValueTask WriteSafeViewAsync(CliContextSelection selection, CliInvocationContext invocation, CancellationToken cancellationToken)
    {
        var document = await ReadAsync(selection, cancellationToken);
        // Each confidential field is deliberately mapped, including downstream-only fields.
        var safe = new SampleSafeView(document.CurrentContext, document.Endpoint, document.Environment, document.Note,
            document.TokenFile is null ? null : "[redacted]", "[redacted]", document.Values);
        if (invocation.JsonOutput) invocation.WriteJson(safe, SampleJsonContext.Default.SampleSafeView);
        else invocation.Output.WriteLine(JsonSerializer.Serialize(safe, SampleJsonContext.Default.SampleSafeView));
    }
    public async ValueTask ApplyCredentialAsync(CliContextSelection selection, CliCredentialChange change, CancellationToken cancellationToken)
    {
        // This example supports file credentials only and one explicitly named registry instance.
        if (change.Instance != "registry" || change.Credential is not null || change.TokenFile is null || !change.Normalize) throw new InvalidDataException();
        var document = await ReadAsync(selection, cancellationToken);
        var updated = document with { TokenFile = change.TokenFile };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, SampleJsonContext.Default.SampleDocument);
        string? temporary = null;
        try
        {
            if (bytes.Length > 65536) throw new InvalidDataException();
            var destination = Path.GetFullPath(change.Destination);
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".cli-sample-" + Guid.NewGuid().ToString("N"));
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            { await stream.WriteAsync(bytes, cancellationToken); await stream.FlushAsync(cancellationToken); stream.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false); temporary = null;
        }
        finally { Array.Clear(bytes); if (temporary is not null) File.Delete(temporary); }
    }
    private async ValueTask<SampleDocument> ReadAsync(CliContextSelection selection, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(paths.Select(selection.ConfigurationPath), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > 65536) throw new InvalidDataException();
        var bytes = new byte[65537]; var count = 0;
        try
        {
            while (count < bytes.Length)
            { var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken); if (read == 0) break; count += read; }
            if (count > 65536) throw new InvalidDataException();
            SampleDocument document;
            try { document = JsonSerializer.Deserialize(bytes.AsSpan(0, count), SampleJsonContext.Default.SampleDocument) ?? throw new InvalidDataException(); }
            catch (JsonException) { throw new InvalidDataException(); }
            if (document.FormatVersion != 1 || string.IsNullOrWhiteSpace(document.CurrentContext) || string.IsNullOrWhiteSpace(document.Environment) ||
                document.CurrentContext.Any(char.IsControl) || document.Environment.Any(char.IsControl)) throw new InvalidDataException();
            if (!Uri.TryCreate(document.Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
                document.Note is null || document.DownstreamSecret is null || document.Values is null) throw new InvalidDataException();
            return document;
        }
        finally { Array.Clear(bytes); }
    }
    private sealed class Credentials(string? path) : ICliCredentialSource
    {
        public async ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken) => path is null ? null : await CliCredentials.ReadTokenFileAsync(path, cancellationToken);
        public override string ToString() => "[Sample credential source]";
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SampleDocument(int FormatVersion, string CurrentContext, string Endpoint, string Environment,
    string? TokenFile, bool AllowInsecureHttp, string Note, string DownstreamSecret, Dictionary<string, string> Values);
public sealed record SampleSafeView(string CurrentContext, string Endpoint, string Environment, string Note,
    string? TokenFile, string DownstreamSecret, Dictionary<string, string> Values);
public sealed record SampleResult(string Outcome);
[JsonSerializable(typeof(SampleDocument))]
[JsonSerializable(typeof(SampleSafeView))]
[JsonSerializable(typeof(SampleResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SampleJsonContext : JsonSerializerContext;
