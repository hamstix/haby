using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class SharedReferenceDeliveryTests
{
    [Fact]
    public async Task Repeated_shared_projections_resolve_once_per_request_and_a_later_formatter_failure_returns_no_partial_JSON()
    {
        var scenario = await Database();
        await using var services = Services(scenario.Connection, scenario.Boundary);
        var profile = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
            services.GetRequiredService<IConfigurationProfileImportStore>(), TimeProvider.System).ExecuteAsync("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"shared-layout","revision":2},"spec":{
             "documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{"Mode":"frozen","Unrelated":{"Password":"literal"}}}},
             "bindingRules":{"shared":{"slot":"service","rendererRef":"sample.shared-access/v1","options":{"consumer":2,"client":{"timeout":20}},
              "documentProjections":[{"documentRef":"settings","path":"/Access"},{"documentRef":"settings","path":"/Again"}]}}}}
            """u8.ToArray(), "profile-author"));
        var environment = (await services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
        var assignments = services.GetRequiredService<IProfileAssignmentStore>();
        var target = new ProfileAssignmentTarget.Environment();
        var before = (await assignments.FindAsync(environment, target, default))!;
        Assert.Equal(AssignmentOutcome.Updated, (await assignments.ApplyAsync(environment,
            new(target, [new ProfileDecision.Use(profile.Revision.Reference)]), new AssignmentPrecondition.ExpectedConcurrencyToken(before.Token),
            new("operator", DateTimeOffset.UtcNow, null), default)).Outcome);
        var application = await Materialize(scenario);
        var grant = application.Grants["api"];
        for (var request = 0; request < 2; request++)
        {
            scenario.Boundary.ResetMaterial(); scenario.Boundary.FormatterCalls = 0;
            using var result = await services.GetRequiredService<DeliverConfiguration>().ExecuteAsync(grant.Credential, grant.Grant.Document.Id, default);
            Assert.Equal(ConfigurationDeliveryStatus.Delivered, result.Status);
            Assert.Equal(1, scenario.Boundary.MaterialReads); Assert.Equal(2, scenario.Boundary.PayloadQueries);
            Assert.Equal(2, scenario.Boundary.FormatterCalls);
            using var json = System.Text.Json.JsonDocument.Parse(result.Json);
            Assert.Equal(json.RootElement.GetProperty("Access"u8).GetRawText(), json.RootElement.GetProperty("Again"u8).GetRawText());
            Assert.Equal("literal", json.RootElement.GetProperty("Unrelated"u8).GetProperty("Password"u8).GetString());
        }
        scenario.Boundary.ResetMaterial(); scenario.Boundary.FormatterCalls = 0; scenario.Boundary.ThrowFormatterAtCall = 2;
        using (var refused = await services.GetRequiredService<DeliverConfiguration>().ExecuteAsync(grant.Credential, grant.Grant.Document.Id, default))
        {
            Assert.NotEqual(ConfigurationDeliveryStatus.Delivered, refused.Status); Assert.True(refused.Json.IsEmpty);
            Assert.Equal(2, scenario.Boundary.FormatterCalls); Assert.Equal(1, scenario.Boundary.MaterialReads);
        }
        scenario.Boundary.ThrowFormatterAtCall = 0;
        await Delivery(application, "api", true);
    }

    private sealed class PausedApplicationAuthorization(IConfigurationApplicationAuthorization inner) : IConfigurationApplicationAuthorization
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> AuthorizeAsync(ConfigurationDeliveryArtifact artifact, IReadOnlyList<ConfigurationSharedCredentialUse> uses, CancellationToken token)
        {
            var authorized = await inner.AuthorizeAsync(artifact, uses, token);
            if (!authorized) return false;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(token);
            return true;
        }
    }
    [Fact]
    public async Task Revocation_permits_only_the_already_authorized_inflight_request_and_cancellation_never_reads_material()
    {
        foreach (var cancel in new[] { false, true })
        {
            var scenario = await Database(); var application = await Materialize(scenario);
            var grant = application.Grants["api"];
            await using var services = Services(scenario.Connection, scenario.Boundary);
            var pause = new PausedApplicationAuthorization(services.GetRequiredService<IConfigurationApplicationAuthorization>());
            var delivery = new DeliverConfiguration(services.GetRequiredService<IConfigurationAccessStore>(),
                services.GetRequiredService<IConfigurationDeliveryDocumentStore>(), services.GetRequiredService<ISecretStore>(),
                services.GetRequiredService<ConfigurationRenderingCatalog>(), services.GetServices<IConfigurationResolutionBinding>(), pause);
            scenario.Boundary.ResetMaterial();
            using var cancellation = new CancellationTokenSource();
            var pending = delivery.ExecuteAsync(grant.Credential, grant.Grant.Document.Id, cancellation.Token).AsTask();
            try
            {
                await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(0, scenario.Boundary.PayloadQueries);
                if (cancel)
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
                    Assert.Equal(0, scenario.Boundary.PayloadQueries);
                }
                else
                {
                    Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await services.GetRequiredService<ISharedCredentialAdministration>()
                        .RevokeAsync(Scope(scenario, SharedCredentialUse.ApplicationDelivery), "revoker", default));
                    pause.Release.TrySetResult();
                    using var completed = await pending;
                    Assert.Equal(ConfigurationDeliveryStatus.Delivered, completed.Status);
                    Assert.Equal(1, scenario.Boundary.MaterialReads);
                }
            }
            finally { pause.Release.TrySetResult(); }
            await Delivery(application, "api", cancel);
        }
    }

    // Only the bounded backend response is faulted; real scope authorization and PostgreSQL
    // reads still execute. Immutable database guards remain enabled throughout this test.
    private sealed class MaterialFault(ISecretStore inner, byte[]? replacement) : ISecretStore
    {
        internal int Calls;
        public async ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token)
        {
            using var original = await inner.ResolveBoundedAsync(definition, reference, maximumBytes, token);
            Calls++;
            if (original is null) throw new InvalidDataException("Positive material control unavailable.");
            return replacement is null ? null : new SecretMaterial(replacement);
        }
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => inner.CreateEnvironmentAsync(id, token);
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => inner.CreateAsync(definition, reference, material, token);
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => inner.AddVersionAsync(environment, reference, material, token);
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => inner.ResolveAsync(definition, reference, token);
    }
    [Fact]
    public async Task Malformed_or_missing_shared_payload_fails_the_whole_response_and_restored_payload_delivers_again()
    {
        var scenario = await Database(); var application = await Materialize(scenario);
        await Delivery(application, "api", true);
        await using var services = Services(scenario.Connection, scenario.Boundary);
        foreach (var invalid in new byte[]?[] { null, [], [0xff], [0], new byte[DeliverConfiguration.MaxSecretBytes + 1] })
        {
            var fault = new MaterialFault(services.GetRequiredService<ISecretStore>(), invalid);
            var delivery = new DeliverConfiguration(services.GetRequiredService<IConfigurationAccessStore>(),
                services.GetRequiredService<IConfigurationDeliveryDocumentStore>(), fault, services.GetRequiredService<ConfigurationRenderingCatalog>(),
                services.GetServices<IConfigurationResolutionBinding>(), services.GetRequiredService<IConfigurationApplicationAuthorization>());
            scenario.Boundary.ResetMaterial();
            var grant = application.Grants["api"];
            using var result = await delivery.ExecuteAsync(grant.Credential, grant.Grant.Document.Id, default);
            Assert.NotEqual(ConfigurationDeliveryStatus.Delivered, result.Status); Assert.True(result.Json.IsEmpty);
            Assert.Equal(1, fault.Calls); Assert.Equal(1, scenario.Boundary.MaterialReads); Assert.Equal(2, scenario.Boundary.PayloadQueries);
            await Delivery(application, "api", true);
        }
    }
}
