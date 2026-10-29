namespace Haby.Operator.Contracts;

/// <summary>Never include unvalidated operator input in generated diagnostic output.</summary>
public sealed partial class ApplyProviderRequest : Google.Protobuf.ICustomDiagnosticMessage
{
    public string ToDiagnosticString() => "{ \"request\": \"ApplyProviderRequest\", \"declaration\": \"[redacted]\" }";
}
