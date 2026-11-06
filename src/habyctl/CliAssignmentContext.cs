using Google.Protobuf;
using Grpc.Core;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed partial class CliCommandContext
{
    internal int PrintAssignment(string command, ProfileAssignmentReply reply) => jsonOutput()
        ? jsonPresenter.PrintAssignment(command, reply) : presenter.PrintAssignment(command, reply);
    internal Task<int> ValidateAssignmentAsync(string file, CancellationToken token) => ExecuteAsync(async (client, ct) =>
        PrintAssignment("profile assignment validate", await client.ValidateProfileAssignmentAsync(new()
        { Payload = ByteString.CopyFrom(await CliApplication.ReadEnvelopeAsync(file, ct)) }, ct)), token);

    internal Task<int> GetAssignmentAsync(string? environment, string? application, string? component, CancellationToken token)
    {
        if (component is not null && application is null)
        {
            error.WriteLine("--component requires --application.");
            return Task.FromResult(CliApplication.UsageError);
        }
        return ExecuteSecretAsync(environment, async (client, env, ct) =>
        {
            var target = new ProfileAssignmentTarget
            {
                Kind = component is not null ? "component" : application is not null ? "application" : "environment",
                ApplicationId = application ?? "",
                ComponentRef = component ?? ""
            };
            var reply = await client.GetProfileAssignmentAsync(new() { Environment = env, Target = target }, ct);
            AssignmentPresentation.CheckScope(reply, env, target);
            return PrintAssignment("profile assignment get", reply);
        }, token);
    }
    internal async Task<int> ApplyAssignmentAsync(string file, string? environment, string? version, bool absent, string? match, CancellationToken token)
    {
        if (absent && match is not null || match is not null && !AssignmentPresentation.ValidToken(match))
        {
            error.WriteLine("Use exactly one of --if-absent or --if-match with a nonempty UUID token.");
            return CliApplication.UsageError;
        }
        var writeStarted = false;
        var result = await ExecuteSecretAsync(environment, async (client, env, ct) =>
        {
            var payload = ByteString.CopyFrom(await CliApplication.ReadEnvelopeAsync(file, ct));
            var request = new ApplyProfileAssignmentRequest { Payload = payload, Environment = env };
            if (version is not null) request.ApplicationVersion = version;
            // Always obtain the authoritative target, including with an explicit precondition.
            // Only precondition acquisition is skipped; portable parsing stays on the server.
            var validation = await client.ValidateProfileAssignmentAsync(new() { Payload = payload }, ct);
            AssignmentPresentation.Check("profile assignment validate", validation);
            if (validation.Status != ProfileAssignmentStatus.Valid) return PrintAssignment("profile assignment apply", validation);
            var target = validation.Target;
            if (absent) request.ExpectedAbsence = true;
            else if (match is not null) request.ExpectedConcurrencyToken = match;
            else
            {
                var current = await client.GetProfileAssignmentAsync(new() { Environment = env, Target = target }, ct);
                AssignmentPresentation.Check("profile assignment get", current);
                AssignmentPresentation.CheckScope(current, env, target);
                if (current.Status == ProfileAssignmentStatus.Found) request.ExpectedConcurrencyToken = current.Assignment.ConcurrencyToken;
                else if (current.Status == ProfileAssignmentStatus.NotFound) request.ExpectedAbsence = true;
                else return PrintAssignment("profile assignment apply", current);
            }
            writeStarted = true;
            try
            {
                var reply = await client.ApplyProfileAssignmentAsync(request, ct);
                AssignmentPresentation.CheckScope(reply, env, target);
                return PrintAssignment("profile assignment apply", reply);
            }
            catch (Exception failure) when (failure is RpcException or HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
            {
                error.WriteLine(CliApplication.SafeError(failure));
                return CliApplication.TransportError;
            }
        }, token);
        if (writeStarted && result == CliApplication.TransportError)
            error.WriteLine("Assignment outcome is uncertain. Read the exact scope with profile assignment get and compare decisions. Equality proves current state, not which request wrote it. Do not blindly resubmit; no automatic retry was performed.");
        return result;
    }
}
