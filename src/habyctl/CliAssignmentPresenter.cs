using System.Globalization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliAssignmentTarget(string Kind, string? ApplicationId, string? ComponentRef);
internal sealed record CliAssignmentDecision(string ProfileId, int? UseRevision, bool? Disable);
internal sealed record CliAssignmentState(string Environment, string EnvironmentRecordId, CliAssignmentTarget Target,
    CliAssignmentDecision[] Decisions, string ConcurrencyToken, string TechnicalActorId, string ChangedAtUtc, CliReference? CheckedRevision);
internal sealed record CliAssignmentData(CliAssignmentTarget? Target, CliAssignmentState? Assignment, int ErrorCount, bool DiagnosticsTruncated);

internal static class AssignmentPresentation
{
    internal static bool ValidToken(string token) => Guid.TryParseExact(token, "D", out var id) && id != Guid.Empty;
    private static bool Name(string value, int max) => value.Length <= max && ProfilePresentation.ValidProfileId(value) && !value.Contains('.');
    private static bool Target(ProfileAssignmentTarget? target) => target is not null && target.Kind switch
    {
        "environment" => target.ApplicationId == "" && target.ComponentRef == "",
        "application" => ProfilePresentation.ValidProfileId(target.ApplicationId) && target.ComponentRef == "",
        "component" => ProfilePresentation.ValidProfileId(target.ApplicationId) && Name(target.ComponentRef, 63),
        _ => false
    };
    internal static void CheckScope(ProfileAssignmentReply reply, string environment, ProfileAssignmentTarget target)
    {
        if (reply.Assignment is { } state && (state.Environment != environment || !Equals(state.Target, target)))
            throw new InvalidDataException("Foreign assignment reply.");
    }
    internal static (string Outcome, int Exit) Check(string command, ProfileAssignmentReply reply)
    {
        var status = reply.Status;
        var accepted = status is ProfileAssignmentStatus.Created or ProfileAssignmentStatus.Updated or ProfileAssignmentStatus.Unchanged or ProfileAssignmentStatus.Found;
        var invalid = status == ProfileAssignmentStatus.InvalidDeclaration;
        var allowed = command switch
        {
            "profile assignment validate" => status is ProfileAssignmentStatus.Valid or ProfileAssignmentStatus.InvalidDeclaration,
            "profile assignment get" => status is ProfileAssignmentStatus.Found or ProfileAssignmentStatus.NotFound or ProfileAssignmentStatus.InvalidContext or ProfileAssignmentStatus.EnvironmentNotFound,
            "profile assignment apply" => status is ProfileAssignmentStatus.Created or ProfileAssignmentStatus.Updated or ProfileAssignmentStatus.Unchanged or
                ProfileAssignmentStatus.InvalidDeclaration or ProfileAssignmentStatus.Conflict or ProfileAssignmentStatus.InvalidContext or
                ProfileAssignmentStatus.EnvironmentNotFound or ProfileAssignmentStatus.ApplicationNotFound or ProfileAssignmentStatus.ApplicationVersionNotFound or
                ProfileAssignmentStatus.ComponentNotFound or ProfileAssignmentStatus.ProfileRevisionNotFound,
            _ => false
        };
        if (!allowed || (accepted || status == ProfileAssignmentStatus.Valid ? !Target(reply.Target) : reply.Target is not null) ||
            (accepted ? reply.Assignment is null : reply.Assignment is not null)) throw new InvalidDataException("Invalid assignment reply.");
        if (invalid ? reply.ErrorCount <= 0 || reply.Diagnostics.Count is < 1 or > 100 || reply.ErrorCount < reply.Diagnostics.Count ||
                reply.DiagnosticsTruncated != (reply.ErrorCount > reply.Diagnostics.Count) || reply.Diagnostics.Any(x =>
                    x.Code.Length is 0 or > 128 || x.Code.Any(char.IsControl) || x.Path.Length > 1024 || x.Path.Any(char.IsControl) || x.Path.Length > 0 && !x.Path.StartsWith('/')) :
                reply.ErrorCount != 0 || reply.Diagnostics.Count != 0 || reply.DiagnosticsTruncated)
            throw new InvalidDataException("Invalid assignment diagnostics.");
        if (reply.Assignment is { } state)
        {
            if (!Equals(reply.Target, state.Target) || !Name(state.Environment, 64) || !ValidToken(state.EnvironmentRecordId) || !ValidToken(state.ConcurrencyToken) ||
                state.TechnicalActorId.Length is 0 or > 256 || state.TechnicalActorId.Any(char.IsControl) ||
                !DateTimeOffset.TryParseExact(state.ChangedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero ||
                state.Decisions.Count > 64 || state.Decisions.Select(x => x.ProfileId).Distinct(StringComparer.Ordinal).Count() != state.Decisions.Count ||
                state.Decisions.Any(x => !ProfilePresentation.ValidProfileId(x.ProfileId) || x.DecisionCase switch
                {
                    ProfileAssignmentDecision.DecisionOneofCase.UseRevision => x.UseRevision <= 0,
                    ProfileAssignmentDecision.DecisionOneofCase.Disable => !x.Disable,
                    _ => true
                }) || (state.Target.Kind == "component" ? state.CheckedRevision is null || !ValidToken(state.CheckedRevision.ApplicationRecordId) || state.CheckedRevision.RevisionNumber <= 0 : state.CheckedRevision is not null))
                throw new InvalidDataException("Invalid assignment state.");
        }
        var name = status.ToString();
        return (char.ToLowerInvariant(name[0]) + name[1..], accepted || status == ProfileAssignmentStatus.Valid ? 0 : 1);
    }
    internal static CliAssignmentTarget Map(ProfileAssignmentTarget target) => new(target.Kind,
        target.ApplicationId == "" ? null : target.ApplicationId, target.ComponentRef == "" ? null : target.ComponentRef);
    internal static CliAssignmentData Data(ProfileAssignmentReply reply) => new(reply.Target is { } t ? Map(t) : null,
        reply.Assignment is { } a ? new(a.Environment, a.EnvironmentRecordId, Map(a.Target),
            [.. a.Decisions.Select(x => new CliAssignmentDecision(x.ProfileId,
                x.DecisionCase == ProfileAssignmentDecision.DecisionOneofCase.UseRevision ? x.UseRevision : null,
                x.DecisionCase == ProfileAssignmentDecision.DecisionOneofCase.Disable ? true : null))],
            a.ConcurrencyToken, a.TechnicalActorId, a.ChangedAtUtc,
            a.CheckedRevision is { } r ? new(r.ApplicationRecordId, r.RevisionNumber) : null) : null,
        reply.ErrorCount, reply.DiagnosticsTruncated);
}
internal sealed partial class CliJsonPresenter
{
    internal int PrintAssignment(string command, ProfileAssignmentReply reply)
    {
        var (outcome, exit) = AssignmentPresentation.Check(command, reply);
        Write(command, outcome, AssignmentPresentation.Data(reply),
            [.. reply.Diagnostics.Select(x => new CliDiagnostic(x.Code, x.Path, "", "Error", [], null))],
            CliOutputJsonContext.Default.CliEnvelopeCliAssignmentData);
        return exit;
    }
}
internal sealed partial class CliTextPresenter
{
    internal int PrintAssignment(string command, ProfileAssignmentReply reply)
    {
        var (outcome, exit) = AssignmentPresentation.Check(command, reply);
        _output.WriteLine(outcome);
        if (reply.Assignment is { } a)
        {
            _output.WriteLine($"Environment: {a.Environment} ({a.EnvironmentRecordId}) target: {a.Target.Kind} {a.Target.ApplicationId} {a.Target.ComponentRef}");
            foreach (var d in a.Decisions) _output.WriteLine($"{d.ProfileId}: {(d.DecisionCase == ProfileAssignmentDecision.DecisionOneofCase.UseRevision ? "use " + d.UseRevision : "disable")}");
            _output.WriteLine($"concurrencyToken: {a.ConcurrencyToken}");
            _output.WriteLine($"technicalActorId: {a.TechnicalActorId} changedAtUtc: {a.ChangedAtUtc}");
            if (a.CheckedRevision is { } r) _output.WriteLine($"checkedRevision: {r.ApplicationRecordId}/{r.RevisionNumber}");
        }
        foreach (var d in reply.Diagnostics) _output.WriteLine($"{d.Code} {d.Path}");
        if (reply.DiagnosticsTruncated) _output.WriteLine($"Diagnostics truncated; {reply.ErrorCount} errors.");
        return exit;
    }
}
