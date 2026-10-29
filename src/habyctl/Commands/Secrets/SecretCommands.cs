using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Secrets;

internal static class SecretCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("secret", "Create administrative secrets and discover safe metadata.");
        var environment = new Option<string?>("--environment") { Recursive = true, Description = "Existing Environment name; overrides selected context." };
        group.Options.Add(environment);
        group.SetAction(_ => context.MissingSubcommand());
        var name = new Argument<string>("name");
        var stdin = new Option<bool>("--stdin") { Description = "Read exact secret bytes from stdin until EOF." };
        var requestId = new Option<string?>("--request-id") { Description = "UUID for durable retry; generated once when omitted." };
        var create = new Command("create", "Create a named Provider administrator secret, version 1.") { name, stdin, requestId };
        create.SetAction((parse, token) => context.CreateSecretAsync(parse.GetRequiredValue(name),
            parse.GetValue(environment), parse.GetValue(stdin), parse.GetValue(requestId), token));
        group.Subcommands.Add(create);

        var selection = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne };
        var id = new Option<string?>("--id") { Description = "Internal Secret identity instead of name." };
        var get = new Command("get", "Get metadata by name or --id; never returns material.") { selection, id };
        get.SetAction((parse, token) => context.ExecuteSecretAsync(parse.GetValue(environment), async (client, env, ct) =>
            context.PrintSecret("secret get", await client.GetSecretAsync(new GetSecretRequest
            { Environment = env, Name = parse.GetValue(selection) ?? "", SecretId = parse.GetValue(id) ?? "" }, ct)), token));
        group.Subcommands.Add(get);
        var size = new Option<int>("--page-size") { Description = "Page size 1..100; default 50." };
        var cursor = new Option<string?>("--cursor") { Description = "Secret continuation returned by the registry." };
        var list = new Command("list", "List one page of safe secret metadata in ordinal name order.") { size, cursor };
        list.SetAction((parse, token) => context.ExecuteSecretAsync(parse.GetValue(environment), async (client, env, ct) =>
            context.PrintSecrets(await client.ListSecretsAsync(new ListSecretsRequest
            { Environment = env, PageSize = parse.GetValue(size), Cursor = parse.GetValue(cursor) ?? "" }, ct)), token));
        group.Subcommands.Add(list);
        return group;
    }
}
