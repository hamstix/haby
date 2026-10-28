using Haby.Cli.Commands.Manifest;
using System.CommandLine;

namespace Haby.Cli;

internal sealed partial class CliApplication(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    TextWriter output,
    TextWriter error,
    Action<RootCommand>? registerAdditionalCommands = null,
    IConfigEnvironment? configEnvironment = null)
{
    internal const int Success = 0;
    internal const int Rejected = 1;
    internal const int UsageError = 2;
    internal const int TransportError = 3;

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args is ["--version"] or ["--version", "--config", _] or ["--config", _, "--version"])
        {
            output.WriteLine(typeof(CliApplication).Assembly.GetName().Version);
            return Success;
        }
        var root = new RootCommand("Inspect and import manifests through a local registry.");
        var configFile = new Option<string?>("--config") { Description = "CLI configuration file.", Recursive = true };
        var contextName = new Option<string?>("--context") { Description = "Context for this invocation.", Recursive = true };
        root.Options.Add(configFile);
        root.Options.Add(contextName);
        var configuration = new CliConfigurationService(configEnvironment ?? new CliConfigEnvironment());
        System.CommandLine.ParseResult? parsed = null;
        var commands = new CliCommandContext(endpointSource, clientFactory, new CliTextPresenter(output),
            output, error, configuration, () => (parsed!.GetValue(configFile), parsed.GetValue(contextName)));
        root.Subcommands.Add(ManifestCommands.Create(commands));
        root.Subcommands.Add(Commands.Config.ConfigCommands.Create(commands));
        registerAdditionalCommands?.Invoke(root);

        parsed = root.Parse(args);
        if (parsed.Errors.Count > 0 || args.Length == 0)
        {
            error.WriteLine("Invalid command syntax. Use --help to see available commands.");
            return UsageError;
        }

        // Help is handled before any command action or endpoint lookup.
        return await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
    }
}
