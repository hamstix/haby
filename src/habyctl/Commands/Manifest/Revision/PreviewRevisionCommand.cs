using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Manifest.Revision;

internal static class PreviewRevisionCommand
{
    internal static Command Create(CliCommandContext context)
    {
        var id = new Argument<string>("application-record-id");
        var number = new Argument<long>("revision-number");
        var file = new Argument<string>("context-file");
        var command = new Command("preview", "Preview a revision against a local context file.")
            { id, number, file };
        command.SetAction((parse, cancellationToken) => context.ExecuteRevisionAsync(
            parse.GetRequiredValue(number), async (client, token) =>
            {
                var reference = new RevisionReference
                {
                    ApplicationRecordId = parse.GetRequiredValue(id),
                    RevisionNumber = parse.GetRequiredValue(number)
                };
                var request = await CliApplication.ReadPreviewContextAsync(
                    parse.GetRequiredValue(file), reference, token);
                var reply = await client.PreviewDatabaseAsync(request, token);
                return context.Print(reply);
            }, cancellationToken));
        return command;
    }
}
