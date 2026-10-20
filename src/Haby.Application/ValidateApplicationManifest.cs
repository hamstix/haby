using Haby.Domain;
using Haby.Manifest;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Application;

/// <summary>The read-only outcome of validating an original import envelope.</summary>
public abstract record ValidateApplicationManifestResult
{
    private ValidateApplicationManifestResult() { }

    public sealed record Valid(ApplicationId ApplicationId, ApplicationVersion ApplicationVersion,
        ManifestValidationReport Warnings) : ValidateApplicationManifestResult;
    public sealed record InvalidRequest(IReadOnlyList<ManifestRequestError> RequestErrors) : ValidateApplicationManifestResult;
    public sealed record InvalidManifest(ManifestValidationReport ValidationReport) : ValidateApplicationManifestResult;
}

/// <summary>Validates and normalizes a manifest without reading or changing catalog state.</summary>
public static class ValidateApplicationManifest
{
    public static ValidateApplicationManifestResult Execute(ReadOnlyMemory<byte> payload, ManifestContractCatalog catalog) =>
        ManifestCandidatePipeline.Prepare(payload, catalog) switch
        {
            ManifestCandidateResult.Valid valid => new ValidateApplicationManifestResult.Valid(
                valid.Candidate.ApplicationId, valid.Candidate.ApplicationVersion, valid.Candidate.Warnings),
            ManifestCandidateResult.InvalidRequest invalid => new ValidateApplicationManifestResult.InvalidRequest(invalid.Errors),
            ManifestCandidateResult.InvalidManifest invalid => new ValidateApplicationManifestResult.InvalidManifest(invalid.Report),
            _ => throw new InvalidOperationException("Unknown manifest candidate result.")
        };
}
