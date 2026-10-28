using System.CommandLine;

namespace Haby.Cli.Commands.Manifest.Revision;

internal static class RevisionCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var revision = new Command("revision", "Inspect accepted manifest revisions.");
        revision.SetAction(_ => context.MissingSubcommand());
        revision.Subcommands.Add(GetRevisionCommand.Create(context));
        revision.Subcommands.Add(PreviewRevisionCommand.Create(context));
        return revision;
    }
}
