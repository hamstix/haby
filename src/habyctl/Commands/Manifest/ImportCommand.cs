using Google.Protobuf;
using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Manifest;

internal static class ImportCommand
{
    internal static Command Create(CliCommandContext context)
    {
        var file = new Argument<string>("file") { Description = "Manifest envelope file." };
        var command = new Command("import", "Import a manifest envelope.") { file };
        command.SetAction((parse, cancellationToken) => context.ExecuteAsync(async (client, token) =>
        {
            var bytes = await CliApplication.ReadEnvelopeAsync(parse.GetRequiredValue(file), token);
            var reply = await client.ImportAsync(
                new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(bytes) }, token);
            return context.Print(reply);
        }, cancellationToken));
        return command;
    }
}
