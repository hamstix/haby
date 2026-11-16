using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli.Commands.Planning;

internal static class ObservationCommands
{
    internal static Command WaitBatch(CliCommandContext context)
    {
        var file = new Option<string>("--file") { Required = true, Description = "Bounded JSON with explicit exact deploymentId/generation members." };
        var request = new Option<string?>("--request-id") { Description = "Correlation UUID; generated once. Identical repeats may replay interrupted history, not resume it." };
        var command = new Command("wait-batch", "Observe an explicit bounded batch through one Host RPC.") { file, request };
        command.SetAction((parse, token) => context.ObserveBatchAsync(parse.GetRequiredValue(file), parse.GetValue(request), token));
        return command;
    }
    internal static Command Wait(CliCommandContext context)
    {
        var id = new Argument<string>("deployment-id"); var generation = new Argument<long>("generation");
        var request = Request();
        var calls = new Option<int?>("--maximum-calls"); var rounds = new Option<int?>("--maximum-rounds");
        var read = new Option<int?>("--read-timeout-ms"); var poll = new Option<int?>("--poll-ms"); var budget = new Option<int?>("--request-ms");
        var command = new Command("wait", "Observe readiness of an exact submitted generation; no submission, retirement or Applied completion.")
            { id, generation, request, calls, rounds, read, poll, budget };
        command.SetAction((parse, token) =>
        {
            var settings = new GenerationObservationSettings();
            if (parse.GetValue(calls) is { } c) settings.MaximumCalls = c;
            if (parse.GetValue(rounds) is { } r) settings.MaximumRounds = r;
            if (parse.GetValue(read) is { } t) settings.ReadTimeoutMilliseconds = t;
            if (parse.GetValue(poll) is { } p) settings.PollMilliseconds = p;
            if (parse.GetValue(budget) is { } b) settings.RequestMilliseconds = b;
            return context.ObserveGenerationAsync(parse.GetRequiredValue(id), parse.GetRequiredValue(generation), parse.GetValue(request), settings, null, token);
        });
        return command;
    }
    internal static Command Resume(CliCommandContext context)
    {
        var id = new Argument<string>("deployment-id"); var generation = new Argument<long>("generation"); var request = Request();
        var members = new Option<string[]>("--member")
        {
            Required = true,
            AllowMultipleArgumentsPerToken = true,
            Description = "Exact Workload/<component>/<workload>=<current-expired-attempt-UUID>; repeat for each member."
        };
        var command = new Command("resume", "Renew only explicitly selected expired observation attempts, then return. Use a separate wait to observe readiness.")
            { id, generation, request, members };
        command.SetAction((parse, token) => context.ObserveGenerationAsync(parse.GetRequiredValue(id), parse.GetRequiredValue(generation),
            parse.GetValue(request), null, parse.GetValue(members) ?? [], token));
        return command;
    }
    private static Option<string?> Request() => new("--request-id") { Description = "Nonempty UUID for idempotent retry; generated once when omitted. Reuse exact inputs after an uncertain response." };
}
