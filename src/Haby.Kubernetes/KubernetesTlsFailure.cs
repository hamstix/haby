using System.Security.Authentication;
using Haby.Application;

namespace Haby.Kubernetes;

/// <summary>Value-free failure retained across HttpClient wrapping; no certificate or endpoint data.</summary>
internal sealed class KubernetesTlsFailure(ProviderConnectionDiagnostic diagnostic) : AuthenticationException("Kubernetes TLS validation failed.")
{
    internal ProviderConnectionDiagnostic Diagnostic { get; } = diagnostic;

    internal static ProviderConnectionDiagnostic? Find(Exception error)
    {
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 12; depth++, current = current.InnerException)
            if (current is KubernetesTlsFailure failure) return failure.Diagnostic;
        return error is HttpRequestException request ? request.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError => ProviderConnectionDiagnostic.TlsValidationFailed,
            HttpRequestError.NameResolutionError => ProviderConnectionDiagnostic.DnsUnavailable,
            HttpRequestError.ConnectionError => ProviderConnectionDiagnostic.ConnectionUnavailable,
            _ => null,
        } : null;
    }
}
