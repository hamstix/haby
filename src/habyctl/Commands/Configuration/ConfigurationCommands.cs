using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Configuration;

internal static class ConfigurationCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("configuration", "Materialize unresolved documents and inspect saved redacted artifacts.");
        group.SetAction(_ => context.MissingSubcommand());
        foreach (var name in new[] { "materialize", "get", "preview" })
        {
            var application = new Argument<string>("application-id");
            var environment = new Option<string?>("--environment");
            var component = new Option<string>("--component") { Required = true };
            var document = new Option<string>("--document") { Required = true };
            var generation = new Option<long?>("--generation");
            var command = new Command(name, name == "materialize" ? "Persist one immutable unresolved document; no delivery or automatic retry." : "Read an existing artifact without rendering.")
            { application, environment, component, document, generation };
            command.SetAction((parse, ct) => context.ExecuteConfigurationDocumentAsync(name,
                new ConfigurationDocumentRequest
                {
                    ApplicationId = parse.GetRequiredValue(application),
                    ComponentId = parse.GetRequiredValue(component),
                    DocumentId = parse.GetRequiredValue(document)
                }, parse.GetValue(environment), parse.GetValue(generation), ct));
            group.Subcommands.Add(command);
        }
        return group;
    }
}
