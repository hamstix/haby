using System.Text.Json.Nodes;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed partial class ConfigurationDeliveryTests
{
    private sealed class RuntimePorts(Harness harness) : IRuntimeIdentityStore, IRuntimeAuthenticator, IRuntimeConfigurationStore
    {
        internal readonly List<string> Calls = [];
        internal readonly RuntimeTrust Trust = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            "implementation", "kind", "configuration", 1, "{}", "authenticator", "audience", "contract", 1, "{}", "inventory", 1, "{}", false);
        internal RuntimeAuthenticationOutcome Authentication = RuntimeAuthenticationOutcome.Authenticated;
        internal bool Published = true, Current = true;
        internal Func<Task>? BeforeAuthorization, BeforeRead;
        public RuntimeAuthenticatorContract Contract => new(Trust.Authenticator, Trust.Contract, Trust.Format, Trust.ProviderKind,
            Trust.ConfigurationShape, Trust.ConfigurationFormat, Trust.Implementation);
        public bool Validate(RuntimeTrust trust) => true;
        public ValueTask<RuntimeTrust?> ReadAsync(Guid binding, CancellationToken token) { Calls.Add("trust"); return ValueTask.FromResult<RuntimeTrust?>(Trust); }
        public ValueTask<bool> IsCurrentAsync(RuntimeTrust trust, CancellationToken token) { Calls.Add("current"); return ValueTask.FromResult(Current); }
        public ValueTask<RuntimeAuthenticationOutcome> AuthenticateAsync(RuntimeTrust trust, ReadOnlyMemory<byte> credential, CancellationToken token)
        { Calls.Add("token-review"); return ValueTask.FromResult(Authentication); }
        public async ValueTask<RuntimeConfigurationAuthority?> AuthorizeAsync(RuntimeAuthenticatedContext identity, ConfigurationDocumentRevisionId revision, CancellationToken token)
        {
            Calls.Add("permission"); if (BeforeAuthorization is not null) await BeforeAuthorization();
            Assert.Equal(new(Trust.Binding, Trust.Environment, Trust.ManagedWorkload, Trust.Provider), identity);
            Assert.Equal(harness.Store.Artifact.Revision.Metadata.Id, revision);
            return Published ? new(identity, Guid.NewGuid(), revision, harness.Store.Artifact.Revision.Metadata.Target,
                Guid.NewGuid(), Guid.NewGuid(), 1, "source", "inputs", Guid.NewGuid()) : null;
        }
        public async ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(RuntimeConfigurationAuthority authority, CancellationToken token)
        {
            Calls.Add("artifact"); authority.Consume(); if (BeforeRead is not null) await BeforeRead();
            return harness.Store.Artifact;
        }
        internal DeliverRuntimeConfiguration Service(RuntimeAuthenticationAdmission admission) => new(
            new(this, [this], admission, TimeProvider.System), this, new(harness.Store, harness.Catalog, [harness.Adapter]));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("lifecycle")]
    [InlineData("permission")]
    public async Task Runtime_delivery_requires_both_fresh_identity_and_exact_permission_before_artifact_or_material(string refused)
    {
        var h = await Prepare(); var ports = new RuntimePorts(h);
        if (refused == "identity") ports.Authentication = RuntimeAuthenticationOutcome.Unauthenticated;
        if (refused == "lifecycle") ports.Current = false;
        if (refused == "permission") ports.Published = false;
        using var admission = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        using var result = await ports.Service(admission).ExecuteAsync(ports.Trust.Binding, h.Store.Artifact.Revision.Metadata.Id, "projected"u8.ToArray(), default);
        Assert.Equal(refused == "identity" ? RuntimeDeliveryOutcome.Unauthenticated : RuntimeDeliveryOutcome.PermissionDenied, result.Outcome);
        Assert.DoesNotContain("artifact", ports.Calls); Assert.Equal(0, h.Store.Resolutions); Assert.Equal(0, h.Store.Reads);
        if (refused != "permission") Assert.DoesNotContain("permission", ports.Calls);
    }

    [Fact]
    public async Task Runtime_admission_and_both_output_limit_origins_have_distinct_closed_outcomes()
    {
        foreach (var origin in new[] { "admission", "materializer", "document" })
        {
            var h = await Prepare(); var ports = new RuntimePorts(h);
            if (origin == "admission") ports.Authentication = RuntimeAuthenticationOutcome.ResourceExhausted;
            if (origin == "materializer") h.Adapter.Text = new string('a', 4 * 1024 * 1024 + 1);
            if (origin == "document") ports.BeforeRead = () => throw new ConfigurationDocumentLimitException();
            using var admission = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
            using var result = await ports.Service(admission).ExecuteAsync(ports.Trust.Binding, h.Store.Artifact.Revision.Metadata.Id, "projected"u8.ToArray(), default);
            Assert.Equal(origin == "admission" ? RuntimeDeliveryOutcome.AdmissionOverload : RuntimeDeliveryOutcome.OutputLimit, result.Outcome);
            if (origin == "admission") Assert.DoesNotContain("permission", ports.Calls);
            else Assert.Contains("artifact", ports.Calls);
            Assert.True(result.Document is null || result.Document.Json.IsEmpty);
        }
    }

    [Fact]
    public async Task Runtime_materialization_uses_same_complete_core_without_static_authorization_or_grants()
    {
        var h = await Prepare(separateRequests: true); h.Store.Allowed = false;
        var ports = new RuntimePorts(h); using var admission = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        using (var result = await ports.Service(admission).ExecuteAsync(ports.Trust.Binding, h.Store.Artifact.Revision.Metadata.Id, "projected"u8.ToArray(), default))
        {
            Assert.Equal(RuntimeDeliveryOutcome.Delivered, result.Outcome); Assert.False(result.Document!.Json.IsEmpty);
            Assert.Equal(new[] { "trust", "token-review", "current", "permission", "artifact" }, ports.Calls);
            Assert.Equal(1, h.Store.Resolutions); Assert.Equal(2, h.Adapter.Calls); Assert.Equal(0, h.Store.Reads);
        }
        using var repeated = await ports.Service(admission).ExecuteAsync(ports.Trust.Binding, h.Store.Artifact.Revision.Metadata.Id, "replacement"u8.ToArray(), default);
        Assert.Equal(RuntimeDeliveryOutcome.Delivered, repeated.Outcome); Assert.Equal(2, h.Store.Resolutions);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("attempt")]
    [InlineData("formatter")]
    [InlineData("reference")]
    public async Task Runtime_frozen_confidential_authority_failures_precede_all_application_material(string fault)
    {
        var h = await Prepare(); var revision = h.Store.Artifact.Revision;
        if (fault == "purpose") h.Adapter.Purpose = SecretPurpose.ProviderAdministrator;
        if (fault == "formatter") h.Adapter.Reference = new(SecretId.New(), SecretVersionId.New());
        if (fault == "reference") h.Store.Artifact = h.Store.Artifact with { Revision = revision with { Tree = new ConfigurationSecretReference(h.Adapter.Reference) } };
        if (fault == "attempt")
        {
            var outputs = JsonNode.Parse(revision.OutputAssociations)!; outputs[0]![1] = Guid.Empty.ToString();
            h.Store.Artifact = h.Store.Artifact with { Revision = revision with { OutputAssociations = outputs.ToJsonString() } };
        }
        var ports = new RuntimePorts(h); using var admission = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        using var result = await ports.Service(admission).ExecuteAsync(ports.Trust.Binding, revision.Metadata.Id, "projected"u8.ToArray(), default);
        Assert.Equal(RuntimeDeliveryOutcome.FailedPrecondition, result.Outcome); Assert.True(result.Document!.Json.IsEmpty);
        Assert.Equal(0, h.Store.Resolutions); Assert.Equal(0, h.Adapter.Calls);
    }

    [Fact]
    public async Task Runtime_revocation_between_identity_and_permission_is_not_restored_by_authenticated_context()
    {
        var h = await Prepare(); var ports = new RuntimePorts(h);
        ports.BeforeAuthorization = () => { ports.Published = false; return Task.CompletedTask; };
        using var admission = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        using var result = await ports.Service(admission).ExecuteAsync(ports.Trust.Binding, h.Store.Artifact.Revision.Metadata.Id, "projected"u8.ToArray(), default);
        Assert.Equal(RuntimeDeliveryOutcome.PermissionDenied, result.Outcome);
        Assert.DoesNotContain("artifact", ports.Calls); Assert.Equal(0, h.Store.Resolutions);
    }

    [Fact]
    public async Task Runtime_permission_revocation_boundary_is_fresh_per_independent_request()
    {
        var h = await Prepare(); var ports = new RuntimePorts(h); using var admission = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        ports.BeforeRead = () => { ports.Published = false; return Task.CompletedTask; };
        using var first = await ports.Service(admission).ExecuteAsync(ports.Trust.Binding, h.Store.Artifact.Revision.Metadata.Id, "projected"u8.ToArray(), default);
        Assert.Equal(RuntimeDeliveryOutcome.Delivered, first.Outcome);
        using var second = await ports.Service(admission).ExecuteAsync(ports.Trust.Binding, h.Store.Artifact.Revision.Metadata.Id, "projected"u8.ToArray(), default);
        Assert.Equal(RuntimeDeliveryOutcome.PermissionDenied, second.Outcome); Assert.Equal(1, h.Store.Resolutions);
    }
}
