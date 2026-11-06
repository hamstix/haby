using Google.Protobuf;
using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Profiles;

internal static class ProfileCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("profile", "Manage installation-scoped configuration Profile declarations.");
        group.SetAction(_ => context.MissingSubcommand());
        group.Subcommands.Add(AssignmentCommands.Create(context));
        foreach (var import in new[] { false, true })
        {
            var name = import ? "import" : "validate";
            var file = new Argument<string>("file") { Description = "Profile UTF-8 JSON source file." };
            var command = new Command(name, import ? "Import an exact immutable Profile revision." : "Validate a portable declaration without resolving references.") { file };
            command.SetAction((parse, ct) => context.ExecuteAsync(async (client, token) =>
            {
                var request = new ProfilePayloadRequest { Payload = ByteString.CopyFrom(await CliApplication.ReadEnvelopeAsync(parse.GetRequiredValue(file), token)) };
                var reply = import ? await client.ImportProfileAsync(request, token) : await client.ValidateProfileAsync(request, token);
                return context.PrintProfile("profile " + name, reply);
            }, ct));
            group.Subcommands.Add(command);
        }
        var revision = new Command("revision", "Inspect exact Profile revisions.");
        revision.SetAction(_ => context.MissingSubcommand());
        var id = new Argument<string>("profile-id");
        var number = new Argument<int>("revision");
        var get = new Command("get", "Read an exact revision; JSON includes the normalized declaration.") { id, number };
        get.SetAction((parse, ct) => context.ExecuteRevisionAsync(parse.GetRequiredValue(number), async (client, token) =>
        {
            var request = new ProfileRevisionRequest { ProfileId = parse.GetRequiredValue(id), Revision = parse.GetRequiredValue(number) };
            var reply = await client.GetProfileRevisionAsync(request, token);
            if (!Equals(reply.Reference, request)) throw new InvalidDataException("Inconsistent Profile identity.");
            return context.PrintProfile("profile revision get", reply);
        }, ct));
        revision.Subcommands.Add(get);
        group.Subcommands.Add(revision);
        return group;
    }
}
