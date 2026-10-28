using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Planning;

internal static class CommitCommands
{
    internal static Command Commit(CliCommandContext context)
    {
        var id = new Argument<string>("change-set-id");
        var plan = new Option<string>("--plan") { Required = true, Description = "Exact Plan ID to accept." };
        var command = new Command("commit", "Accept desired state and record Pending work; no Provider execution.") { id, plan };
        // The commit get subcommand has its own positional ID and does not require --plan.
        var getId = new Argument<string>("change-set-id");
        var get = new Command("get", "Read the original immutable desired-state commit.") { getId };
        get.SetAction((parse, token) => context.ExecutePlanningAsync(async (client, _, ct) =>
            context.PrintCommit("changeset commit get", await client.GetCommitAsync(new ChangeSetRequest
            { ChangeSetId = parse.GetRequiredValue(getId) }, ct)), token));
        command.Subcommands.Add(get);
        command.SetAction((parse, token) => context.ExecutePlanningAsync(async (client, _, ct) =>
            context.PrintCommit("changeset commit", await client.CommitDesiredStateAsync(new CommitDesiredStateRequest
            { ChangeSetId = parse.GetRequiredValue(id), PlanId = parse.GetRequiredValue(plan) }, ct)), token));
        return command;
    }

    internal static Command Deployment(CliCommandContext context)
    {
        var group = new Command("deployment", "Inspect accepted deployment desired state.");
        group.SetAction(_ => context.MissingSubcommand());
        var generation = new Command("generation", "Inspect immutable desired generations.");
        generation.SetAction(_ => context.MissingSubcommand());
        var id = new Argument<string>("deployment-id");
        var number = new Argument<long>("generation");
        var get = new Command("get", "Read a desired snapshot; this does not prove infrastructure was applied.") { id, number };
        get.SetAction((parse, token) => context.ExecutePlanningAsync(async (client, _, ct) =>
            context.PrintCommit("deployment generation get", await client.GetGenerationAsync(new GenerationRequest
            { DeploymentId = parse.GetRequiredValue(id), Generation = parse.GetRequiredValue(number) }, ct)), token));
        generation.Subcommands.Add(get);
        group.Subcommands.Add(generation);
        return group;
    }
}
