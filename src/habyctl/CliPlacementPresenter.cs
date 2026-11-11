using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliPlacementReference(string Environment, string ProviderAlias, string Name, int Revision);
internal sealed record CliPlacementData(CliPlacementReference? Reference, string? RecordId, string? EnvironmentRecordId,
    string? TechnicalActorId, string? ImportedAtUtc,
    [property: JsonConverter(typeof(ProfileDeclarationElementConverter))] JsonElement? Declaration);

internal sealed partial class CliCommandContext
{
    internal int PrintPlacement(string command, PlacementReply reply, string environment, string alias) => jsonOutput()
        ? jsonPresenter.PrintPlacement(command, reply, environment, alias) : presenter.PrintPlacement(command, reply, environment, alias);
}

internal static class PlacementPresentation
{
    internal static (string Outcome, int Exit, CliPlacementData Data) Check(string command, PlacementReply reply, string environment, string alias)
    {
        var audited = reply.Status is PlacementStatus.Created or PlacementStatus.ExistingEquivalent or PlacementStatus.Found;
        var referenced = audited || reply.Status is PlacementStatus.Valid or PlacementStatus.RevisionContentConflict;
        var allowed = command switch
        {
            "placement validate" => reply.Status is PlacementStatus.Valid or PlacementStatus.InvalidDeclaration,
            "placement import" => reply.Status is PlacementStatus.Created or PlacementStatus.ExistingEquivalent or PlacementStatus.RevisionContentConflict or
                PlacementStatus.InvalidContext or PlacementStatus.EnvironmentNotFound or PlacementStatus.InvalidDeclaration,
            "placement revision get" => reply.Status is PlacementStatus.Found or PlacementStatus.NotFound or PlacementStatus.InvalidContext or PlacementStatus.EnvironmentNotFound,
            _ => false
        };
        if (!allowed || referenced != (reply.Reference is not null) || audited != (reply.Metadata is not null)) throw Invalid();
        if (reply.Reference is { } r && (r.Environment != environment || r.ProviderAlias != alias || r.Revision <= 0 ||
            !ProfilePresentation.ValidProfileId(r.Name) || r.Name.Length > 63 || r.Name.Contains('.'))) throw Invalid();
        if (reply.Metadata is { } m && (!Guid.TryParse(m.RecordId, out var id) || id == Guid.Empty ||
            !Guid.TryParse(m.EnvironmentRecordId, out var env) || env == Guid.Empty || m.TechnicalActorId.Length is 0 or > 256 ||
            m.TechnicalActorId.Any(char.IsControl) || !DateTimeOffset.TryParseExact(m.ImportedAtUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero)) throw Invalid();
        if (reply.Status == PlacementStatus.InvalidDeclaration ? reply.DiagnosticCode.Length is 0 or > 128 ||
            reply.DiagnosticCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')) : reply.DiagnosticCode != "") throw Invalid();
        JsonElement? declaration = null;
        if (reply.Status == PlacementStatus.Found)
        {
            if (reply.DeclarationJson.IsEmpty || reply.DeclarationJson.Length > 1048576) throw Invalid();
            try
            {
                using var json = JsonDocument.Parse(reply.DeclarationJson.Memory);
                var root = json.RootElement;
                Unique(root);
                if (root.GetProperty("apiVersion").GetString() != "haby.dev/v1alpha1" || root.GetProperty("kind").GetString() != "KubernetesPlacement" ||
                    root.GetProperty("metadata").GetProperty("name").GetString() != reply.Reference!.Name ||
                    root.GetProperty("metadata").GetProperty("revision").GetInt32() != reply.Reference.Revision ||
                    root.GetProperty("spec").ValueKind != JsonValueKind.Object) throw Invalid();
                declaration = root.Clone();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { throw Invalid(); }
        }
        else if (!reply.DeclarationJson.IsEmpty) throw Invalid();
        var status = reply.Status.ToString();
        var data = new CliPlacementData(reply.Reference is { } reference ? new(reference.Environment, reference.ProviderAlias, reference.Name, reference.Revision) : null,
            reply.Metadata?.RecordId, reply.Metadata?.EnvironmentRecordId, reply.Metadata?.TechnicalActorId, reply.Metadata?.ImportedAtUtc, declaration);
        return (char.ToLowerInvariant(status[0]) + status[1..], audited || reply.Status == PlacementStatus.Valid ? 0 : 1, data);
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw Invalid(); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
        else if (value.ValueKind == JsonValueKind.Null) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Invalid Placement response.");
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintPlacement(string command, PlacementReply reply, string environment, string alias)
    {
        var (outcome, exit, data) = PlacementPresentation.Check(command, reply, environment, alias);
        Write(command, outcome, data, reply.DiagnosticCode == "" ? [] : [new(reply.DiagnosticCode, "", "", "Error", [], null)],
            CliOutputJsonContext.Default.CliEnvelopeCliPlacementData);
        return exit;
    }
}
internal sealed partial class CliTextPresenter
{
    internal int PrintPlacement(string command, PlacementReply reply, string environment, string alias)
    {
        var (outcome, exit, _) = PlacementPresentation.Check(command, reply, environment, alias);
        _output.WriteLine(outcome);
        if (reply.Reference is { } r) _output.WriteLine($"{r.Name} revision {r.Revision} environment: {r.Environment} providerAlias: {r.ProviderAlias}");
        if (reply.Metadata is { } m) _output.WriteLine($"recordId: {m.RecordId} technicalActorId: {m.TechnicalActorId} importedAtUtc: {m.ImportedAtUtc}");
        if (reply.DiagnosticCode != "") _output.WriteLine(reply.DiagnosticCode);
        return exit;
    }
}
