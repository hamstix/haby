using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliProfileReference(string ProfileId, int Revision);
internal sealed record CliProfileData(CliProfileReference? Reference, string? TechnicalActorId, string? ImportedAtUtc,
    int ErrorCount, bool DiagnosticsTruncated,
    [property: JsonConverter(typeof(ProfileDeclarationElementConverter))] JsonElement? Declaration);

internal static class ProfilePresentation
{
    internal static JsonElement? Declaration(ProfileReply reply)
    {
        if (reply.Status == ProfileStatus.Found)
        {
            if (reply.DeclarationJson.IsEmpty) throw new InvalidDataException("Missing Profile declaration.");
            return ProfileDeclarationReply.Read(reply);
        }
        if (!reply.DeclarationJson.IsEmpty) throw new InvalidDataException("Unexpected Profile declaration.");
        return null;
    }

    internal static bool ValidProfileId(string value) => value.Length is > 0 and <= 128 &&
        value[0] is >= 'a' and <= 'z' && value[^1] is not ('.' or '-') &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') &&
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains(".-", StringComparison.Ordinal) &&
        !value.Contains("-.", StringComparison.Ordinal) && !value.Contains("--", StringComparison.Ordinal);

    internal static (string Outcome, int Exit, JsonElement? Declaration) Check(string command, ProfileReply reply)
    {
        var allowed = command switch
        {
            "profile validate" => reply.Status is ProfileStatus.Valid or ProfileStatus.InvalidDeclaration,
            "profile import" => reply.Status is ProfileStatus.Created or ProfileStatus.ExistingEquivalent or
                ProfileStatus.InvalidDeclaration or ProfileStatus.RevisionContentConflict,
            "profile revision get" => reply.Status is ProfileStatus.Found or ProfileStatus.NotFound,
            _ => false
        };
        if (!allowed) throw new InvalidDataException("Unknown Profile outcome.");
        var invalid = reply.Status == ProfileStatus.InvalidDeclaration;
        var audited = reply.Status is ProfileStatus.Created or ProfileStatus.ExistingEquivalent or ProfileStatus.Found;
        if (invalid ? reply.Reference is not null || reply.Metadata is not null || reply.ErrorCount <= 0 ||
                reply.Diagnostics.Count is < 1 or > 100 || reply.ErrorCount < reply.Diagnostics.Count ||
                reply.DiagnosticsTruncated != (reply.ErrorCount > reply.Diagnostics.Count) ||
                reply.Diagnostics.Any(x => x.Code.Length is 0 or > 128 || x.Code.Any(char.IsControl) ||
                    x.Path.Length > 1024 || x.Path.Any(char.IsControl) || x.Path.Length > 0 && !x.Path.StartsWith('/')) :
                reply.Reference is null || reply.Reference.Revision <= 0 || !ValidProfileId(reply.Reference.ProfileId) ||
                reply.ErrorCount != 0 || reply.Diagnostics.Count != 0 || reply.DiagnosticsTruncated)
            throw new InvalidDataException("Incomplete Profile result.");
        if (audited ? reply.Metadata is null || !Equals(reply.Metadata.Reference, reply.Reference) ||
                string.IsNullOrWhiteSpace(reply.Metadata.TechnicalActorId) || reply.Metadata.TechnicalActorId.Length > 256 ||
                reply.Metadata.TechnicalActorId.Any(char.IsControl) ||
                !DateTimeOffset.TryParseExact(reply.Metadata.ImportedAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero : reply.Metadata is not null)
            throw new InvalidDataException("Incomplete Profile audit.");
        var declaration = Declaration(reply);
        var (outcome, exit) = reply.Status switch
        {
            ProfileStatus.Valid => ("valid", 0),
            ProfileStatus.Created => ("created", 0),
            ProfileStatus.ExistingEquivalent => ("existingEquivalent", 0),
            ProfileStatus.Found => ("found", 0),
            ProfileStatus.InvalidDeclaration => ("invalidDeclaration", 1),
            ProfileStatus.RevisionContentConflict => ("revisionContentConflict", 1),
            ProfileStatus.NotFound => ("notFound", 1),
            _ => throw new InvalidDataException("Unknown Profile outcome.")
        };
        return (outcome, exit, declaration);
    }
}

internal sealed partial class CliCommandContext
{
    internal int PrintProfile(string command, ProfileReply reply) => jsonOutput()
        ? jsonPresenter.PrintProfile(command, reply) : presenter.PrintProfile(command, reply);
}

internal sealed partial class CliJsonPresenter
{
    private static readonly CliOutputJsonContext ProfileJsonContext = new(new JsonSerializerOptions(CliOutputJsonContext.Default.Options)
    { MaxDepth = 66 });
    internal int PrintProfile(string command, ProfileReply reply)
    {
        var (outcome, exit, declaration) = ProfilePresentation.Check(command, reply);
        Write(command, outcome, new CliProfileData(reply.Reference is { } r ? new(r.ProfileId, r.Revision) : null,
            reply.Metadata?.TechnicalActorId, reply.Metadata?.ImportedAtUtc, reply.ErrorCount, reply.DiagnosticsTruncated, declaration),
            [.. reply.Diagnostics.Select(x => new CliDiagnostic(x.Code, x.Path, "", "Error", [], null))],
            ProfileJsonContext.CliEnvelopeCliProfileData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal int PrintProfile(string command, ProfileReply reply)
    {
        var (outcome, exit, _) = ProfilePresentation.Check(command, reply);
        _output.WriteLine(outcome);
        if (reply.Reference is { } r) _output.WriteLine($"{r.ProfileId} revision {r.Revision}");
        if (reply.Metadata is { } m) _output.WriteLine($"technicalActorId: {m.TechnicalActorId} importedAtUtc: {m.ImportedAtUtc}");
        foreach (var diagnostic in reply.Diagnostics) _output.WriteLine($"{diagnostic.Code} {diagnostic.Path}");
        if (reply.DiagnosticsTruncated) _output.WriteLine($"Diagnostics truncated; {reply.ErrorCount} errors.");
        return exit;
    }
}
