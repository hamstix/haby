using System.CommandLine;

namespace Haby.Cli.Commands.Profiles;

internal static class AssignmentCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("assignment", "Administer conditional local Profile decisions.");
        group.SetAction(_ => context.MissingSubcommand());
        var validationFile = new Argument<string>("file");
        var validate = new Command("validate", "Validate only the portable declaration.") { validationFile };
        validate.SetAction((p, ct) => context.ValidateAssignmentAsync(p.GetRequiredValue(validationFile), ct));
        group.Subcommands.Add(validate);
        var file = new Argument<string>("file");
        var environment = new Option<string?>("--environment");
        var version = new Option<string?>("--application-version");
        var absent = new Option<bool>("--if-absent");
        var match = new Option<string?>("--if-match");
        var apply = new Command("apply", "Replace local decisions conditionally; no automatic conflict retry.") { file, environment, version, absent, match };
        apply.SetAction((p, ct) => context.ApplyAssignmentAsync(p.GetRequiredValue(file), p.GetValue(environment), p.GetValue(version),
            p.GetValue(absent), p.GetValue(match), ct));
        group.Subcommands.Add(apply);
        var getEnvironment = new Option<string?>("--environment");
        var application = new Option<string?>("--application");
        var component = new Option<string?>("--component");
        var get = new Command("get", "Read exact local decisions and their last-change audit.") { getEnvironment, application, component };
        get.SetAction((p, ct) => context.GetAssignmentAsync(p.GetValue(getEnvironment), p.GetValue(application), p.GetValue(component), ct));
        group.Subcommands.Add(get);
        return group;
    }
}
