using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Environments;

internal static class EnvironmentCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("environment", "Create and discover Environments in the registry.");
        group.SetAction(_ => context.MissingSubcommand());
        foreach (var verb in new[] { "create", "get" })
        {
            var name = new Argument<string>("name");
            var command = new Command(verb, verb == "create" ? "Create or get an Environment by logical name." : "Get Environment metadata by logical name.") { name };
            command.SetAction((parse, token) => context.ExecuteAsync(async (client, ct) =>
            {
                var request = new EnvironmentNameRequest { Name = parse.GetRequiredValue(name) };
                var reply = verb == "create" ? await client.CreateEnvironmentAsync(request, ct) : await client.GetEnvironmentAsync(request, ct);
                return context.PrintEnvironment("environment " + verb, reply);
            }, token));
            group.Subcommands.Add(command);
        }
        var size = new Option<int>("--page-size") { Description = "Page size 1..100; default 50." };
        var cursor = new Option<string?>("--cursor") { Description = "Continuation returned by the registry." };
        var list = new Command("list", "List one page in name order; follow nextCursor for remaining items.") { size, cursor };
        list.SetAction((parse, token) => context.ExecuteAsync(async (client, ct) =>
            context.PrintEnvironments(await client.ListEnvironmentsAsync(new ListEnvironmentsRequest
            { PageSize = parse.GetValue(size), Cursor = parse.GetValue(cursor) ?? "" }, ct)), token));
        group.Subcommands.Add(list);
        return group;
    }
}
