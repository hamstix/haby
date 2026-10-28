using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Manifest.Revision;

internal static class GetRevisionCommand
{
    internal static Command Create(CliCommandContext context)
    {
        var id = new Argument<string>("application-record-id");
        var number = new Argument<long>("revision-number");
        var command = new Command("get", "Get an accepted revision.") { id, number };
        command.SetAction((parse, cancellationToken) => context.ExecuteRevisionAsync(
            parse.GetRequiredValue(number), async (client, token) =>
            {
                var reply = await client.GetRevisionAsync(new GetRevisionRequest
                {
                    Reference = new RevisionReference
                    {
                        ApplicationRecordId = parse.GetRequiredValue(id),
                        RevisionNumber = parse.GetRequiredValue(number)
                    }
                }, token);
                return context.Presenter.Print(reply);
            }, cancellationToken));
        return command;
    }
}
