using Haby.Cli.Commands.Manifest;
using System.CommandLine;

namespace Haby.Cli;

internal sealed partial class CliApplication(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    TextWriter output,
    TextWriter error,
    Action<RootCommand>? registerAdditionalCommands = null)
{
    internal const int Success = 0;
    internal const int Rejected = 1;
    internal const int UsageError = 2;
    internal const int TransportError = 3;

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var commands = new CliCommandContext(endpointSource, clientFactory, new CliTextPresenter(output), error);
        var root = new RootCommand("Inspect and import manifests through a local registry.");
        root.Subcommands.Add(ManifestCommands.Create(commands));
        registerAdditionalCommands?.Invoke(root);

        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0 || args.Length == 0)
        {
            error.WriteLine("Invalid command syntax. Use --help to see available commands.");
            return UsageError;
        }

        // Help is handled before any command action or endpoint lookup.
        return await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
    }
}
