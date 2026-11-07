using System.CommandLine;
using Haby.Operator.Contracts;

namespace Haby.Cli.Commands.Configuration;

internal static class ConfigurationAccessCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("access", "Issue, inspect or revoke exact-document delivery credentials.");
        group.SetAction(_ => context.MissingSubcommand());
        var application = new Argument<string>("application-id");
        var environment = new Option<string?>("--environment");
        var component = new Option<string>("--component") { Required = true };
        var document = new Option<string>("--document") { Required = true };
        var generation = new Option<long?>("--generation");
        var expiry = new Option<string?>("--expires-at");
        var file = new Option<string?>("--token-output-file");
        var stdout = new Option<bool>("--token-stdout");
        var create = new Command("create", "Create a new credential once; exactly one explicit sensitive output is required.")
        { application, environment, component, document, generation, expiry, file, stdout };
        create.SetAction((parse, ct) => context.CreateConfigurationAccessAsync(new ConfigurationDocumentRequest
        {
            ApplicationId = parse.GetRequiredValue(application),
            ComponentId = parse.GetRequiredValue(component),
            DocumentId = parse.GetRequiredValue(document)
        }, parse.GetValue(environment), parse.GetValue(generation), parse.GetValue(expiry), parse.GetValue(file), parse.GetValue(stdout), ct));
        group.Subcommands.Add(create);
        foreach (var name in new[] { "get", "revoke" })
        {
            var id = new Argument<string>("grant-id");
            var command = new Command(name, "Safe grant metadata only.") { id };
            command.SetAction((parse, ct) => context.ConfigurationAccessAsync(name, parse.GetRequiredValue(id), ct));
            group.Subcommands.Add(command);
        }
        return group;
    }
}
