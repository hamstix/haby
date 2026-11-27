using System.Globalization;
using System.Text.Json.Serialization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliProviderConnectionItem(string InstanceId, string? Name, string? Alias, long? Revision,
    string ObservedAt, string Outcome, string? Diagnostic, string? Warning);
internal sealed record CliProviderConnections(IReadOnlyList<CliProviderConnectionItem> Items);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliEnvelope<CliProviderConnections>))]
internal partial class CliProviderConnectionJsonContext : JsonSerializerContext;
internal static class ProviderConnectionPresentation
{
    internal static string Status(ProviderConnectionStatusCode status) => status switch
    {
        ProviderConnectionStatusCode.Observed => "observed",
        ProviderConnectionStatusCode.InvalidRequest => "invalidRequest",
        ProviderConnectionStatusCode.EnvironmentNotFound => "environmentNotFound",
        ProviderConnectionStatusCode.NotFound => "notFound",
        ProviderConnectionStatusCode.TooManyProviders => "tooManyProviders",
        ProviderConnectionStatusCode.StorageUnavailable => "storageUnavailable",
        ProviderConnectionStatusCode.DeadlineExceeded => "deadlineExceeded",
        ProviderConnectionStatusCode.CapacityUnavailable => "capacityUnavailable",
        _ => throw new InvalidDataException("Unknown Provider connection status.")
    };
    internal static int Exit(ProviderConnectionStatusReply reply) => reply.Status == ProviderConnectionStatusCode.Observed &&
        reply.Items.All(x => x.Outcome == ProviderConnectionOutcomeCode.Connected) ? 0 : 1;
    internal static CliProviderConnections Data(ProviderConnectionStatusReply reply)
    {
        _ = Status(reply.Status);
        if (reply.Items.Count > 1024 || reply.Status != ProviderConnectionStatusCode.Observed && reply.Items.Count != 0)
            throw new InvalidDataException("Invalid Provider connection result.");
        return new(reply.Items.Select(x =>
        {
            if (!Guid.TryParseExact(x.InstanceId, "D", out var id) || id.Version != 7 || id.ToString() != x.InstanceId ||
                !DateTimeOffset.TryParseExact(x.ObservedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                x.HasRevision && x.Revision <= 0 || x.HasName && !Safe(x.Name) || x.HasAlias && !Safe(x.Alias) ||
                (x.Outcome == ProviderConnectionOutcomeCode.NotSelected) != !x.HasRevision || x.HasRevision != x.HasAlias)
                throw new InvalidDataException("Invalid Provider connection identity.");
            var outcome = x.Outcome switch
            {
                ProviderConnectionOutcomeCode.Connected => "connected",
                ProviderConnectionOutcomeCode.NotSelected => "notSelected",
                ProviderConnectionOutcomeCode.Unsupported => "unsupported",
                ProviderConnectionOutcomeCode.HandlerUnavailable => "handlerUnavailable",
                ProviderConnectionOutcomeCode.RevisionUnavailable => "revisionUnavailable",
                ProviderConnectionOutcomeCode.ConfigurationUnavailable => "configurationUnavailable",
                ProviderConnectionOutcomeCode.SecretUnavailable => "secretUnavailable",
                ProviderConnectionOutcomeCode.AuthenticationFailed => "authenticationFailed",
                ProviderConnectionOutcomeCode.AuthenticationUnavailable => "authenticationUnavailable",
                ProviderConnectionOutcomeCode.ConnectionUnavailable => "connectionUnavailable",
                ProviderConnectionOutcomeCode.TlsFailed => "tlsFailed",
                ProviderConnectionOutcomeCode.DeadlineExceeded => "deadlineExceeded",
                _ => throw new InvalidDataException("Unknown Provider connection outcome.")
            };
            var diagnostic = x.HasDiagnostic ? x.Diagnostic switch
            {
                ProviderConnectionDiagnosticCode.TlsValidationFailed => "tls-validation-failed",
                ProviderConnectionDiagnosticCode.TlsTrustFailed => "tls-trust-failed",
                ProviderConnectionDiagnosticCode.TlsNameMismatch => "tls-name-mismatch",
                ProviderConnectionDiagnosticCode.TlsCertificateTimeInvalid => "tls-certificate-time-invalid",
                ProviderConnectionDiagnosticCode.TlsCertificateRevoked => "tls-certificate-revoked",
                ProviderConnectionDiagnosticCode.TlsRevocationUnavailable => "tls-revocation-unavailable",
                ProviderConnectionDiagnosticCode.DnsUnavailable => "dns-unavailable",
                ProviderConnectionDiagnosticCode.ConnectionUnavailable => "connection-unavailable",
                _ => throw new InvalidDataException("Unknown Provider connection diagnostic.")
            } : null;
            if (diagnostic is not null && x.Outcome is not (ProviderConnectionOutcomeCode.TlsFailed or ProviderConnectionOutcomeCode.ConnectionUnavailable))
                throw new InvalidDataException("Contradictory Provider connection diagnostic.");
            return new CliProviderConnectionItem(x.InstanceId, x.HasName ? x.Name : null, x.HasAlias ? x.Alias : null,
                x.HasRevision ? x.Revision : null, x.ObservedAt, outcome, diagnostic,
                x.ServerCertificateValidationDisabled ? "Server certificate validation is explicitly disabled." : null);
        }).ToArray());
    }
    private static bool Safe(string text) => text.Length is > 0 and <= 128 && !text.Any(char.IsControl);
}
internal sealed partial class CliCommandContext
{
    internal int PrintProviderConnections(ProviderConnectionStatusReply reply) =>
        jsonOutput() ? jsonPresenter.PrintProviderConnections(reply) : presenter.PrintProviderConnections(reply);
}
internal sealed partial class CliJsonPresenter
{
    internal int PrintProviderConnections(ProviderConnectionStatusReply reply)
    {
        var data = ProviderConnectionPresentation.Data(reply);
        Write("provider status", ProviderConnectionPresentation.Status(reply.Status), data, NoDiagnostics,
            CliProviderConnectionJsonContext.Default.CliEnvelopeCliProviderConnections);
        return ProviderConnectionPresentation.Exit(reply);
    }
}
internal sealed partial class CliTextPresenter
{
    internal int PrintProviderConnections(ProviderConnectionStatusReply reply)
    {
        var data = ProviderConnectionPresentation.Data(reply);
        _output.WriteLine(ProviderConnectionPresentation.Status(reply.Status));
        foreach (var item in data.Items)
        {
            _output.WriteLine($"{item.Name ?? item.InstanceId} {item.InstanceId} alias {item.Alias ?? "-"} revision {item.Revision?.ToString(CultureInfo.InvariantCulture) ?? "-"} {item.ObservedAt}: {item.Outcome}");
            if (item.Diagnostic is not null) _output.WriteLine($"connection: {item.Diagnostic}");
            if (item.Warning is not null) _output.WriteLine($"WARNING: {item.Warning}");
        }
        return ProviderConnectionPresentation.Exit(reply);
    }
}
