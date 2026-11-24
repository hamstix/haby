using Haby.Application;
using Haby.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class SharedReferenceDeliveryTests
{
    [Fact]
    public async Task Invalid_shared_authority_refuses_before_unrelated_static_Profile_secret_material_with_a_live_positive_control()
    {
        var scenario = await Database();
        await using var services = Services(scenario.Connection, scenario.Boundary);
        using var material = new SecretMaterial("profile-configuration-test-material"u8.ToArray());
        Assert.Equal(SecretOutcome.Created, (await services.GetRequiredService<SecretAdministration>().CreateAsync("pilot", "configuration", Guid.NewGuid(),
            material, "creator", default, SecretPurpose.Configuration)).Outcome);
        var profile = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
            services.GetRequiredService<IConfigurationProfileImportStore>(), TimeProvider.System).ExecuteAsync("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"shared-layout","revision":2},"spec":{
             "documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{"Mode":"frozen"}}},
             "bindingRules":{"shared":{"slot":"service","rendererRef":"sample.shared-access/v1","options":{"consumer":2,"client":{"timeout":20}},
              "documentProjections":[{"documentRef":"settings","path":"/Access"}]}},
             "staticContributions":{"secret":{"documentRef":"settings","path":"/Static","value":{},
              "secretProjections":[{"path":"/Password","format":"string","secretRef":{"name":"configuration","version":1}}]}}}}
            """u8.ToArray(), "profile-author"));
        var environment = (await services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
        var assignments = services.GetRequiredService<IProfileAssignmentStore>(); var target = new ProfileAssignmentTarget.Environment();
        var prior = (await assignments.FindAsync(environment, target, default))!;
        Assert.Equal(AssignmentOutcome.Updated, (await assignments.ApplyAsync(environment, new(target, [new ProfileDecision.Use(profile.Revision.Reference)]),
            new AssignmentPrecondition.ExpectedConcurrencyToken(prior.Token), new("operator", DateTimeOffset.UtcNow, null), default)).Outcome);
        var application = await Materialize(scenario); var grant = application.Grants["api"];
        async Task Positive()
        {
            scenario.Boundary.ResetMaterial();
            using var delivered = await services.GetRequiredService<DeliverConfiguration>().ExecuteAsync(grant.Credential, grant.Grant.Document.Id, default);
            Assert.Equal(ConfigurationDeliveryStatus.Delivered, delivered.Status);
            Assert.Equal(2, scenario.Boundary.MaterialReads); Assert.Equal(4, scenario.Boundary.PayloadQueries);
            using var json = System.Text.Json.JsonDocument.Parse(delivered.Json);
            Assert.Equal("profile-configuration-test-material", json.RootElement.GetProperty("Static"u8).GetProperty("Password"u8).GetString());
            Assert.Equal("shared-application-test-material", json.RootElement.GetProperty("Access"u8).GetProperty("Password"u8).GetString());
        }
        await Positive();
        scenario.Boundary.BindingFault = "purpose";
        await Delivery(application, "api", false);
        scenario.Boundary.BindingFault = null;
        await Positive();
        await Delivery(application, "api", false, "formatter");
        await Positive();
        Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await services.GetRequiredService<ISharedCredentialAdministration>()
            .RevokeAsync(Scope(scenario, SharedCredentialUse.ApplicationDelivery), "revoker", default));
        await Delivery(application, "api", false);
    }
}
