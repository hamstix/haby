using System.CommandLine;

namespace Haby.Cli.Commands;

internal static class OperatorAccessCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var root = new Command("operator", "Administer registry operators.");
        root.SetAction(_ => context.MissingSubcommand());
        var access = new Command("access", "Create, inspect and revoke operator-admin tokens.");
        access.SetAction(_ => context.MissingSubcommand()); root.Subcommands.Add(access);
        var create = new Command("create", "Issue a token once to an explicit sensitive destination.");
        var name = new Option<string?>("--display-name"); var expiry = new Option<string?>("--expires-at");
        var file = new Option<string?>("--token-output-file"); var stdout = new Option<bool>("--token-stdout");
        create.Options.Add(name); create.Options.Add(expiry); create.Options.Add(file); create.Options.Add(stdout);
        create.SetAction((p, ct) => context.CreateOperatorAccessAsync(p.GetValue(name), p.GetValue(expiry), p.GetValue(file), p.GetValue(stdout), ct));
        access.Subcommands.Add(create);
        foreach (var verb in new[] { "get", "revoke" })
        {
            var command = new Command(verb); var id = new Argument<string>("grant-id"); command.Arguments.Add(id);
            command.SetAction((p, ct) => context.OperatorAccessAsync(verb, p.GetValue(id), 50, null, ct)); access.Subcommands.Add(command);
        }
        var list = new Command("list"); var size = new Option<int>("--page-size") { DefaultValueFactory = _ => 50 }; var after = new Option<string?>("--after-grant-id");
        list.Options.Add(size); list.Options.Add(after);
        list.SetAction((p, ct) => context.OperatorAccessAsync("list", null, p.GetValue(size), p.GetValue(after), ct)); access.Subcommands.Add(list);
        return root;
    }
}
