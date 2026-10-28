using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Operations;

internal static class OperationCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("operation", "Explicitly execute or inspect an exact committed operation.");
        group.SetAction(_ => context.MissingSubcommand());
        foreach (var execute in new[] { true, false })
        {
            var deployment = new Argument<string>("deployment-id");
            var generation = new Argument<long>("generation");
            var resource = new Argument<string>("resource-id");
            var action = new Option<string>("--action") { Required = true, Description = "Exact action (create)." };
            var command = new Command(execute ? "execute" : "get", execute
                ? "Execute committed work. A lost response does not prove rollback; inspect before retrying."
                : "Read state and attempt history without dispatch, recovery, or adoption.")
                { deployment, generation, resource, action };
            command.SetAction((parse, token) =>
            {
                if (parse.GetRequiredValue(action) != "create") return Task.FromResult(context.InvalidOperationAction());
                var request = new OperationRequest
                {
                    DeploymentId = parse.GetRequiredValue(deployment),
                    Generation = parse.GetRequiredValue(generation),
                    ResourceId = parse.GetRequiredValue(resource),
                    Action = OperationAction.Create
                };
                return context.ExecuteAsync(async (client, ct) => context.PrintOperation("operation " + command.Name,
                    execute ? await client.ExecuteOperationAsync(request, ct) : await client.GetOperationAsync(request, ct)), token);
            });
            group.Subcommands.Add(command);
        }
        return group;
    }
}
