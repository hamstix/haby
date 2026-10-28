using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService(ManifestContractCatalog catalog, ImportApplicationManifest importer,
    IManifestRevisionQuery revisions, PreviewImportedDatabase preview,
    IDurableDeploymentPlanner planner, IEnvironmentQuery environments, IDurableDesiredStateCommitter committer) : ManifestOperator.ManifestOperatorBase
{
    public override Task<ValidateReply> Validate(EnvelopeRequest request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var reply = ValidateApplicationManifest.Execute(request.OriginalEnvelope.Memory, catalog) switch
        {
            ValidateApplicationManifestResult.Valid valid => new ValidateReply
            {
                Valid = new ValidManifest
                {
                    ApplicationId = valid.ApplicationId.Value,
                    ApplicationVersion = valid.ApplicationVersion.Value,
                    Warnings = Report(valid.Warnings)
                }
            },
            ValidateApplicationManifestResult.InvalidRequest invalid => new ValidateReply
            { InvalidRequest = new InvalidRequest { Report = RequestErrors(invalid.RequestErrors) } },
            ValidateApplicationManifestResult.InvalidManifest invalid => new ValidateReply
            { InvalidManifest = new InvalidManifest { Report = Report(invalid.ValidationReport) } },
            _ => throw new InvalidOperationException("Unknown validation outcome.")
        };
        return Task.FromResult(reply);
    }

    public override async Task<ImportReply> Import(EnvelopeRequest request, ServerCallContext context)
    {
        var result = await importer.ExecuteAsync(request.OriginalEnvelope.Memory, OperatorHostApplication.TechnicalActorId,
            context.CancellationToken);
        return result switch
        {
            ImportApplicationManifestResult.Succeeded accepted when accepted.Disposition ==
                ImportApplicationManifestResult.ImportDisposition.Created => new ImportReply
                { Created = new ImportAccepted { Revision = await ReadAccepted(accepted) } },
            ImportApplicationManifestResult.Succeeded accepted => new ImportReply
            { ExistingEquivalent = new ImportAccepted { Revision = await ReadAccepted(accepted) } },
            ImportApplicationManifestResult.InvalidRequest invalid => new ImportReply
            { InvalidRequest = new InvalidRequest { Report = RequestErrors(invalid.RequestErrors) } },
            ImportApplicationManifestResult.InvalidManifest invalid => new ImportReply
            { InvalidManifest = new InvalidManifest { Report = Report(invalid.ValidationReport) } },
            ImportApplicationManifestResult.ApplicationVersionContentConflict conflict => Conflict(conflict),
            _ => throw new InvalidOperationException("Unknown import outcome.")
        };
    }

    public override async Task<GetRevisionReply> GetRevision(GetRevisionRequest request, ServerCallContext context)
    {
        if (request.Reference is null || !Guid.TryParse(request.Reference.ApplicationRecordId, out _) ||
            request.Reference.RevisionNumber <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid revision reference."));
        ManifestRevisionRef reference;
        try
        {
            reference = ManifestRevisionRef.Create(ApplicationRecordId.Parse(request.Reference.ApplicationRecordId),
                ManifestRevisionNumber.Create(request.Reference.RevisionNumber));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid revision reference."));
        }
        var revision = await revisions.FindAsync(reference, context.CancellationToken);
        return revision is null
            ? new GetRevisionReply { NotFound = Reference(reference) }
            : new GetRevisionReply { Found = Summary(revision) };
    }

    public override async Task<PreviewDatabaseReply> PreviewDatabase(PreviewDatabaseRequest request,
        ServerCallContext context)
    {
        if (request.Reference is null || request.Reference.RevisionNumber <= 0)
            return new PreviewDatabaseReply { InvalidContext = true };
        ManifestRevisionRef reference;
        try
        {
            reference = ManifestRevisionRef.Create(ApplicationRecordId.Parse(request.Reference.ApplicationRecordId),
                ManifestRevisionNumber.Create(request.Reference.RevisionNumber));
        }
        catch (ArgumentException) { return new PreviewDatabaseReply { InvalidContext = true }; }
        var selections = request.Providers.Select(item => new Haby.Application.PreviewProviderSelection(
            item.Alias, item.InstanceId, item.ConfigurationRevision, item.ImplementationId)).ToArray();
        var result = await preview.ExecuteAsync(reference, request.EnvironmentId, selections, context.CancellationToken);
        return result switch
        {
            PreviewDatabaseResult.WouldCreate ready => new PreviewDatabaseReply
            {
                WouldCreate = new PreviewWouldCreate
                {
                    EnvironmentId = ready.EnvironmentId,
                    ResourceId = ready.ResourceId,
                    DatabaseName = ready.DatabaseName,
                    ProviderAlias = ready.ProviderAlias,
                    InstanceId = ready.ProviderInstanceId,
                    ConfigurationRevision = ready.ConfigurationRevision,
                    ImplementationId = ready.ImplementationId
                }
            },
            PreviewDatabaseResult.RevisionNotFound => new PreviewDatabaseReply { RevisionNotFound = true },
            PreviewDatabaseResult.InvalidContext => new PreviewDatabaseReply { InvalidContext = true },
            PreviewDatabaseResult.AliasNotResolved missing => new PreviewDatabaseReply
            { AliasNotResolved = new PreviewAliasFailure { Alias = missing.Alias } },
            PreviewDatabaseResult.CapabilityMismatch mismatch => new PreviewDatabaseReply
            { CapabilityMismatch = new PreviewAliasFailure { Alias = mismatch.Alias } },
            _ => new PreviewDatabaseReply { UnsupportedShape = true }
        };
    }

    private async Task<RevisionSummary> ReadAccepted(ImportApplicationManifestResult.Succeeded accepted)
    {
        var revision = await revisions.FindAsync(accepted.ManifestRevisionRef, CancellationToken.None)
            ?? throw new InvalidOperationException("Committed revision was not found.");
        return Summary(revision);
    }

    private static ImportReply Conflict(ImportApplicationManifestResult.ApplicationVersionContentConflict conflict)
    {
        var response = new VersionContentConflict
        {
            ApplicationId = conflict.ApplicationId.Value,
            ApplicationVersion = conflict.ApplicationVersion.Value,
            ExistingRevision = Reference(conflict.ExistingManifestRevisionRef),
            ChangedPathsTruncated = conflict.ChangedPathsTruncated
        };
        response.ChangedPaths.AddRange(conflict.ChangedPaths);
        return new ImportReply { VersionContentConflict = response };
    }

    private static RevisionSummary Summary(ManifestRevision revision) => new()
    {
        Reference = Reference(revision.Reference),
        RevisionId = revision.Id.ToString(),
        ApplicationId = revision.ApplicationId.Value,
        ApplicationVersion = revision.ApplicationVersion.Value,
        InputApiVersion = revision.InputApiVersion,
        NormalizedModelVersion = revision.NormalizedModelVersion,
        HistoricalWarnings = Report(revision.HistoricalWarnings),
        TechnicalActorId = revision.AuditMetadata.ActorId,
        ImportedAtUtc = revision.AuditMetadata.ImportedAt.ToUniversalTime().ToString("O")
    };

    private static RevisionReference Reference(ManifestRevisionRef reference) => new()
    {
        ApplicationRecordId = reference.ApplicationRecordId.ToString(),
        RevisionNumber = reference.RevisionNumber.Value
    };

    private static DiagnosticReport RequestErrors(IReadOnlyList<ManifestRequestError> errors)
    {
        var report = new DiagnosticReport();
        foreach (var error in errors)
        {
            var diagnostic = new Diagnostic
            {
                Code = error.Code,
                Path = error.Path,
                Message = error.Message,
                Severity = "Error",
                Location = Location(error.Location)
            };
            foreach (var parameter in error.Parameters)
                diagnostic.Parameters.Add(parameter.Key, parameter.Value);
            report.Diagnostics.Add(diagnostic);
        }
        return report;
    }

    private static DiagnosticReport Report(ManifestValidationReport validation)
    {
        var report = new DiagnosticReport { Truncated = validation.Truncated };
        foreach (var item in validation.Diagnostics)
        {
            var diagnostic = new Diagnostic
            {
                Code = item.Code,
                Path = item.Path,
                Message = item.Message,
                Severity = item.Severity.ToString(),
                Location = Location(item.Location)
            };
            foreach (var parameter in item.Parameters)
                diagnostic.Parameters.Add(parameter.Key, parameter.Value);
            diagnostic.RelatedPaths.AddRange(item.RelatedPaths);
            report.Diagnostics.Add(diagnostic);
        }
        return report;
    }

    private static SourceLocation? Location(ManifestSourceLocation? value) => value is null
        ? null : new SourceLocation { Line = value.Line, Column = value.Column };
}
