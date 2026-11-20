using System.Text.Json;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class ProviderTlsPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Insecure_warning_is_visible_in_text_and_source_generated_JSON_without_material(bool failed)
    {
        var reply = new ProviderAccessReply
        {
            Status = failed ? ProviderAccessStatus.PreflightFailed : ProviderAccessStatus.Ready,
            Preflight = failed ? ProviderPreflightStatus.AuthenticationFailed : ProviderPreflightStatus.Ready,
            ServerCertificateValidationDisabled = true,
        };
        using var text = new StringWriter(); using var json = new StringWriter();
        Assert.Equal(failed ? 1 : 0, new CliTextPresenter(text).PrintProviderAccess("provider check", reply));
        Assert.Equal(failed ? 1 : 0, new CliJsonPresenter(json).PrintProviderAccess("provider check", reply));
        Assert.Contains("WARNING: Server certificate validation is explicitly disabled.", text.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal("Server certificate validation is explicitly disabled.", document.RootElement.GetProperty("data"u8).GetProperty("warning"u8).GetString());
        Assert.DoesNotContain("BEGIN CERTIFICATE", json.ToString());
    }

    [Theory]
    [InlineData(ProviderConnectionDiagnosticCode.TlsValidationFailed, "tls-validation-failed")]
    [InlineData(ProviderConnectionDiagnosticCode.TlsTrustFailed, "tls-trust-failed")]
    [InlineData(ProviderConnectionDiagnosticCode.TlsNameMismatch, "tls-name-mismatch")]
    [InlineData(ProviderConnectionDiagnosticCode.TlsCertificateTimeInvalid, "tls-certificate-time-invalid")]
    [InlineData(ProviderConnectionDiagnosticCode.TlsCertificateRevoked, "tls-certificate-revoked")]
    [InlineData(ProviderConnectionDiagnosticCode.TlsRevocationUnavailable, "tls-revocation-unavailable")]
    [InlineData(ProviderConnectionDiagnosticCode.DnsUnavailable, "dns-unavailable")]
    [InlineData(ProviderConnectionDiagnosticCode.ConnectionUnavailable, "connection-unavailable")]
    public void Closed_transport_facts_have_safe_consistent_presentations(ProviderConnectionDiagnosticCode code, string expected)
    {
        var reply = new ProviderAccessReply
        {
            Status = ProviderAccessStatus.PreflightFailed,
            Preflight = ProviderPreflightStatus.ServerUnavailable,
            ConnectionDiagnostic = code
        };
        using var text = new StringWriter(); using var json = new StringWriter();
        Assert.Equal(1, new CliTextPresenter(text).PrintProviderAccess("provider check", reply));
        Assert.Equal(1, new CliJsonPresenter(json).PrintProviderAccess("provider check", reply));
        Assert.Contains("connection: " + expected, text.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(expected, document.RootElement.GetProperty("data"u8).GetProperty("connectionDiagnostic"u8).GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_alias_selection_renders_the_completed_preflight_warning(bool unchanged)
    {
        var reply = new ProviderAccessReply
        {
            Status = unchanged ? ProviderAccessStatus.Unchanged : ProviderAccessStatus.Changed,
            Preflight = ProviderPreflightStatus.Ready,
            ServerCertificateValidationDisabled = true,
            Binding = new() { Alias = "kubernetes.primary", InstanceId = "019d03cf-a000-7000-8000-000000000002", Revision = 1 },
        };
        using var text = new StringWriter(); using var json = new StringWriter();
        Assert.Equal(0, new CliTextPresenter(text).PrintProviderAccess("provider alias set", reply));
        Assert.Equal(0, new CliJsonPresenter(json).PrintProviderAccess("provider alias set", reply));
        Assert.Contains("WARNING: Server certificate validation is explicitly disabled.", text.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal("ready", document.RootElement.GetProperty("data"u8).GetProperty("preflight"u8).GetString());
        Assert.NotNull(document.RootElement.GetProperty("data"u8).GetProperty("warning"u8).GetString());
    }

    [Fact]
    public void Unknown_or_contradictory_connection_metadata_cannot_print_a_success()
    {
        var reply = new ProviderAccessReply
        {
            Status = ProviderAccessStatus.PreflightFailed,
            Preflight = ProviderPreflightStatus.ServerUnavailable,
            ConnectionDiagnostic = (ProviderConnectionDiagnosticCode)999
        };
        Assert.Throws<InvalidDataException>(() => ProviderAccessPresentation.Data("provider check", reply));
        reply.ConnectionDiagnostic = ProviderConnectionDiagnosticCode.TlsValidationFailed;
        reply.Status = ProviderAccessStatus.Ready; reply.Preflight = ProviderPreflightStatus.Ready;
        Assert.Throws<InvalidDataException>(() => ProviderAccessPresentation.Data("provider check", reply));
        reply.ClearPreflight(); reply.ServerCertificateValidationDisabled = true;
        Assert.Throws<InvalidDataException>(() => ProviderAccessPresentation.Data("provider check", reply));
    }
}
