using System.CommandLine;

namespace Haby.Cli.Commands.Providers;

internal static class ResourceNamingCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("resource-naming", "Administer immutable Environment naming policies and exact binding selection.");
        var environment = new Option<string?>("--environment") { Recursive = true };
        group.Options.Add(environment);
        group.SetAction(_ => context.MissingSubcommand());
        var name = new Argument<string>("name");
        var revision = new Option<long>("--revision") { Required = true };
        var file = new Option<string>("--file") { Required = true };
        var create = new Command("create", "Create one immutable exact positive revision from closed policy JSON.") { name, revision, file };
        create.SetAction((p, ct) => context.CreateNamingAsync(p.GetValue(environment), p.GetRequiredValue(name), p.GetValue(revision), p.GetRequiredValue(file), ct));
        group.Subcommands.Add(create);
        var getName = new Argument<string>("name");
        var getRevision = new Option<long>("--revision") { Required = true };
        var get = new Command("get", "Read one exact policy revision.") { getName, getRevision };
        get.SetAction((p, ct) => context.ExecuteSecretAsync(p.GetValue(environment), async (client, env, token) =>
            context.PrintNaming("resource-naming get", await client.GetResourceNamingAsync(new() { Environment = env, Name = p.GetRequiredValue(getName), Revision = p.GetValue(getRevision) }, token)), ct));
        group.Subcommands.Add(get);
        var bindingAlias = new Argument<string>("alias");
        var bindingGet = new Command("selection", "Read the exact selection, or canonical exact default.") { bindingAlias };
        bindingGet.SetAction((p, ct) => context.ExecuteSecretAsync(p.GetValue(environment), async (client, env, token) =>
            context.PrintNaming("resource-naming selection", await client.GetResourceNamingSelectionAsync(new() { Environment = env, Alias = p.GetRequiredValue(bindingAlias) }, token)), ct));
        group.Subcommands.Add(bindingGet);
        var alias = new Argument<string>("alias");
        var policy = new Option<string>("--policy") { Required = true };
        var selectedRevision = new Option<long>("--revision") { Required = true };
        var absent = new Option<bool>("--if-absent");
        var match = new Option<string?>("--if-match");
        var select = new Command("select", "Conditionally select an exact policy without changing the Provider binding.") { alias, policy, selectedRevision, absent, match };
        select.SetAction((p, ct) => context.SelectNamingAsync(p.GetValue(environment), p.GetRequiredValue(alias), p.GetRequiredValue(policy),
            p.GetValue(selectedRevision), p.GetValue(absent), p.GetValue(match), ct));
        group.Subcommands.Add(select);
        return group;
    }
}
