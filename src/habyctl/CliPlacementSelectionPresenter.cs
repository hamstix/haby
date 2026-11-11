using System.Globalization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliPlacementSelectionTarget(string Kind, string? Selector, string? ApplicationId, string? ComponentRef);
internal sealed record CliPlacementSelectionData(CliPlacementSelectionTarget? Target, CliPlacementReference? Reference, string? EnvironmentRecordId,
    string? ConcurrencyToken, string? TechnicalActorId, string? ChangedAtUtc, CliPlacementCheckedRevision? CheckedRevision);
internal sealed record CliPlacementCheckedRevision(string ApplicationRecordId, long RevisionNumber);
internal sealed partial class CliCommandContext
{
    internal int PrintPlacementSelection(string command, PlacementSelectionReply reply, string environment, string alias, PlacementSelectionTarget target)
        => jsonOutput() ? jsonPresenter.PrintPlacementSelection(command, reply, environment, alias, target) : presenter.PrintPlacementSelection(command, reply, environment, alias, target);
}
internal static class PlacementSelectionPresentation
{
    internal static (string Outcome, int Exit, CliPlacementSelectionData Data) Check(string command, PlacementSelectionReply reply, string environment, string alias, PlacementSelectionTarget target)
    {
        var success = reply.Status is PlacementSelectionStatus.Created or PlacementSelectionStatus.Updated or PlacementSelectionStatus.Unchanged or PlacementSelectionStatus.Found;
        var allowed = command == "placement selection get" ? reply.Status is PlacementSelectionStatus.Found or PlacementSelectionStatus.NotFound or PlacementSelectionStatus.InvalidContext or PlacementSelectionStatus.EnvironmentNotFound :
            command == "placement selection apply" && reply.Status is PlacementSelectionStatus.Created or PlacementSelectionStatus.Updated or PlacementSelectionStatus.Unchanged or PlacementSelectionStatus.Conflict or
                PlacementSelectionStatus.InvalidContext or PlacementSelectionStatus.EnvironmentNotFound or PlacementSelectionStatus.ApplicationNotFound or
                PlacementSelectionStatus.ApplicationVersionNotFound or PlacementSelectionStatus.ComponentNotFound or PlacementSelectionStatus.RevisionNotFound;
        if (!allowed || success != (reply.Selection is not null)) throw Invalid();
        var s = reply.Selection;
        static bool Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
        if (s is not null && (!Equals(s.Target, target) || s.Reference is null || s.Reference.Environment != environment || s.Reference.ProviderAlias != alias ||
            s.Reference.Revision <= 0 || !ProfilePresentation.ValidProfileId(s.Reference.Name) || s.Reference.Name.Length > 63 || s.Reference.Name.Contains('.') ||
            !Id(s.EnvironmentRecordId) || !Id(s.ConcurrencyToken) || s.TechnicalActorId.Length is 0 or > 256 || s.TechnicalActorId.Any(char.IsControl) ||
            !DateTimeOffset.TryParseExact(s.ChangedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero ||
            (target.Kind == "component") != (s.CheckedRevision is not null) || s.CheckedRevision is { } r && (!Id(r.ApplicationRecordId) || r.RevisionNumber <= 0))) throw Invalid();
        var status = reply.Status.ToString();
        return (char.ToLowerInvariant(status[0]) + status[1..], success ? 0 : 1,
            new(s is null ? null : new(s.Target.Kind, Empty(s.Target.Selector), Empty(s.Target.ApplicationId), Empty(s.Target.ComponentRef)),
                s is null ? null : new(environment, alias, s.Reference.Name, s.Reference.Revision), s?.EnvironmentRecordId, s?.ConcurrencyToken, s?.TechnicalActorId, s?.ChangedAtUtc,
                s?.CheckedRevision is { } check ? new(check.ApplicationRecordId, check.RevisionNumber) : null));
    }
    private static string? Empty(string value) => value == "" ? null : value;
    private static InvalidDataException Invalid() => new("Invalid Placement selection response.");
}
internal sealed partial class CliJsonPresenter
{
    internal int PrintPlacementSelection(string command, PlacementSelectionReply reply, string environment, string alias, PlacementSelectionTarget target)
    {
        var (outcome, exit, data) = PlacementSelectionPresentation.Check(command, reply, environment, alias, target);
        Write(command, outcome, data, [], CliOutputJsonContext.Default.CliEnvelopeCliPlacementSelectionData); return exit;
    }
}
internal sealed partial class CliTextPresenter
{
    internal int PrintPlacementSelection(string command, PlacementSelectionReply reply, string environment, string alias, PlacementSelectionTarget target)
    {
        var (outcome, exit, _) = PlacementSelectionPresentation.Check(command, reply, environment, alias, target);
        _output.WriteLine(outcome);
        if (reply.Selection is { } s)
        {
            _output.WriteLine($"target: {s.Target.Kind} selector: {s.Target.Selector} application: {s.Target.ApplicationId} component: {s.Target.ComponentRef}");
            _output.WriteLine($"{s.Reference.Name} revision {s.Reference.Revision} environment: {environment} environmentRecordId: {s.EnvironmentRecordId} providerAlias: {alias}");
            _output.WriteLine($"concurrencyToken: {s.ConcurrencyToken} technicalActorId: {s.TechnicalActorId} changedAtUtc: {s.ChangedAtUtc}");
            if (s.CheckedRevision is { } r) _output.WriteLine($"checkedApplicationRecordId: {r.ApplicationRecordId} checkedRevisionNumber: {r.RevisionNumber}");
        }
        return exit;
    }
}
