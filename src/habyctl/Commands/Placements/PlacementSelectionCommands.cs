using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Placements;

internal static class PlacementSelectionCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("selection", "Conditionally select one whole exact Placement revision.");
        group.SetAction(_ => context.MissingSubcommand());
        foreach (var apply in new[] { false, true })
        {
            var kind = new Argument<string>("kind");
            var alias = new Option<string>("--provider-alias") { Required = true };
            var selector = new Option<string>("--selector");
            var app = new Option<string>("--application");
            var component = new Option<string>("--component");
            var placement = new Option<string>("--placement") { Required = true };
            var revision = new Option<int>("--revision") { Required = true };
            var version = new Option<string>("--application-version");
            var absence = new Option<bool>("--expected-absence");
            var expected = new Option<string>("--expected-token");
            var command = new Command(apply ? "apply" : "get", apply ? "Apply with explicit expected absence or current token." : "Get exact selection, token and original audit.") { kind };
            foreach (var option in new Option[] { alias, selector, app, component }) command.Options.Add(option);
            if (apply) foreach (var option in new Option[] { placement, revision, version, absence, expected }) command.Options.Add(option);
            command.Validators.Add(result =>
            {
                var k = result.GetValue(kind); var s = result.GetValue(selector); var a = result.GetValue(app); var c = result.GetValue(component);
                var valid = k switch
                {
                    "named" => !string.IsNullOrEmpty(s) && a is null && c is null,
                    "environment" => s is null && a is null && c is null,
                    "application" => s is null && !string.IsNullOrEmpty(a) && c is null,
                    "component" => s is null && !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(c),
                    _ => false
                };
                if (!valid) result.AddError("Invalid Placement selection target options.");
                if (apply && ((k == "component" ? string.IsNullOrEmpty(result.GetValue(version)) : result.GetValue(version) is not null) ||
                    result.GetValue(revision) <= 0 || (result.GetResult(absence) is { Implicit: false }) == (result.GetResult(expected) is { Implicit: false }) ||
                    result.GetResult(absence) is { Implicit: false } && !result.GetValue(absence) ||
                    result.GetValue(expected) is { } t && (!Guid.TryParseExact(t, "D", out var id) || id == Guid.Empty)))
                    result.AddError("An exact revision, applicable validation version and exactly one valid precondition are required.");
            });
            command.SetAction((parse, ct) => context.ExecutePlanningAsync(async (client, environment, token) =>
            {
                var target = new PlacementSelectionTarget
                {
                    Kind = parse.GetRequiredValue(kind),
                    Selector = parse.GetValue(selector) ?? "",
                    ApplicationId = parse.GetValue(app) ?? "",
                    ComponentRef = parse.GetValue(component) ?? ""
                };
                PlacementSelectionReply reply;
                if (apply)
                {
                    var request = new ApplyPlacementSelectionRequest
                    {
                        Environment = environment,
                        ProviderAlias = parse.GetRequiredValue(alias),
                        Target = target,
                        PlacementName = parse.GetRequiredValue(placement),
                        Revision = parse.GetRequiredValue(revision)
                    };
                    if (parse.GetValue(version) is { } v) request.ApplicationVersion = v;
                    if (parse.GetValue(absence)) request.ExpectedAbsence = true; else request.ExpectedToken = parse.GetRequiredValue(expected);
                    reply = await client.ApplyPlacementSelectionAsync(request, token);
                    if (reply.Selection?.Reference is { } accepted && (accepted.Name != request.PlacementName || accepted.Revision != request.Revision))
                        throw new InvalidDataException("Inconsistent Placement selection reference.");
                }
                else reply = await client.GetPlacementSelectionAsync(new() { Environment = environment, ProviderAlias = parse.GetRequiredValue(alias), Target = target }, token);
                return context.PrintPlacementSelection("placement selection " + (apply ? "apply" : "get"), reply, environment, parse.GetRequiredValue(alias), target);
            }, ct));
            group.Subcommands.Add(command);
        }
        return group;
    }
}
