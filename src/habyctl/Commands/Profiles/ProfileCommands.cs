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
        var list = new Command("list", "List imported Profiles with their maximum numeric revision (discovery only).");
        var pageSize = new Option<int>("--page-size") { DefaultValueFactory = _ => 50 };
        var afterId = new Option<string?>("--after-profile-id");
        list.Options.Add(pageSize); list.Options.Add(afterId);
        list.SetAction((parse, ct) => context.ListProfilesAsync(parse.GetValue(pageSize), parse.GetValue(afterId), ct));
        group.Subcommands.Add(list);
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
        var history = new Command("list", "List exact imported revisions in numeric order.");
        var historyId = new Argument<string>("profile-id");
        var historySize = new Option<int>("--page-size") { DefaultValueFactory = _ => 50 };
        var afterRevision = new Option<int?>("--after-revision");
        history.Arguments.Add(historyId); history.Options.Add(historySize); history.Options.Add(afterRevision);
        history.SetAction((parse, ct) => context.ListProfileRevisionsAsync(parse.GetRequiredValue(historyId), parse.GetValue(historySize), parse.GetValue(afterRevision), ct));
        revision.Subcommands.Add(history);
        group.Subcommands.Add(revision);
        return group;
    }
}
