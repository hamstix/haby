using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Haby.Operator.Contracts;

namespace Haby.Cli;

// This is an alpha presentation contract. Only fields explicitly mapped here can reach stdout.
internal sealed record CliEnvelope<T>(int FormatVersion, string Command, string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] T? Data,
    CliDiagnostic[] Diagnostics) where T : class;
internal sealed record CliDiagnostic(string Code, string Path, string Message, string Severity,
    string[] RelatedPaths, CliLocation? Location);
internal sealed record CliLocation(int Line, int Column);
internal sealed record CliReference(string ApplicationRecordId, long RevisionNumber);
internal sealed record CliRevision(CliReference Reference, string RevisionId, string ApplicationId,
    string ApplicationVersion, string InputApiVersion, int NormalizedModelVersion,
    string TechnicalActorId, string ImportedAtUtc, CliDiagnostic[] HistoricalWarnings,
    bool HistoricalWarningsTruncated);
internal sealed record CliValidationData(string ApplicationId, string ApplicationVersion, bool DiagnosticsTruncated);
internal sealed record CliImportData(CliRevision? Revision, string? ApplicationId, string? ApplicationVersion,
    CliReference? ExistingRevision, string[]? ChangedPaths, bool? ChangedPathsTruncated,
    bool DiagnosticsTruncated);
internal sealed record CliGetRevisionData(CliRevision? Revision, CliReference? RequestedReference);
internal sealed record CliPreviewData(bool Hypothetical, bool CanApply, string? EnvironmentId,
    string? ResourceId, string? DatabaseName, string? ProviderAlias, string? InstanceId,
    long? ConfigurationRevision, string? ImplementationId, string? Alias);
internal sealed record CliConfigInstance(string Name, string Endpoint);
internal sealed record CliConfigContext(string Name, string Instance, string EnvironmentId);
internal sealed record CliConfigData(int FormatVersion, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CurrentContext,
    CliConfigInstance[] Instances, CliConfigContext[] Contexts);
internal sealed record CliCurrentContextData([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CurrentContext);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false)]
[JsonSerializable(typeof(CliEnvelope<CliValidationData>))]
[JsonSerializable(typeof(CliEnvelope<CliImportData>))]
[JsonSerializable(typeof(CliEnvelope<CliGetRevisionData>))]
[JsonSerializable(typeof(CliEnvelope<CliPreviewData>))]
[JsonSerializable(typeof(CliEnvelope<CliPlanningData>))]
[JsonSerializable(typeof(CliEnvelope<CliConfigData>))]
[JsonSerializable(typeof(CliEnvelope<CliCurrentContextData>))]
internal partial class CliOutputJsonContext : JsonSerializerContext;

internal sealed partial class CliJsonPresenter(TextWriter output)
{
    private static readonly CliDiagnostic[] NoDiagnostics = [];

    private void Write<T>(string command, string outcome, T? data, CliDiagnostic[] diagnostics,
        JsonTypeInfo<CliEnvelope<T>> metadata) where T : class =>
        output.WriteLine(JsonSerializer.Serialize(new CliEnvelope<T>(1, command, outcome, data, diagnostics), metadata));

    internal int Print(ValidateReply reply)
    {
        const string command = "manifest validate";
        switch (reply.OutcomeCase)
        {
            case ValidateReply.OutcomeOneofCase.Valid:
                Write(command, "valid", new CliValidationData(reply.Valid.ApplicationId,
                    reply.Valid.ApplicationVersion, reply.Valid.Warnings?.Truncated ?? false),
                    Diagnostics(reply.Valid.Warnings), CliOutputJsonContext.Default.CliEnvelopeCliValidationData);
                return CliApplication.Success;
            case ValidateReply.OutcomeOneofCase.InvalidRequest:
                Write(command, "invalidRequest", new CliValidationData("", "",
                    reply.InvalidRequest.Report?.Truncated ?? false), Diagnostics(reply.InvalidRequest.Report),
                    CliOutputJsonContext.Default.CliEnvelopeCliValidationData);
                return CliApplication.Rejected;
            case ValidateReply.OutcomeOneofCase.InvalidManifest:
                Write(command, "invalidManifest", new CliValidationData("", "",
                    reply.InvalidManifest.Report?.Truncated ?? false), Diagnostics(reply.InvalidManifest.Report),
                    CliOutputJsonContext.Default.CliEnvelopeCliValidationData);
                return CliApplication.Rejected;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal int Print(ImportReply reply)
    {
        const string command = "manifest import";
        var metadata = CliOutputJsonContext.Default.CliEnvelopeCliImportData;
        switch (reply.OutcomeCase)
        {
            case ImportReply.OutcomeOneofCase.Created:
            case ImportReply.OutcomeOneofCase.ExistingEquivalent:
                var revision = reply.OutcomeCase == ImportReply.OutcomeOneofCase.Created
                    ? reply.Created.Revision : reply.ExistingEquivalent.Revision;
                Write(command, reply.OutcomeCase == ImportReply.OutcomeOneofCase.Created ? "created" : "existingEquivalent",
                    new CliImportData(Revision(revision), null, null, null, null, null, false),
                    NoDiagnostics, metadata);
                return CliApplication.Success;
            case ImportReply.OutcomeOneofCase.InvalidRequest:
            case ImportReply.OutcomeOneofCase.InvalidManifest:
                var report = reply.OutcomeCase == ImportReply.OutcomeOneofCase.InvalidRequest
                    ? reply.InvalidRequest.Report : reply.InvalidManifest.Report;
                Write(command, reply.OutcomeCase == ImportReply.OutcomeOneofCase.InvalidRequest
                        ? "invalidRequest" : "invalidManifest",
                    new CliImportData(null, null, null, null, null, null, report?.Truncated ?? false),
                    Diagnostics(report), metadata);
                return CliApplication.Rejected;
            case ImportReply.OutcomeOneofCase.VersionContentConflict:
                var conflict = reply.VersionContentConflict;
                Write(command, "versionContentConflict", new CliImportData(null, conflict.ApplicationId,
                    conflict.ApplicationVersion, Reference(conflict.ExistingRevision),
                    [.. conflict.ChangedPaths], conflict.ChangedPathsTruncated, false), NoDiagnostics, metadata);
                return CliApplication.Rejected;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal int Print(GetRevisionReply reply)
    {
        const string command = "manifest revision get";
        var metadata = CliOutputJsonContext.Default.CliEnvelopeCliGetRevisionData;
        switch (reply.OutcomeCase)
        {
            case GetRevisionReply.OutcomeOneofCase.Found:
                Write(command, "found", new CliGetRevisionData(Revision(reply.Found), null), NoDiagnostics, metadata);
                return CliApplication.Success;
            case GetRevisionReply.OutcomeOneofCase.NotFound:
                Write(command, "notFound", new CliGetRevisionData(null, Reference(reply.NotFound)), NoDiagnostics, metadata);
                return CliApplication.Rejected;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal int Print(PreviewDatabaseReply reply)
    {
        const string command = "manifest revision preview";
        var metadata = CliOutputJsonContext.Default.CliEnvelopeCliPreviewData;
        var empty = new CliPreviewData(true, false, null, null, null, null, null, null, null, null);
        switch (reply.OutcomeCase)
        {
            case PreviewDatabaseReply.OutcomeOneofCase.WouldCreate:
                var value = reply.WouldCreate;
                Write(command, "wouldCreate", new CliPreviewData(true, false, value.EnvironmentId,
                    value.ResourceId, value.DatabaseName, value.ProviderAlias, value.InstanceId,
                    value.ConfigurationRevision, value.ImplementationId, null), NoDiagnostics, metadata);
                return CliApplication.Success;
            case PreviewDatabaseReply.OutcomeOneofCase.RevisionNotFound:
                Write(command, "revisionNotFound", empty, NoDiagnostics, metadata); return CliApplication.Rejected;
            case PreviewDatabaseReply.OutcomeOneofCase.InvalidContext:
                Write(command, "invalidContext", empty, NoDiagnostics, metadata); return CliApplication.Rejected;
            case PreviewDatabaseReply.OutcomeOneofCase.AliasNotResolved:
                Write(command, "aliasNotResolved", empty with { Alias = reply.AliasNotResolved.Alias }, NoDiagnostics, metadata);
                return CliApplication.Rejected;
            case PreviewDatabaseReply.OutcomeOneofCase.CapabilityMismatch:
                Write(command, "capabilityMismatch", empty with { Alias = reply.CapabilityMismatch.Alias }, NoDiagnostics, metadata);
                return CliApplication.Rejected;
            case PreviewDatabaseReply.OutcomeOneofCase.UnsupportedShape:
                Write(command, "unsupportedShape", empty, NoDiagnostics, metadata); return CliApplication.Rejected;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal void PrintConfiguration(CliConfiguration config, string? current) =>
        Write("config view", "ok", new CliConfigData(1, current,
            [.. config.Instances.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new CliConfigInstance(item.Key, item.Value.Endpoint))],
            [.. config.Contexts.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new CliConfigContext(item.Key, item.Value.Instance, item.Value.EnvironmentId))]),
            NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliConfigData);

    internal void PrintCurrentContext(string? current) =>
        Write("config current-context", "ok", new CliCurrentContextData(current), NoDiagnostics,
            CliOutputJsonContext.Default.CliEnvelopeCliCurrentContextData);

    private static CliReference Reference(RevisionReference? value) =>
        new(value?.ApplicationRecordId ?? "", value?.RevisionNumber ?? 0);

    private static CliRevision Revision(RevisionSummary? value)
    {
        if (value is null) throw new InvalidDataException("Unknown server outcome.");
        return new CliRevision(Reference(value.Reference), value.RevisionId, value.ApplicationId,
            value.ApplicationVersion, value.InputApiVersion, value.NormalizedModelVersion,
            value.TechnicalActorId, value.ImportedAtUtc, Diagnostics(value.HistoricalWarnings),
            value.HistoricalWarnings?.Truncated ?? false);
    }

    private static CliDiagnostic[] Diagnostics(DiagnosticReport? report) => report is null
        ? [] : [.. report.Diagnostics.Select(value => new CliDiagnostic(value.Code, value.Path,
            value.Message, value.Severity, [.. value.RelatedPaths],
            value.Location is null ? null : new CliLocation(value.Location.Line, value.Location.Column)))];
}
