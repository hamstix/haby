using Haby.Cli.Commands.Manifest.Revision;
using System.CommandLine;

namespace Haby.Cli.Commands.Manifest;

internal static class ManifestCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var manifest = new Command("manifest", "Validate or import a manifest and inspect revisions.");
        manifest.SetAction(_ => context.MissingSubcommand());
        manifest.Subcommands.Add(ValidateCommand.Create(context));
        manifest.Subcommands.Add(ImportCommand.Create(context));
        manifest.Subcommands.Add(RevisionCommands.Create(context));
        return manifest;
    }
}
