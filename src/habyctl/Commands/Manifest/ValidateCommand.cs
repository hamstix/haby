using Google.Protobuf;
using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Manifest;

internal static class ValidateCommand
{
    internal static Command Create(CliCommandContext context)
    {
        var file = new Argument<string>("file") { Description = "Manifest envelope file." };
        var command = new Command("validate", "Validate a manifest envelope.") { file };
        command.SetAction((parse, cancellationToken) => context.ExecuteAsync(async (client, token) =>
        {
            var bytes = await CliApplication.ReadEnvelopeAsync(parse.GetRequiredValue(file), token);
            var reply = await client.ValidateAsync(
                new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(bytes) }, token);
            return context.Presenter.Print(reply);
        }, cancellationToken));
        return command;
    }
}
