using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Haby.Operator.Contracts;
using Grpc.Net.Client;
using Grpc.Core.Interceptors;

namespace Haby.Cli;

public sealed record CliContextSelection(string? ConfigurationPath, string? ContextName);

/// <summary>Safe selection metadata. Credentials are accessed separately only for an RPC.</summary>
public sealed class CliResolvedContext(Uri endpoint, string environment, ICliCredentialSource? credentials = null,
    bool allowInsecureHttp = false, bool skipTlsCertificateValidation = false)
{
    public Uri Endpoint { get; } = endpoint;
    public string Environment { get; } = environment;
    public ICliCredentialSource? Credentials { get; } = credentials;
    public bool AllowInsecureHttp { get; } = allowInsecureHttp;
    public bool SkipTlsCertificateValidation { get; } = skipTlsCertificateValidation;
    public override string ToString() => "[Registry context]";
}

/// <summary>Caller-owned resolver. It must not resolve credentials while selecting metadata.</summary>
public interface ICliContextResolver
{
    ValueTask<CliResolvedContext> ResolveAsync(CliContextSelection selection, CancellationToken cancellationToken);
}

/// <summary>Caller-owned lazy source; never included in help, metadata or diagnostics.</summary>
public interface ICliCredentialSource
{
    ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken);
}

public static class CliCredentials
{
    public static async ValueTask<string> ReadTokenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await CliOperatorCredential.ReadAsync(stream, cancellationToken);
    }
}

[Flags]
public enum CliConfigurationCapabilities { None = 0, View = 1, CurrentContext = 2, SetCredential = 4 }

/// <summary>Explicit sensitive mutation. The adapter owns full-model persistence and must refuse unsafe writes.</summary>
public sealed class CliCredentialChange(string instance, string? credential, string? tokenFile, string destination, bool normalize)
{
    public string Instance { get; } = instance;
    public string? Credential { get; } = credential;
    public string? TokenFile { get; } = tokenFile;
    public string Destination { get; } = destination;
    public bool Normalize { get; } = normalize;
    public override string ToString() => "[Credential configuration change]";
}

/// <summary>Optional caller-owned local adapter. Safe view must explicitly redact the complete model.
/// Implementations must preserve all downstream values or refuse the mutation before writing.</summary>
public interface ICliConfigurationAdapter
{
    CliConfigurationCapabilities Capabilities { get; }
    ValueTask<string?> GetCurrentContextAsync(CliContextSelection selection, CancellationToken cancellationToken);
    ValueTask WriteSafeViewAsync(CliContextSelection selection, CliInvocationContext invocation, CancellationToken cancellationToken);
    ValueTask ApplyCredentialAsync(CliContextSelection selection, CliCredentialChange change, CancellationToken cancellationToken);
}

/// <summary>Composition-selected paths; an explicit path always wins. Home expansion is explicit.</summary>
public sealed record CliConfigurationPaths(string EnvironmentVariable, string DefaultPath)
{
    public string Select(string? explicitPath) => ExpandHome(explicitPath ?? System.Environment.GetEnvironmentVariable(EnvironmentVariable) ?? DefaultPath);
    public static string ExpandHome(string path) => path == "~" ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) :
        path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
        ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), path[2..]) : path;
}

public sealed class CliInvocationContext
{
    private readonly ICliContextResolver _resolver;
    internal CliInvocationContext(TextWriter output, TextWriter error, bool json, CliContextSelection selection,
        ICliContextResolver resolver, string commandPrefix)
    { Output = output; Error = error; JsonOutput = json; Selection = selection; _resolver = resolver; CommandPrefix = commandPrefix; }
    public TextWriter Output { get; }
    public TextWriter Error { get; }
    public bool JsonOutput { get; }
    public CliContextSelection Selection { get; }
    public string CommandPrefix { get; }
    public void WriteJson<T>(T value, JsonTypeInfo<T> metadata) => Output.WriteLine(JsonSerializer.Serialize(value, metadata));
    /// <summary>The returned session owns its channel. Dispose it after the command, including failures.</summary>
    public async ValueTask<CliRegistrySession> OpenRegistryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = await _resolver.ResolveAsync(Selection, cancellationToken);
        var address = CliApplication.ParseEndpoint(context.Endpoint?.AbsoluteUri, context.AllowInsecureHttp) ?? throw new InvalidDataException();
        cancellationToken.ThrowIfCancellationRequested();
        var credential = context.Credentials is null ? null : await context.Credentials.GetCredentialAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (credential is not null && !CliOperatorCredential.Valid(credential)) throw new InvalidDataException();
        return new CliRegistrySession(address, context.SkipTlsCertificateValidation, credential, context.Environment);
    }
}

public sealed class CliRegistrySession : IDisposable
{
    private readonly GrpcChannel _channel;
    internal CliRegistrySession(Uri endpoint, bool skipTls, string? credential, string environment)
    {
        _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = CliApplication.CreateHttpHandler(skipTls) });
        Client = new ManifestOperator.ManifestOperatorClient(_channel.Intercept(headers =>
        { if (credential is not null) headers.Add("authorization", "Bearer " + credential); return headers; }));
        Environment = environment;
    }
    public ManifestOperator.ManifestOperatorClient Client { get; }
    public string Environment { get; }
    public void Dispose() => _channel.Dispose();
    public override string ToString() => "[Registry session]";
}

public sealed class HabyCliBuilder
{
    internal string Identity { get; }
    internal ICliContextResolver? Resolver { get; }
    internal ICliConfigurationAdapter? Adapter { get; private set; }
    internal Command? RegistryParent { get; private set; }
    internal Command? ConfigurationParent { get; private set; }
    internal IConfigEnvironment? ConfigEnvironment { get; private set; }
    private bool _built;
    internal List<(Command Parent, Command Command, Func<System.CommandLine.ParseResult, CliInvocationContext, CancellationToken, Task<int>> Action)> Extensions { get; } = [];
    public RootCommand Root { get; }
    public TextWriter Output { get; set; } = Console.Out;
    public TextWriter Error { get; set; } = Console.Error;
    public HabyCliBuilder(string executableName, string description, ICliContextResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(executableName) || executableName.Any(char.IsControl) || executableName.Any(char.IsWhiteSpace)) throw new ArgumentException("Invalid executable identity.");
        Identity = executableName; Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver)); Root = new(description);
    }
    private HabyCliBuilder() { Identity = "habyctl"; Root = new("Inspect manifests and plan deployments through a local registry."); RegistryParent = Root; ConfigurationParent = Root; }
    public static HabyCliBuilder CreateDefault() => new();
    /// <summary>Reuse upstream YAML semantics with explicitly selected distribution paths.</summary>
    public static HabyCliBuilder CreateDefault(string executableName, CliConfigurationPaths paths)
    {
        var environment = new SelectedConfigEnvironment(paths);
        var builder = new HabyCliBuilder(executableName, "Inspect manifests and plan deployments through a registry.",
            new DefaultCliContextResolver(new CliConfigurationService(environment), new EnvironmentEndpointSource()));
        builder.ConfigEnvironment = environment; builder.RegistryParent = builder.Root; builder.ConfigurationParent = builder.Root;
        return builder;
    }
    private sealed class SelectedConfigEnvironment(CliConfigurationPaths paths) : IConfigEnvironment
    {
        public string? GetConfigPath() => System.Environment.GetEnvironmentVariable(paths.EnvironmentVariable) is { } value ? CliConfigurationPaths.ExpandHome(value) : null;
        public string GetDefaultPath() => CliConfigurationPaths.ExpandHome(paths.DefaultPath);
    }
    private void EnsureMutable() { if (_built) throw new InvalidOperationException("Composition is already finalized."); }
    public HabyCliBuilder MountRegistry(Command parent)
    {
        EnsureMutable();
        if (RegistryParent is not null) throw new InvalidOperationException("Registry commands are already mounted.");
        RegistryParent = parent; return this;
    }
    public HabyCliBuilder MountConfiguration(Command parent, ICliConfigurationAdapter adapter)
    {
        EnsureMutable();
        if (ConfigurationParent is not null) throw new InvalidOperationException("Configuration commands are already mounted.");
        ConfigurationParent = parent; Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter)); return this;
    }
    public HabyCliBuilder AddCommand(Command parent, Command command,
        Func<System.CommandLine.ParseResult, CliInvocationContext, CancellationToken, Task<int>> action)
    { EnsureMutable(); Extensions.Add((parent, command, action)); return this; }
    public HabyCliRuntime Build() { EnsureMutable(); _built = true; return new(this); }
}

public sealed class HabyCliRuntime
{
    private readonly CliApplication _application;
    private readonly SemaphoreSlim _invocation = new(1, 1);
    internal HabyCliRuntime(HabyCliBuilder builder)
    {
        _application = new CliApplication(new EnvironmentEndpointSource(), new GrpcRegistryClientFactory(), builder.Output, builder.Error, configEnvironment: builder.ConfigEnvironment, composition: builder);
        _application.Initialize();
    }
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try { await _invocation.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return 3; }
        try { return await _application.RunAsync(args, cancellationToken); }
        finally { _invocation.Release(); }
    }
}
