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
        var checkName = new Argument<string>("name");
        var checkRevision = new Option<long>("--revision") { Required = true };
        var check = new Command("check", "Check one exact saved revision without external mutations.") { checkName, checkRevision };
        check.SetAction((parse, token) => context.ExecuteSecretAsync(parse.GetValue(environment), async (client, env, ct) =>
            context.PrintProviderAccess("provider check", await client.CheckProviderAsync(new CheckProviderRequest
            { Environment = env, Name = parse.GetRequiredValue(checkName), Revision = parse.GetValue(checkRevision) }, ct)), token));
        group.Subcommands.Add(check);
        var aliases = new Command("alias", "Inspect or conditionally change an exact Provider alias.");
        aliases.SetAction(_ => context.MissingSubcommand());
        var getAlias = new Argument<string>("alias");
        var aliasGet = new Command("get", "Get an exact alias binding.") { getAlias };
        aliasGet.SetAction((parse, token) => context.ExecuteSecretAsync(parse.GetValue(environment), async (client, env, ct) =>
            context.PrintProviderAccess("provider alias get", await client.GetProviderAliasAsync(new GetProviderAliasRequest
            { Environment = env, Alias = parse.GetRequiredValue(getAlias) }, ct)), token));
        aliases.Subcommands.Add(aliasGet);
        var setAlias = new Argument<string>("alias");
        var provider = new Option<string?>("--provider");
        var instance = new Option<string?>("--provider-instance-id");
        var targetRevision = new Option<long>("--revision") { Required = true };
        var absent = new Option<bool>("--expect-absent");
        var oldProvider = new Option<string?>("--expected-provider");
        var oldInstance = new Option<string?>("--expected-instance-id");
        var oldRevision = new Option<long?>("--expected-revision");
        var aliasSet = new Command("set", "Repeat preflight and atomically compare and set a binding.")
            { setAlias, provider, instance, targetRevision, absent, oldProvider, oldInstance, oldRevision };
        aliasSet.SetAction((parse, token) => context.SetProviderAliasAsync(parse.GetValue(environment), parse.GetRequiredValue(setAlias),
            parse.GetValue(provider), parse.GetValue(instance), parse.GetValue(targetRevision), parse.GetValue(absent),
            parse.GetValue(oldProvider), parse.GetValue(oldInstance), parse.GetValue(oldRevision), token));
        aliases.Subcommands.Add(aliasSet);
        group.Subcommands.Add(aliases);
        return group;
    }
}
