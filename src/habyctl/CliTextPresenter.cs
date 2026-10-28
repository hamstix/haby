using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed partial class CliTextPresenter(TextWriter output)
{
    private readonly TextWriter _output = output;

    internal int Print(ValidateReply reply)
    {
        switch (reply.OutcomeCase)
        {
            case ValidateReply.OutcomeOneofCase.Valid:
                _output.WriteLine($"Valid {reply.Valid.ApplicationId} {reply.Valid.ApplicationVersion}");
                Print(reply.Valid.Warnings);
                return 0;
            case ValidateReply.OutcomeOneofCase.InvalidRequest:
                _output.WriteLine("InvalidRequest"); Print(reply.InvalidRequest.Report); return 1;
            case ValidateReply.OutcomeOneofCase.InvalidManifest:
                _output.WriteLine("InvalidManifest"); Print(reply.InvalidManifest.Report); return 1;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal int Print(ImportReply reply)
    {
        switch (reply.OutcomeCase)
        {
            case ImportReply.OutcomeOneofCase.Created:
                _output.WriteLine("Created"); Print(reply.Created.Revision); return 0;
            case ImportReply.OutcomeOneofCase.ExistingEquivalent:
                _output.WriteLine("ExistingEquivalent"); Print(reply.ExistingEquivalent.Revision); return 0;
            case ImportReply.OutcomeOneofCase.InvalidRequest:
                _output.WriteLine("InvalidRequest"); Print(reply.InvalidRequest.Report); return 1;
            case ImportReply.OutcomeOneofCase.InvalidManifest:
                _output.WriteLine("InvalidManifest"); Print(reply.InvalidManifest.Report); return 1;
            case ImportReply.OutcomeOneofCase.VersionContentConflict:
                var conflict = reply.VersionContentConflict;
                _output.WriteLine($"VersionContentConflict {conflict.ApplicationId} {conflict.ApplicationVersion}");
                _output.WriteLine($"Existing revision: {conflict.ExistingRevision.ApplicationRecordId} {conflict.ExistingRevision.RevisionNumber}");
                foreach (var path in conflict.ChangedPaths) _output.WriteLine($"Changed: {path}");
                if (conflict.ChangedPathsTruncated) _output.WriteLine("Changed paths truncated");
                return 1;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal int Print(GetRevisionReply reply)
    {
        switch (reply.OutcomeCase)
        {
            case GetRevisionReply.OutcomeOneofCase.Found:
                _output.WriteLine("Found"); Print(reply.Found); return 0;
            case GetRevisionReply.OutcomeOneofCase.NotFound:
                _output.WriteLine("NotFound"); return 1;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal int Print(PreviewDatabaseReply reply)
    {
        switch (reply.OutcomeCase)
        {
            case PreviewDatabaseReply.OutcomeOneofCase.WouldCreate:
                var value = reply.WouldCreate;
                _output.WriteLine("Hypothetical preview only; cannot be applied.");
                _output.WriteLine($"Would create one database: {value.DatabaseName}");
                _output.WriteLine($"Environment: {value.EnvironmentId}; resource: {value.ResourceId}");
                _output.WriteLine($"Provider: {value.ProviderAlias} -> {value.InstanceId} revision {value.ConfigurationRevision}; implementation: {value.ImplementationId}");
                return 0;
            case PreviewDatabaseReply.OutcomeOneofCase.RevisionNotFound: _output.WriteLine("RevisionNotFound"); return 1;
            case PreviewDatabaseReply.OutcomeOneofCase.InvalidContext: _output.WriteLine("InvalidPreviewContext"); return 1;
            case PreviewDatabaseReply.OutcomeOneofCase.AliasNotResolved:
                _output.WriteLine($"ProviderAliasNotResolved: {reply.AliasNotResolved.Alias}"); return 1;
            case PreviewDatabaseReply.OutcomeOneofCase.CapabilityMismatch:
                _output.WriteLine($"ProviderCapabilityMismatch: {reply.CapabilityMismatch.Alias}"); return 1;
            case PreviewDatabaseReply.OutcomeOneofCase.UnsupportedShape: _output.WriteLine("UnsupportedManifestShape"); return 1;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    private void Print(RevisionSummary revision)
    {
        _output.WriteLine($"Application: {revision.ApplicationId} {revision.ApplicationVersion}");
        _output.WriteLine($"Revision: {revision.Reference.ApplicationRecordId} {revision.Reference.RevisionNumber}");
        _output.WriteLine($"Revision ID: {revision.RevisionId}");
        _output.WriteLine($"Input API: {revision.InputApiVersion}; model: {revision.NormalizedModelVersion}");
        _output.WriteLine($"Technical actor: {revision.TechnicalActorId}; imported UTC: {revision.ImportedAtUtc}");
        Print(revision.HistoricalWarnings);
    }

    private void Print(DiagnosticReport report)
    {
        foreach (var diagnostic in report.Diagnostics)
            _output.WriteLine($"{diagnostic.Severity} {diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}");
        if (report.Truncated) _output.WriteLine("Diagnostics truncated");
    }
}
