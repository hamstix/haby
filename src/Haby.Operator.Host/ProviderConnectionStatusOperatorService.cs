using System.Globalization;
using Grpc.Core;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<ProviderConnectionStatusReply> GetProviderConnectionStatus(ProviderConnectionStatusRequest request, ServerCallContext context)
    {
        var result = await providerConnections.ObserveAsync(request.Environment, request.HasName ? request.Name : null, context.CancellationToken);
        var reply = new ProviderConnectionStatusReply
        {
            Status = result.Outcome switch
            {
                Application.ProviderConnectionStatusOutcome.Observed => ProviderConnectionStatusCode.Observed,
                Application.ProviderConnectionStatusOutcome.InvalidRequest => ProviderConnectionStatusCode.InvalidRequest,
                Application.ProviderConnectionStatusOutcome.EnvironmentNotFound => ProviderConnectionStatusCode.EnvironmentNotFound,
                Application.ProviderConnectionStatusOutcome.NotFound => ProviderConnectionStatusCode.NotFound,
                Application.ProviderConnectionStatusOutcome.TooManyProviders => ProviderConnectionStatusCode.TooManyProviders,
                Application.ProviderConnectionStatusOutcome.DeadlineExceeded => ProviderConnectionStatusCode.DeadlineExceeded,
                Application.ProviderConnectionStatusOutcome.CapacityUnavailable => ProviderConnectionStatusCode.CapacityUnavailable,
                _ => ProviderConnectionStatusCode.StorageUnavailable
            }
        };
        foreach (var item in result.Items)
        {
            var observation = new ProviderConnectionObservation
            {
                InstanceId = item.Selection.Provider.ToString(),
                ObservedAt = item.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
                Outcome = item.Result.Outcome switch
                {
                    Application.ProviderConnectionOutcome.Connected => ProviderConnectionOutcomeCode.Connected,
                    Application.ProviderConnectionOutcome.NotSelected => ProviderConnectionOutcomeCode.NotSelected,
                    Application.ProviderConnectionOutcome.Unsupported => ProviderConnectionOutcomeCode.Unsupported,
                    Application.ProviderConnectionOutcome.HandlerUnavailable => ProviderConnectionOutcomeCode.HandlerUnavailable,
                    Application.ProviderConnectionOutcome.RevisionUnavailable => ProviderConnectionOutcomeCode.RevisionUnavailable,
                    Application.ProviderConnectionOutcome.SecretUnavailable => ProviderConnectionOutcomeCode.SecretUnavailable,
                    Application.ProviderConnectionOutcome.AuthenticationFailed => ProviderConnectionOutcomeCode.AuthenticationFailed,
                    Application.ProviderConnectionOutcome.AuthenticationUnavailable => ProviderConnectionOutcomeCode.AuthenticationUnavailable,
                    Application.ProviderConnectionOutcome.ConnectionUnavailable => ProviderConnectionOutcomeCode.ConnectionUnavailable,
                    Application.ProviderConnectionOutcome.TlsFailed => ProviderConnectionOutcomeCode.TlsFailed,
                    Application.ProviderConnectionOutcome.DeadlineExceeded => ProviderConnectionOutcomeCode.DeadlineExceeded,
                    _ => ProviderConnectionOutcomeCode.ConfigurationUnavailable
                },
                ServerCertificateValidationDisabled = item.Result.ServerCertificateValidationDisabled
            };
            if (item.Selection.Name is { } name) observation.Name = name;
            if (item.Selection.Alias is { } alias) observation.Alias = alias;
            if (item.Selection.Revision is { } revision) observation.Revision = revision.Value;
            if (item.Result.Diagnostic is { } diagnostic) observation.Diagnostic = diagnostic switch
            {
                Application.ProviderConnectionDiagnostic.TlsTrustFailed => ProviderConnectionDiagnosticCode.TlsTrustFailed,
                Application.ProviderConnectionDiagnostic.TlsNameMismatch => ProviderConnectionDiagnosticCode.TlsNameMismatch,
                Application.ProviderConnectionDiagnostic.TlsCertificateTimeInvalid => ProviderConnectionDiagnosticCode.TlsCertificateTimeInvalid,
                Application.ProviderConnectionDiagnostic.TlsCertificateRevoked => ProviderConnectionDiagnosticCode.TlsCertificateRevoked,
                Application.ProviderConnectionDiagnostic.TlsRevocationUnavailable => ProviderConnectionDiagnosticCode.TlsRevocationUnavailable,
                Application.ProviderConnectionDiagnostic.DnsUnavailable => ProviderConnectionDiagnosticCode.DnsUnavailable,
                Application.ProviderConnectionDiagnostic.ConnectionUnavailable => ProviderConnectionDiagnosticCode.ConnectionUnavailable,
                _ => ProviderConnectionDiagnosticCode.TlsValidationFailed
            };
            reply.Items.Add(observation);
        }
        return reply;
    }
}
