using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliCommit(string ChangeSetId, string PlanId, string DeploymentId, long Generation,
    string CommittedAtUtc, string TechnicalActorId);
internal sealed record CliDesiredGeneration(string DeploymentId, long Generation, string DesiredPresence,
    string ChangeSetId, string PlanId, string AcceptedAtUtc, string TechnicalActorId,
    string EnvironmentRecordId, string EnvironmentId, CliReference Reference, string ProviderAlias,
    string ProviderInstanceId, long ConfigurationRevision, string ImplementationId);
internal sealed record CliCommitData(string StateMeaning, CliCommit? Commit,
    CliDesiredGeneration? DesiredGeneration, long? ActualGeneration);

internal static class CommitPresentation
{
    internal static string Outcome(CommitStatus status) => status switch
    {
        CommitStatus.Committed => "committed",
        CommitStatus.Found => "found",
        CommitStatus.InvalidRequest => "invalidRequest",
        CommitStatus.ChangeSetNotFound => "changeSetNotFound",
        CommitStatus.PlanNotFound => "planNotFound",
        CommitStatus.PlanDoesNotBelongToChangeSet => "planDoesNotBelongToChangeSet",
        CommitStatus.Closed => "closed",
        CommitStatus.Expired => "expired",
        CommitStatus.ChangeSetStale => "changeSetStale",
        CommitStatus.PlanStale => "planStale",
        CommitStatus.PlanInputUnavailable => "planInputUnavailable",
        CommitStatus.UnsupportedPrecondition => "unsupportedPrecondition",
        CommitStatus.UnsupportedShape => "unsupportedShape",
        CommitStatus.CommitNotFound => "commitNotFound",
        CommitStatus.GenerationNotFound => "generationNotFound",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static int Exit(CommitReply reply)
    {
        _ = Outcome(reply.Status);
        if (reply.Status == CommitStatus.Committed && reply.Commit is null ||
            reply.Status == CommitStatus.Found && reply.Commit is null && reply.DesiredGeneration is null)
            throw new InvalidDataException("Missing server snapshot.");
        return reply.Status is CommitStatus.Committed or CommitStatus.Found ? CliApplication.Success : CliApplication.Rejected;
    }

    internal static CliCommitData Data(CommitReply reply) => new("desiredStateOnly",
        reply.Commit is not { } c ? null : new(c.ChangeSetId, c.PlanId, c.DeploymentId, c.Generation,
            c.CommittedAtUtc, c.TechnicalActorId),
        reply.DesiredGeneration is not { } g ? null : new(g.DeploymentId, g.Generation,
            g.DesiredPresence == "Present" ? "present" : throw new InvalidDataException("Unsupported desired presence."),
            g.ChangeSetId, g.PlanId, g.AcceptedAtUtc, g.TechnicalActorId, g.EnvironmentRecordId, g.EnvironmentId,
            g.Reference is { } r ? new(r.ApplicationRecordId, r.RevisionNumber) : throw new InvalidDataException("Missing revision."),
            g.ProviderAlias, g.ProviderInstanceId, g.ConfigurationRevision, g.ImplementationId),
        reply.HasActualGeneration ? reply.ActualGeneration : null);
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintCommit(string command, CommitReply reply)
    {
        var exit = CommitPresentation.Exit(reply);
        Write(command, CommitPresentation.Outcome(reply.Status), CommitPresentation.Data(reply), NoDiagnostics,
            CliOutputJsonContext.Default.CliEnvelopeCliCommitData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal int PrintCommit(string command, CommitReply reply)
    {
        var exit = CommitPresentation.Exit(reply);
        var data = CommitPresentation.Data(reply);
        _output.WriteLine(CommitPresentation.Outcome(reply.Status));
        if (data.Commit is { } c)
        {
            _output.WriteLine($"Desired state accepted: deployment {c.DeploymentId}; generation {c.Generation}");
            _output.WriteLine($"ChangeSet: {c.ChangeSetId}; original Plan: {c.PlanId}");
            _output.WriteLine($"Committed UTC: {c.CommittedAtUtc}; technical actor: {c.TechnicalActorId}");
            _output.WriteLine("Commit records Pending work. This snapshot does not confirm applied infrastructure.");
        }
        if (data.DesiredGeneration is { } g)
        {
            _output.WriteLine($"Desired generation: {g.DeploymentId} {g.Generation}; presence: {g.DesiredPresence}");
            _output.WriteLine($"ChangeSet: {g.ChangeSetId}; original Plan: {g.PlanId}");
            _output.WriteLine($"Environment: {g.EnvironmentId}; record: {g.EnvironmentRecordId}");
            _output.WriteLine($"Revision: {g.Reference.ApplicationRecordId} {g.Reference.RevisionNumber}");
            _output.WriteLine($"Provider: {g.ProviderAlias} -> {g.ProviderInstanceId} revision {g.ConfigurationRevision}; implementation: {g.ImplementationId}");
            _output.WriteLine($"Accepted UTC: {g.AcceptedAtUtc}; technical actor: {g.TechnicalActorId}");
            _output.WriteLine("This immutable desired snapshot does not confirm applied infrastructure.");
        }
        if (data.ActualGeneration is { } actual) _output.WriteLine($"Actual desired generation: {actual}");
        return exit;
    }
}
