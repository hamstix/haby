using Google.Protobuf;
using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Placements;

internal static class PlacementCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("placement", "Validate and import immutable Placement revisions; import changes no selections.");
        group.SetAction(_ => context.MissingSubcommand());
        foreach (var import in new[] { false, true })
        {
            var file = new Argument<string>("file");
            var alias = new Option<string>("--provider-alias") { Required = import };
            var command = new Command(import ? "import" : "validate", import ? "Import into the selected Environment and alias scope." : "Validate portable content only.") { file };
            if (import) command.Options.Add(alias);
            command.SetAction((parse, ct) => import
                ? context.ExecutePlanningAsync(async (client, environment, token) =>
                {
                    var request = new PlacementPayloadRequest
                    {
                        Environment = environment,
                        ProviderAlias = parse.GetRequiredValue(alias),
                        Payload = ByteString.CopyFrom(await CliApplication.ReadEnvelopeAsync(parse.GetRequiredValue(file), token))
                    };
                    return context.PrintPlacement("placement import", await client.ImportPlacementAsync(request, token), environment, request.ProviderAlias);
                }, ct)
                : context.ExecuteAsync(async (client, token) => context.PrintPlacement("placement validate", await client.ValidatePlacementAsync(new()
                { Payload = ByteString.CopyFrom(await CliApplication.ReadEnvelopeAsync(parse.GetRequiredValue(file), token)) }, token), "", ""), ct));
            group.Subcommands.Add(command);
        }
        var revision = new Command("revision", "Read an exact imported revision.");
        revision.SetAction(_ => context.MissingSubcommand());
        var name = new Argument<string>("name"); var number = new Argument<int>("revision");
        var provider = new Option<string>("--provider-alias") { Required = true };
        var get = new Command("get", "Get exact metadata; JSON includes the complete reusable declaration.") { name, number };
        get.Options.Add(provider);
        get.SetAction((parse, ct) => context.ExecutePlanningAsync(async (client, environment, token) =>
        {
            var request = new PlacementRevisionRequest
            {
                Environment = environment,
                ProviderAlias = parse.GetRequiredValue(provider),
                Name = parse.GetRequiredValue(name),
                Revision = parse.GetRequiredValue(number)
            };
            var reply = await client.GetPlacementRevisionAsync(request, token);
            if (reply.Reference is not null && !Equals(reply.Reference, request)) throw new InvalidDataException("Inconsistent Placement identity.");
            return context.PrintPlacement("placement revision get", reply, environment, request.ProviderAlias);
        }, ct));
        revision.Subcommands.Add(get); group.Subcommands.Add(revision);
        return group;
    }
}
