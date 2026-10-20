using Haby.Domain;
using Haby.Manifest;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Application;

/// <summary>Closed outcomes of importing an original UTF-8 envelope.</summary>
public abstract record ImportApplicationManifestResult
{
    private ImportApplicationManifestResult() { }

    public enum ImportDisposition { Created, ExistingEquivalent }
    public sealed record Succeeded(ManifestRevisionRef ManifestRevisionRef, ImportDisposition Disposition,
        ManifestValidationReport HistoricalWarnings, ImportAuditMetadata AuditMetadata) : ImportApplicationManifestResult;
    public sealed record InvalidRequest(IReadOnlyList<ManifestRequestError> RequestErrors) : ImportApplicationManifestResult;
    public sealed record InvalidManifest(ManifestValidationReport ValidationReport) : ImportApplicationManifestResult;
    public sealed record ApplicationVersionContentConflict(ApplicationId ApplicationId, ApplicationVersion ApplicationVersion,
        ManifestRevisionRef ExistingManifestRevisionRef, IReadOnlyList<string> ChangedPaths,
        bool ChangedPathsTruncated) : ImportApplicationManifestResult;
}

/// <summary>Validates an envelope and atomically imports its immutable declaration.</summary>
public sealed class ImportApplicationManifest(IManifestImportStore store, ManifestContractCatalog catalog, TimeProvider clock)
{
    public async ValueTask<ImportApplicationManifestResult> ExecuteAsync(
        ReadOnlyMemory<byte> payload, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actorId);
        if (actorId.Length is 0 or > 256 || actorId.Any(char.IsControl))
            throw new ArgumentException("Actor identity must be bounded and nonempty.", nameof(actorId));
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = ManifestCandidatePipeline.Prepare(payload, catalog);
        if (prepared is ManifestCandidateResult.InvalidRequest request)
            return new ImportApplicationManifestResult.InvalidRequest(request.Errors);
        if (prepared is ManifestCandidateResult.InvalidManifest manifest)
            return new ImportApplicationManifestResult.InvalidManifest(manifest.Report);
        var candidate = ((ManifestCandidateResult.Valid)prepared).Candidate;
        cancellationToken.ThrowIfCancellationRequested();
        var audit = new ImportAuditMetadata(actorId, clock.GetUtcNow());
        var outcome = await store.CompareAndCreateAsync(candidate, audit, cancellationToken);
        // The store's return confirms the commit. Never observe cancellation after this point.
        var accepted = outcome.Revision;
        if (outcome.Created || accepted.Declaration.Equals(candidate.Declaration))
            return new ImportApplicationManifestResult.Succeeded(accepted.Reference,
                outcome.Created ? ImportApplicationManifestResult.ImportDisposition.Created :
                    ImportApplicationManifestResult.ImportDisposition.ExistingEquivalent,
                accepted.HistoricalWarnings, accepted.AuditMetadata);
        var paths = NormalizedManifestDiff.Compare(accepted.Declaration, candidate.Declaration);
        return new ImportApplicationManifestResult.ApplicationVersionContentConflict(candidate.ApplicationId,
            candidate.ApplicationVersion, accepted.Reference, paths.Paths, paths.Truncated);
    }
}
