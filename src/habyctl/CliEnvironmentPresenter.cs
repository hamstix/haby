using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliEnvironmentData(string RecordId, string Name);
internal sealed record CliEnvironmentListData(CliEnvironmentData[] Items, string? NextCursor);

internal static class EnvironmentPresentation
{
    internal static string Outcome(EnvironmentStatus status) => status switch
    {
        EnvironmentStatus.Created => "created",
        EnvironmentStatus.Existing => "existing",
        EnvironmentStatus.Found => "found",
        EnvironmentStatus.NotFound => "notFound",
        EnvironmentStatus.InvalidName => "invalidName",
        EnvironmentStatus.Listed => "listed",
        EnvironmentStatus.InvalidCursor => "invalidCursor",
        EnvironmentStatus.InvalidPageSize => "invalidPageSize",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static int Exit(EnvironmentReply reply)
    {
        if (reply.Status is EnvironmentStatus.Created or EnvironmentStatus.Existing or EnvironmentStatus.Found)
        {
            if (reply.Environment is null) throw new InvalidDataException("Incomplete Environment result.");
            return 0;
        }
        if (reply.Status is EnvironmentStatus.InvalidName or EnvironmentStatus.NotFound) return 1;
        throw new InvalidDataException("Unknown server outcome.");
    }

    internal static int Exit(ListEnvironmentsReply reply) => reply.Status switch
    {
        EnvironmentStatus.Listed => 0,
        EnvironmentStatus.InvalidCursor or EnvironmentStatus.InvalidPageSize => 1,
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static CliEnvironmentData Data(EnvironmentMetadata metadata) => new(metadata.RecordId, metadata.Name);
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintEnvironment(string command, EnvironmentReply reply)
    {
        var exit = EnvironmentPresentation.Exit(reply);
        Write(command, EnvironmentPresentation.Outcome(reply.Status), reply.Environment is null ? null : EnvironmentPresentation.Data(reply.Environment),
            NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliEnvironmentData);
        return exit;
    }

    internal int PrintEnvironments(ListEnvironmentsReply reply)
    {
        var exit = EnvironmentPresentation.Exit(reply);
        Write("environment list", EnvironmentPresentation.Outcome(reply.Status),
            new CliEnvironmentListData([.. reply.Items.Select(EnvironmentPresentation.Data)],
                string.IsNullOrEmpty(reply.NextCursor) ? null : reply.NextCursor),
            NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliEnvironmentListData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal int PrintEnvironment(string command, EnvironmentReply reply)
    {
        var exit = EnvironmentPresentation.Exit(reply);
        _output.WriteLine(EnvironmentPresentation.Outcome(reply.Status));
        if (reply.Environment is { } value) _output.WriteLine($"{value.Name} {value.RecordId}");
        return exit;
    }

    internal int PrintEnvironments(ListEnvironmentsReply reply)
    {
        var exit = EnvironmentPresentation.Exit(reply);
        _output.WriteLine(EnvironmentPresentation.Outcome(reply.Status));
        foreach (var value in reply.Items) _output.WriteLine($"{value.Name} {value.RecordId}");
        if (!string.IsNullOrEmpty(reply.NextCursor)) _output.WriteLine($"nextCursor: {reply.NextCursor}");
        return exit;
    }
}
