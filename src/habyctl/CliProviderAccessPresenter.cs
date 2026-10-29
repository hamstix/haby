using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliProviderAliasData(string Alias, string InstanceId, long Revision);
internal sealed record CliProviderAccessData(string? Preflight, CliProviderAliasData? Binding);
internal static class ProviderAccessPresentation
{
    internal static string Outcome(ProviderAccessStatus value) => value switch
    {
        ProviderAccessStatus.Ready => "ready",
        ProviderAccessStatus.Found => "found",
        ProviderAccessStatus.NotFound => "notFound",
        ProviderAccessStatus.Changed => "changed",
        ProviderAccessStatus.Unchanged => "unchanged",
        ProviderAccessStatus.InvalidRequest => "invalidRequest",
        ProviderAccessStatus.EnvironmentNotFound => "environmentNotFound",
        ProviderAccessStatus.TargetUnavailable => "targetUnavailable",
        ProviderAccessStatus.PreflightFailed => "preflightFailed",
        ProviderAccessStatus.StaleExpectation => "staleExpectation",
        ProviderAccessStatus.InstanceAlreadyBound => "instanceAlreadyBound",
        ProviderAccessStatus.StorageUnavailable => "storageUnavailable",
        _ => throw new InvalidDataException("Unknown Provider access outcome.")
    };
    internal static string Preflight(ProviderPreflightStatus value) => value switch
    {
        ProviderPreflightStatus.Ready => "ready",
        ProviderPreflightStatus.RevisionUnavailable => "revisionUnavailable",
        ProviderPreflightStatus.SecretUnavailable => "secretUnavailable",
        ProviderPreflightStatus.AuthenticationFailed => "authenticationFailed",
        ProviderPreflightStatus.ServerUnavailable => "serverUnavailable",
        ProviderPreflightStatus.InsufficientPrivilege => "insufficientPrivilege",
        ProviderPreflightStatus.ConfigurationUnavailable => "configurationUnavailable",
        ProviderPreflightStatus.DeadlineExceeded => "deadlineExceeded",
        ProviderPreflightStatus.HandlerUnavailable => "handlerUnavailable",
        _ => throw new InvalidDataException("Unknown Provider preflight outcome.")
    };
    internal static int Exit(ProviderAccessStatus value) => value is ProviderAccessStatus.Ready or ProviderAccessStatus.Found or ProviderAccessStatus.Changed or ProviderAccessStatus.Unchanged ? 0 : 1;
    internal static CliProviderAccessData Data(string command, ProviderAccessReply reply)
    {
        _ = Outcome(reply.Status);
        if (command == "provider check" ? reply.Status is not (ProviderAccessStatus.Ready or ProviderAccessStatus.InvalidRequest or
            ProviderAccessStatus.EnvironmentNotFound or ProviderAccessStatus.TargetUnavailable or ProviderAccessStatus.PreflightFailed or ProviderAccessStatus.StorageUnavailable) :
            command == "provider alias get" ? reply.Status is not (ProviderAccessStatus.Found or ProviderAccessStatus.NotFound or ProviderAccessStatus.InvalidRequest or
            ProviderAccessStatus.EnvironmentNotFound or ProviderAccessStatus.StorageUnavailable) :
            reply.Status is ProviderAccessStatus.Ready or ProviderAccessStatus.Found or ProviderAccessStatus.NotFound)
            throw new InvalidDataException("Unexpected Provider access outcome.");
        if ((reply.Status == ProviderAccessStatus.Ready && (!reply.HasPreflight || reply.Preflight != ProviderPreflightStatus.Ready)) ||
            (reply.Status == ProviderAccessStatus.PreflightFailed && (!reply.HasPreflight || reply.Preflight == ProviderPreflightStatus.Ready)))
            throw new InvalidDataException("Incomplete preflight result.");
        var bound = reply.Status is ProviderAccessStatus.Found or ProviderAccessStatus.Changed or ProviderAccessStatus.Unchanged;
        if (bound != (reply.Binding is not null)) throw new InvalidDataException("Incomplete alias result.");
        CliProviderAliasData? binding = null;
        if (reply.Binding is { } b)
        {
            if (!Guid.TryParseExact(b.InstanceId, "D", out var id) || id.Version != 7 || b.InstanceId != id.ToString() ||
                b.InstanceId[19] is not ('8' or '9' or 'a' or 'b') || b.Revision <= 0 || b.Alias.Length is < 1 or > 128 || b.Alias.Any(char.IsControl))
                throw new InvalidDataException("Invalid alias metadata.");
            binding = new(b.Alias, b.InstanceId, b.Revision);
        }
        return new(reply.HasPreflight ? Preflight(reply.Preflight) : null, binding);
    }
}
internal sealed partial class CliJsonPresenter
{
    internal int PrintProviderAccess(string command, ProviderAccessReply reply)
    {
        var data = ProviderAccessPresentation.Data(command, reply);
        Write(command, ProviderAccessPresentation.Outcome(reply.Status), data, NoDiagnostics,
            CliOutputJsonContext.Default.CliEnvelopeCliProviderAccessData);
        return ProviderAccessPresentation.Exit(reply.Status);
    }
}
internal sealed partial class CliTextPresenter
{
    internal int PrintProviderAccess(string command, ProviderAccessReply reply)
    {
        var data = ProviderAccessPresentation.Data(command, reply);
        _output.WriteLine(ProviderAccessPresentation.Outcome(reply.Status));
        if (data.Preflight is not null) _output.WriteLine($"preflight: {data.Preflight}");
        if (data.Binding is { } b) _output.WriteLine($"{b.Alias} {b.InstanceId} revision {b.Revision}");
        return ProviderAccessPresentation.Exit(reply.Status);
    }
}
