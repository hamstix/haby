using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Configuration;

internal static class ConfigurationCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("configuration", "Materialize unresolved documents and inspect saved redacted artifacts.");
        group.Subcommands.Add(ConfigurationAccessCommands.Create(context));
        group.SetAction(_ => context.MissingSubcommand());
        foreach (var name in new[] { "materialize", "get", "preview" })
        {
            var application = new Argument<string>("application-id");
            var environment = new Option<string?>("--environment");
            var component = new Option<string?>("--component") { Required = name != "materialize" };
            var document = new Option<string?>("--document") { Required = name != "materialize" };
            var generation = new Option<long?>("--generation");
            var command = new Command(name, name == "materialize" ? "Persist one immutable unresolved document; no delivery or automatic retry." : "Read an existing artifact without rendering.")
            { application, environment, component, document, generation };
            var all = new Option<bool>("--all") { Description = "Materialize every frozen document in one request, stopping on the first failure." };
            if (name == "materialize")
            {
                command.Options.Add(all);
                command.Validators.Add(result =>
                {
                    var batch = result.GetValue(all);
                    var hasComponent = result.GetValue(component) is not null;
                    var hasDocument = result.GetValue(document) is not null;
                    if (batch ? hasComponent || hasDocument : !hasComponent || !hasDocument)
                        result.AddError("Select --all or both --component and --document.");
                });
            }
            command.SetAction((parse, ct) => parse.GetValue(all) ? context.ExecuteConfigurationApplicationAsync(parse.GetRequiredValue(application), parse.GetValue(environment), parse.GetValue(generation), ct) : context.ExecuteConfigurationDocumentAsync(name,
                new ConfigurationDocumentRequest
                {
                    ApplicationId = parse.GetRequiredValue(application),
                    ComponentId = parse.GetValue(component) ?? "",
                    DocumentId = parse.GetValue(document) ?? ""
                }, parse.GetValue(environment), parse.GetValue(generation), ct));
            group.Subcommands.Add(command);
        }
        return group;
    }
}
