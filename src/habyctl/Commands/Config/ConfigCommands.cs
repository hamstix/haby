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
        var credential = new Command("set-credential", "Write a new normalized config with the named registry credential; preserve the original.");
        var instance = new Argument<string>("instance"); var tokenFile = new Option<string?>("--token-file"); var stdin = new Option<bool>("--stdin");
        var destination = new Option<string?>("--new-config-file"); var normalize = new Option<bool>("--normalize");
        credential.Arguments.Add(instance); credential.Options.Add(tokenFile); credential.Options.Add(stdin); credential.Options.Add(destination); credential.Options.Add(normalize);
        credential.SetAction((p, ct) => context.SetCredentialAsync(p.GetValue(instance)!, p.GetValue(tokenFile), p.GetValue(stdin), p.GetValue(destination), p.GetValue(normalize), ct));
        config.Subcommands.Add(credential);
        config.Subcommands.Add(view);
        config.Subcommands.Add(current);
        return config;
    }
}
