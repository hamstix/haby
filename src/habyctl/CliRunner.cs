using Haby.Cli.Commands.Manifest;
using System.CommandLine;

namespace Haby.Cli;

internal sealed partial class CliApplication(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    TextWriter output,
    TextWriter error,
    Action<RootCommand>? registerAdditionalCommands = null,
    IConfigEnvironment? configEnvironment = null, ISecretTerminal? secretTerminal = null)
{
    internal const int Success = 0;
    internal const int Rejected = 1;
    internal const int UsageError = 2;
    internal const int TransportError = 3;

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (VersionOnly(args))
        {
            output.WriteLine(typeof(CliApplication).Assembly.GetName().Version);
            return Success;
        }
        var root = new RootCommand("Inspect manifests and plan deployments through a local registry.");
        var configFile = new Option<string?>("--config") { Description = "CLI configuration file.", Recursive = true };
        var contextName = new Option<string?>("--context") { Description = "Context for this invocation.", Recursive = true };
        var outputFormat = new Option<string?>("--output", "-o")
        { Description = "Result format: text (default) or json. Help and version stay human-readable.", Recursive = true };
        root.Options.Add(configFile);
        root.Options.Add(contextName);
        root.Options.Add(outputFormat);
        var configuration = new CliConfigurationService(configEnvironment ?? new CliConfigEnvironment());
        System.CommandLine.ParseResult? parsed = null;
        var commands = new CliCommandContext(endpointSource, clientFactory, new CliTextPresenter(output),
            new CliJsonPresenter(output), output, error, configuration,
            () => (parsed!.GetValue(configFile), parsed.GetValue(contextName)),
            () => parsed!.GetValue(outputFormat) == "json", secretTerminal ?? new SecretTerminal());
        root.Subcommands.Add(ManifestCommands.Create(commands));
        root.Subcommands.Add(Commands.Secrets.SecretCommands.Create(commands));
        root.Subcommands.Add(Commands.Environments.EnvironmentCommands.Create(commands));
        root.Subcommands.Add(Commands.Planning.PlanningCommands.ChangeSet(commands));
        root.Subcommands.Add(Commands.Planning.PlanningCommands.Plan(commands));
        root.Subcommands.Add(Commands.Planning.CommitCommands.Deployment(commands));
        root.Subcommands.Add(Commands.Config.ConfigCommands.Create(commands));
        root.Subcommands.Add(Commands.Operations.OperationCommands.Create(commands));
        registerAdditionalCommands?.Invoke(root);

        parsed = root.Parse(args);
        if (parsed.Errors.Count > 0 || args.Length == 0)
        {
            error.WriteLine("Invalid command syntax. Use --help to see available commands.");
            return UsageError;
        }
        if (parsed.GetValue(outputFormat) is { } selectedFormat && selectedFormat is not ("text" or "json"))
        {
            error.WriteLine("Invalid output format. Use text or json.");
            return UsageError;
        }

        // Help is handled before any command action or endpoint lookup.
        return await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
    }

    private static bool VersionOnly(string[] args)
    {
        var version = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--version" && !version) version = true;
            else if (args[index] is "--config" or "--output" or "-o" && index + 1 < args.Length)
            {
                if (args[index] is "--output" or "-o" && args[index + 1] is not ("text" or "json"))
                    return false;
                index++;
            }
            else return false;
        }
        return version;
    }
}
