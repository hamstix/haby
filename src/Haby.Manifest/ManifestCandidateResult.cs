using System.Collections.ObjectModel;
using Haby.Domain;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Manifest;

/// <summary>A fully validated declaration prepared for a later application operation.</summary>
public sealed class ValidatedManifestCandidate
{
    internal ValidatedManifestCandidate(ApplicationVersion applicationVersion,
        NormalizedApplicationManifest declaration, ManifestValidationReport warnings)
    {
        ApplicationVersion = applicationVersion;
        Declaration = declaration;
        Warnings = new ManifestValidationReport(Array.AsReadOnly(warnings.Diagnostics.Select(diagnostic =>
            diagnostic with
            {
                Parameters = new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(diagnostic.Parameters, StringComparer.Ordinal)),
                RelatedPaths = Array.AsReadOnly(diagnostic.RelatedPaths.ToArray())
            }).ToArray()), warnings.Truncated);
    }

    public ApplicationId ApplicationId => Declaration.ApplicationId;
    public ApplicationVersion ApplicationVersion { get; }
    public NormalizedApplicationManifest Declaration { get; }
    public ManifestValidationReport Warnings { get; }
}

/// <summary>Typed result of validation followed by normalization.</summary>
public abstract record ManifestCandidateResult
{
    private ManifestCandidateResult() { }

    public sealed record Valid(ValidatedManifestCandidate Candidate) : ManifestCandidateResult;
    public sealed record InvalidRequest(IReadOnlyList<ManifestRequestError> Errors) : ManifestCandidateResult;
    public sealed record InvalidManifest(ManifestValidationReport Report) : ManifestCandidateResult;
}

/// <summary>The single validated DTO-to-domain boundary for application operations.</summary>
public static class ManifestCandidatePipeline
{
    public static ManifestCandidateResult Prepare(ReadOnlyMemory<byte> payload, ManifestContractCatalog catalog) =>
        ManifestPayloadParser.Parse(payload, catalog) switch
        {
            ManifestParseResult.Valid valid => new ManifestCandidateResult.Valid(
                new ValidatedManifestCandidate(ApplicationVersion.Create(valid.ApplicationVersion),
                    NormalizedApplicationManifestMapper.Map(valid.Manifest), valid.Report)),
            ManifestParseResult.InvalidRequest invalid => new ManifestCandidateResult.InvalidRequest(invalid.Errors),
            ManifestParseResult.InvalidManifest invalid => new ManifestCandidateResult.InvalidManifest(invalid.Report),
            _ => throw new InvalidOperationException("Unknown manifest parser result.")
        };
}
