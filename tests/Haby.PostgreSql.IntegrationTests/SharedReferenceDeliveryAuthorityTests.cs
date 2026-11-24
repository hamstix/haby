using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class SharedReferenceDeliveryTests
{
    private sealed record ReadyApplication(Scenario Scenario, string Application, DurableChangeSetCommit Commit,
        IReadOnlyDictionary<string, ConfigurationAccessCreated> Grants)
    {
        public override string ToString() => "[Reference configuration fixture]";
    }
    private static async Task<ReadyApplication> Materialize(Scenario scenario, string application = "reference-consumer")
    {
        var commit = await Confirm(scenario, application);
        scenario.Boundary.ResetMaterial();
        await using var services = Services(scenario.Connection, scenario.Boundary);
        var documents = services.GetRequiredService<IConfigurationDocumentStore>();
        var access = services.GetRequiredService<IConfigurationAccessStore>();
        var grants = new Dictionary<string, ConfigurationAccessCreated>();
        foreach (var component in new[] { "api", "worker" })
        {
            var target = new ConfigurationDocumentTarget(commit.DeploymentId, commit.Generation, ManifestLocalId.Create(component), ManifestLocalId.Create("settings"));
            Assert.Equal(ConfigurationDocumentStatus.Created, (await documents.MaterializeAsync(target, "operator", default)).Status);
            var grant = await access.CreateAsync(new(EnvironmentId.Create("pilot"), Haby.Domain.ApplicationId.Create(application),
                ManifestLocalId.Create(component), ManifestLocalId.Create("settings"), null), "operator", null, default);
            Assert.NotNull(grant); grants.Add(component, grant);
        }
        Assert.Equal(0, scenario.Boundary.PayloadQueries);
        return new(scenario, application, commit, grants);
    }
    private static async Task Delivery(ReadyApplication application, string component, bool allowed, string? registrationFault = null,
        ConfigurationAccessCreated? grant = null)
    {
        var scenario = application.Scenario;
        scenario.Boundary.ResetMaterial();
        await using var services = Services(scenario.Connection, scenario.Boundary, registrationFault);
        grant ??= application.Grants[component];
        using var result = await services.GetRequiredService<DeliverConfiguration>().ExecuteAsync(grant.Credential, grant.Grant.Document.Id, default);
        if (allowed)
        {
            Assert.Equal(ConfigurationDeliveryStatus.Delivered, result.Status);
            Assert.Equal(1, scenario.Boundary.MaterialReads);
            Assert.Equal(2, scenario.Boundary.PayloadQueries);
            using var json = JsonDocument.Parse(result.Json);
            Assert.Equal("shared-application-test-material", json.RootElement.GetProperty("Access"u8).GetProperty("Password"u8).GetString());
        }
        else
        {
            Assert.NotEqual(ConfigurationDeliveryStatus.Delivered, result.Status);
            Assert.True(result.Json.IsEmpty);
            Assert.Equal(0, scenario.Boundary.MaterialReads);
            Assert.Equal(0, scenario.Boundary.PayloadQueries);
        }
    }
    private static SharedCredentialScope Scope(Scenario scenario, SharedCredentialUse use, long revision = 1) => new(scenario.Environment,
        scenario.Provider, ProviderConfigurationRevisionNumber.Create(revision), use == SharedCredentialUse.ApplicationDelivery ?
            PostgreSqlProviderConfigurationCodec.ApplicationCredentialSlot : PostgreSqlProviderConfigurationCodec.SystemAccessCredentialSlot, use, scenario.Secret);

    [Fact]
    public async Task Shared_use_revocation_blocks_every_old_consumer_and_new_grant_without_reviving_old_artifacts()
    {
        var scenario = await Database();
        var first = await Materialize(scenario);
        var peer = await Materialize(scenario, "reference-peer");
        foreach (var application in new[] { first, peer })
            foreach (var component in application.Grants.Keys) await Delivery(application, component, true);
        scenario.Boundary.ResetMaterial();
        await using (var services = Services(scenario.Connection, scenario.Boundary))
        {
            var authority = services.GetRequiredService<ISharedCredentialAdministration>();
            var before = await authority.InspectAsync(scenario.Environment, scenario.Provider, ProviderConfigurationRevisionNumber.Create(1), default);
            Assert.Equal(4, before.Single(x => x.Scope.Use == SharedCredentialUse.ApplicationDelivery).Dependencies.Count);
            Assert.Empty(before.Single(x => x.Scope.Use == SharedCredentialUse.SystemAccess).Dependencies);
            Assert.Equal(0, scenario.Boundary.PayloadQueries);
            Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await authority.RevokeAsync(Scope(scenario, SharedCredentialUse.ApplicationDelivery), "revoker", default));
            using var system = await services.GetRequiredService<ISharedCredentialSystemAccess>().ResolveAsync(Scope(scenario, SharedCredentialUse.SystemAccess), 65536, default);
            Assert.NotNull(system);
        }
        foreach (var application in new[] { first, peer })
            foreach (var component in application.Grants.Keys) await Delivery(application, component, false);
        await using (var restart = Services(scenario.Connection, scenario.Boundary))
        {
            var access = restart.GetRequiredService<IConfigurationAccessStore>();
            var newGrant = await access.CreateAsync(new(EnvironmentId.Create("pilot"), Haby.Domain.ApplicationId.Create(first.Application),
                ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"), null), "new-grant", null, default);
            Assert.NotNull(newGrant); await Delivery(first, "api", false, grant: newGrant);
            var history = (await restart.GetRequiredService<ISharedCredentialAdministration>().InspectAsync(scenario.Environment, scenario.Provider,
                ProviderConfigurationRevisionNumber.Create(1), default)).Single(x => x.Scope.Use == SharedCredentialUse.ApplicationDelivery);
            Assert.Equal("revoker", history.RevokedBy); Assert.NotNull(history.RevokedAt); Assert.Equal(4, history.Dependencies.Count);
            Assert.Equal(first.Grants.Values.Concat(peer.Grants.Values).Select(x => x.Grant.Document.Id.ToString()).Order(StringComparer.Ordinal),
                history.Dependencies.Select(x => x.Document.ToString()).Order(StringComparer.Ordinal));
            Assert.All(history.Dependencies, x => Assert.True(x.ActiveAssociation));
            var declaration = ProviderDeclarationTests.Declaration.Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal)
                .Replace("\"secretRefs\":{", "\"secretRefs\":{\"system-access-credential\":{\"secretName\":\"shared\",\"version\":1},\"application-credential\":{\"secretName\":\"shared\",\"version\":1},", StringComparison.Ordinal);
            Assert.Equal(ProviderOutcome.Created, (await restart.GetRequiredService<ProviderAdministration>().ApplyAsync(Encoding.UTF8.GetBytes(declaration),
                null, false, null, "new-issuer", default)).Outcome);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await restart.GetRequiredService<IProviderAliasStore>().ChangeAsync(scenario.Environment,
                ProviderAlias.Create("shared.primary"), new(scenario.Provider, ProviderConfigurationRevisionNumber.Create(1)),
                new(scenario.Provider, ProviderConfigurationRevisionNumber.Create(2)), "operator", default)).Outcome);
        }
        var next = await Materialize(scenario, "next-reference");
        await Delivery(next, "api", true);
        await Delivery(first, "api", false);
        await Delivery(peer, "worker", false);
    }

    [Fact]
    public async Task System_revocation_preserves_delivery_and_inactive_own_association_does_not_disable_peers()
    {
        var scenario = await Database();
        var first = await Materialize(scenario);
        var peer = await Materialize(scenario, "reference-peer");
        await using (var services = Services(scenario.Connection, scenario.Boundary))
            Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await services.GetRequiredService<ISharedCredentialAdministration>()
                .RevokeAsync(Scope(scenario, SharedCredentialUse.SystemAccess), "system-revoker", default));
        foreach (var application in new[] { first, peer })
            foreach (var component in application.Grants.Keys) await Delivery(application, component, true);
        await using (var services = Services(scenario.Connection, scenario.Boundary))
        await using (var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            var owner = Guid.Parse(first.Grants["api"].Grant.Document.Revision.ApplicationRecordId.ToString());
            // Trusted test-only maintenance simulates an inactive association without adding a removal API.
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM resource_management_heads WHERE application_record_id = {owner} AND resource_local_id = {"left"}"));
        }
        await Delivery(first, "api", false);
        await Delivery(first, "worker", true);
        await Delivery(peer, "api", true);
        scenario.Boundary.ResetMaterial();
        await using var restarted = Services(scenario.Connection, scenario.Boundary);
        var history = (await restarted.GetRequiredService<ISharedCredentialAdministration>().InspectAsync(scenario.Environment, scenario.Provider,
            ProviderConfigurationRevisionNumber.Create(1), default)).Single(x => x.Scope.Use == SharedCredentialUse.ApplicationDelivery);
        Assert.Equal(4, history.Dependencies.Count);
        Assert.False(history.Dependencies.Single(x => x.Document == first.Grants["api"].Grant.Document.Id).ActiveAssociation);
        Assert.All(history.Dependencies.Where(x => x.Document != first.Grants["api"].Grant.Document.Id), x => Assert.True(x.ActiveAssociation));
        Assert.Equal(0, scenario.Boundary.PayloadQueries);
    }

    [Fact]
    public async Task Invalid_scope_and_unavailable_registrations_refuse_before_payload_queries_with_a_live_positive_control()
    {
        var scenario = await Database(); var application = await Materialize(scenario);
        await Delivery(application, "api", true);
        foreach (var fault in new[] { "purpose", "use", "environment", "provider", "revision", "slot", "version" })
        {
            scenario.Boundary.BindingFault = fault;
            try { await Delivery(application, "api", false); }
            finally { scenario.Boundary.BindingFault = null; }
            await Delivery(application, "api", true);
        }
        foreach (var fault in new[] { "binding", "formatter", "evidence" })
        {
            await Delivery(application, "api", false, fault);
            await Delivery(application, "api", true);
        }
        // Delivery must not require renderer instances or rerender the saved document.
        await Delivery(application, "api", true, "renderer");
        scenario.Boundary.ResetMaterial();
        await using var services = Services(scenario.Connection, scenario.Boundary);
        var delivery = services.GetRequiredService<DeliverConfiguration>();
        using var wrongDocument = await delivery.ExecuteAsync(application.Grants["api"].Credential, application.Grants["worker"].Grant.Document.Id, default);
        Assert.Equal(ConfigurationDeliveryStatus.Denied, wrongDocument.Status); Assert.True(wrongDocument.Json.IsEmpty);
        using var wrongToken = await delivery.ExecuteAsync("haby_operator_v1.not-a-delivery-token", application.Grants["api"].Grant.Document.Id, default);
        Assert.Equal(ConfigurationDeliveryStatus.Denied, wrongToken.Status); Assert.True(wrongToken.Json.IsEmpty);
        Assert.Equal(0, scenario.Boundary.PayloadQueries);
    }
}
