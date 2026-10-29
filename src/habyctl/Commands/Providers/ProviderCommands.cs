using System.CommandLine;
using Haby.Operator.Contracts;

namespace Haby.Cli.Commands.Providers;

internal static class ProviderCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("provider", "Apply immutable Provider declarations and discover safe metadata.");
        var environment = new Option<string?>("--environment") { Recursive = true, Description = "Environment name; must match the apply file when explicit." };
        group.Options.Add(environment);
        group.SetAction(_ => context.MissingSubcommand());
        var file = new Option<string>("--file") { Required = true };
        var confirm = new Option<bool>("--confirm-logical-identity");
        var expected = new Option<long?>("--expected-previous-revision");
        var apply = new Command("apply", "Accept one immutable revision without connecting or changing aliases.") { file, confirm, expected };
        apply.SetAction((parse, token) => context.ApplyProviderAsync(parse.GetRequiredValue(file), parse.GetValue(environment),
            parse.GetValue(confirm), parse.GetValue(expected), token));
        group.Subcommands.Add(apply);

        var name = new Argument<string>("name");
        var get = new Command("get", "Get safe metadata for the highest accepted revision.") { name };
        get.SetAction((parse, token) => context.ExecuteSecretAsync(parse.GetValue(environment), async (client, env, ct) =>
            context.PrintProvider("provider get", await client.GetProviderAsync(new GetProviderRequest
            { Environment = env, Name = parse.GetRequiredValue(name) }, ct)), token));
        group.Subcommands.Add(get);
        var size = new Option<int>("--page-size");
        var cursor = new Option<string?>("--cursor");
        var list = new Command("list", "List one bounded page of safe Provider metadata.") { size, cursor };
        list.SetAction((parse, token) => context.ExecuteSecretAsync(parse.GetValue(environment), async (client, env, ct) =>
            context.PrintProviders(await client.ListProvidersAsync(new ListProvidersRequest
            { Environment = env, PageSize = parse.GetValue(size), Cursor = parse.GetValue(cursor) ?? "" }, ct)), token));
        group.Subcommands.Add(list);
        var revisions = new Command("revision", "Inspect exact immutable revision metadata.");
        revisions.SetAction(_ => context.MissingSubcommand());
        var revisionName = new Argument<string>("name");
        var number = new Option<long>("--revision") { Required = true };
        var revisionGet = new Command("get", "Get one exact revision's safe metadata.") { revisionName, number };
        revisionGet.SetAction((parse, token) => context.ExecuteSecretAsync(parse.GetValue(environment), async (client, env, ct) =>
                context.PrintProvider("provider revision get", await client.GetProviderAsync(new GetProviderRequest
                { Environment = env, Name = parse.GetRequiredValue(revisionName), Revision = parse.GetValue(number) }, ct)), token));
        revisions.Subcommands.Add(revisionGet);
        group.Subcommands.Add(revisions);
        return group;
    }
}
