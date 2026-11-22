using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Planning;

internal static class ResourceImportCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("import", "Prepare names on the server and create or inspect import intent; execution remains explicit.");
        group.SetAction(_ => context.MissingSubcommand());
        var application = new Argument<string>("application-record-id");
        var revision = new Argument<long>("revision-number");
        var input = new Option<string>("--input") { Required = true, Description = "Closed JSON {format,input} names document; no password or client OIDs." };
        var environment = new Option<string?>("--environment") { Description = "Existing Environment name; overrides the selected context." };
        var record = new Option<string?>("--environment-record-id") { Description = "Exact Environment UUID, exclusive with --environment." };
        var requestId = new Option<string?>("--request-id") { Description = "Reuse this UUID only with the exact original input/password; otherwise generated once per invocation." };
        var stdin = new Option<bool>("--password-stdin") { Description = "Read exact password UTF-8 bytes from stdin to EOF, without trimming." };
        var previous = new Option<string?>("--corrects-request") { Description = "Explicit previous unconfirmed import request UUID." };
        var reason = new Option<string?>("--correction-reason") { Description = "credential-rejected, input-correction or explicit-retry." };
        var absence = new Option<bool>("--expected-uncommitted") { Description = "Require that the previous request has no Commit." };
        var deployment = new Option<string?>("--expected-deployment") { Description = "Exact Deployment UUID for a committed failed/unconfirmed import." };
        var generation = new Option<long?>("--expected-generation") { Description = "Exact generation of that previous import." };
        var create = new Command("create", "Create only a ChangeSet; never implicitly Plan, Commit or Execute.")
            { application, revision, input, environment, record, requestId, stdin, previous, reason, absence, deployment, generation };
        create.Validators.Add(result =>
        {
            if (result.GetValue(environment) is not null && result.GetValue(record) is not null)
                result.AddError("Select --environment or --environment-record-id, exclusively.");
            if (result.GetRequiredValue(revision) <= 0) result.AddError("Revision number must be positive.");
            var correcting = result.GetValue(previous) is not null;
            var committed = result.GetValue(deployment) is not null || result.GetValue(generation) is not null;
            if (correcting ? result.GetValue(reason) is null || result.GetValue(absence) == committed ||
                    committed && (result.GetValue(deployment) is null || result.GetValue(generation) is not > 0) :
                    result.GetValue(reason) is not null || result.GetValue(absence) || result.GetValue(deployment) is not null || result.GetValue(generation) is not null)
                result.AddError("Correction requires --corrects-request, --correction-reason and exactly --expected-uncommitted or the exact --expected-deployment/--expected-generation pair.");
        });
        create.SetAction((parse, token) => context.CreateResourceImportAsync(parse.GetRequiredValue(application), parse.GetRequiredValue(revision),
            parse.GetRequiredValue(input), parse.GetValue(environment), parse.GetValue(record), parse.GetValue(requestId), parse.GetValue(stdin),
            parse.GetValue(previous), parse.GetValue(reason), parse.GetValue(absence), parse.GetValue(deployment), parse.GetValue(generation), token));
        var id = new Argument<string>("request-id");
        var get = new Command("get", "Read safe request metadata; UUID knowledge does not grant authorization.") { id };
        get.SetAction((parse, token) => context.GetResourceImportAsync(parse.GetRequiredValue(id), token));
        group.Subcommands.Add(create); group.Subcommands.Add(get);
        return group;
    }
}
