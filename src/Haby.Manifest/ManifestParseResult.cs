namespace Haby.Manifest;

/// <summary>A source position in the original UTF-8 payload (one-based).</summary>
public sealed record ManifestSourceLocation(int Line, int Column);

public sealed record ManifestRequestError(
    string Code,
    string Path,
    string Message,
    IReadOnlyDictionary<string, string> Parameters,
    ManifestSourceLocation? Location = null);

public enum ManifestDiagnosticSeverity { Error, Warning }

public sealed record ManifestDiagnostic(
    string Code,
    ManifestDiagnosticSeverity Severity,
    string Path,
    string Message,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> RelatedPaths,
    ManifestSourceLocation? Location = null);

public sealed record ManifestValidationReport(IReadOnlyList<ManifestDiagnostic> Diagnostics, bool Truncated)
{
    public bool IsValid => Diagnostics.All(diagnostic => diagnostic.Severity != ManifestDiagnosticSeverity.Error);
}

/// <summary>The parser's transport-neutral result. No import or persistence occurs here.</summary>
public abstract record ManifestParseResult
{
    private ManifestParseResult() { }

    public sealed record Valid(string ApplicationVersion, ApplicationManifest Manifest) : ManifestParseResult;
    public sealed record InvalidRequest(IReadOnlyList<ManifestRequestError> Errors) : ManifestParseResult;
    public sealed record InvalidManifest(ManifestValidationReport Report) : ManifestParseResult;
}
