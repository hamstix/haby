using System.CommandLine;

namespace Haby.Cli.Commands.Config;

internal static class ConfigCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var config = new Command("config", "Inspect local CLI configuration.");
        config.SetAction(_ => context.MissingSubcommand());
        var view = new Command("view", "View validated local configuration.");
        view.SetAction((_, cancellationToken) => context.ViewConfigurationAsync(false, cancellationToken));
        var current = new Command("current-context", "Show the selected context.");
        current.SetAction((_, cancellationToken) => context.ViewConfigurationAsync(true, cancellationToken));
        config.Subcommands.Add(view);
        config.Subcommands.Add(current);
        return config;
    }
}
