using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Planning;

internal static class PlanningCommands
{
    internal static Command ChangeSet(CliCommandContext context)
    {
        var group = new Command("changeset", "Create and inspect durable deployment intent.");
        group.SetAction(_ => context.MissingSubcommand());
        var application = new Argument<string>("application-record-id");
        var revision = new Argument<long>("revision-number");
        var absence = new Option<bool>("--expected-absence") { Required = true, Description = "Require no existing deployment." };
        var create = new Command("create", "Create intent for an exact revision in the selected Environment.")
            { application, revision, absence };
        create.SetAction((parse, token) => context.ExecutePlanningAsync(async (client, environment, ct) =>
            context.PrintPlanning("changeset create", await client.CreateChangeSetAsync(new CreateChangeSetRequest
            {
                EnvironmentId = environment,
                Reference = new RevisionReference
                {
                    ApplicationRecordId = parse.GetRequiredValue(application),
                    RevisionNumber = parse.GetRequiredValue(revision)
                },
                Precondition = parse.GetValue(absence) ? PlanningPrecondition.ExpectedAbsence : PlanningPrecondition.Unspecified
            }, ct)), token));
        group.Subcommands.Add(create);
        group.Subcommands.Add(ForChangeSet("get", "changeset get", context,
            (client, request, ct) => client.GetChangeSetAsync(request, ct)));
        return group;
    }

    internal static Command Plan(CliCommandContext context)
    {
        var group = new Command("plan", "Create, inspect, check and close durable Plans.");
        group.SetAction(_ => context.MissingSubcommand());
        group.Subcommands.Add(ForChangeSet("create", "plan create", context,
            (client, request, ct) => client.CreatePlanAsync(request, ct)));
        group.Subcommands.Add(ForPlan("get", context, (client, request, ct) => client.GetPlanAsync(request, ct)));
        group.Subcommands.Add(ForPlan("check", context, (client, request, ct) => client.CheckPlanAsync(request, ct)));
        group.Subcommands.Add(ForPlan("close", context, (client, request, ct) => client.ClosePlanAsync(request, ct)));
        return group;
    }

    private static Command ForChangeSet(string name, string path, CliCommandContext context,
        Func<IRegistryClient, ChangeSetRequest, CancellationToken, Task<PlanningReply>> call)
    {
        var id = new Argument<string>("changeset-id");
        var command = new Command(name) { id };
        command.SetAction((parse, token) => context.ExecutePlanningAsync(async (client, _, ct) =>
            context.PrintPlanning(path, await call(client,
                new ChangeSetRequest { ChangeSetId = parse.GetRequiredValue(id) }, ct)), token));
        return command;
    }

    private static Command ForPlan(string name, CliCommandContext context,
        Func<IRegistryClient, PlanRequest, CancellationToken, Task<PlanningReply>> call)
    {
        var id = new Argument<string>("plan-id");
        var command = new Command(name) { id };
        command.SetAction((parse, token) => context.ExecutePlanningAsync(async (client, _, ct) =>
            context.PrintPlanning("plan " + name, await call(client,
                new PlanRequest { PlanId = parse.GetRequiredValue(id) }, ct)), token));
        return command;
    }
}
