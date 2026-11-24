using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class SharedReferenceDeliveryTests
{
    [Fact]
    public async Task Restart_and_new_Provider_Profile_defaults_and_credentials_cannot_rebind_created_Reference_documents()
    {
        var contract = new Definition();
        foreach (var exact in new[] { "0", "0.0", "1.0", "1e3", "1023" })
            Assert.True(contract.ValidateOptions(Options(exact, "5000", "20"), true));
        foreach (var invalid in new[] { "-1", "1.1", "1e-100000", "1024", "9007199254740993", "1e100000", "1.000000000000000000000000000000000001" })
            Assert.False(contract.ValidateOptions(Options(invalid, "5000", "20"), true));
        var scenario = await Database(); var original = await Materialize(scenario);
        var grant = original.Grants["api"];
        string encoded;
        await using (var before = Services(scenario.Connection, scenario.Boundary))
        {
            var authorization = (await before.GetRequiredService<IConfigurationAccessStore>().AuthorizeAsync(grant.Credential, grant.Grant.Document.Id, default))!;
            encoded = ConfigurationDocumentCodec.Encode((await before.GetRequiredService<IConfigurationDeliveryDocumentStore>().ReadAsync(authorization, default))!.Revision);
            using var replacement = new SecretMaterial("replacement-shared-test-material"u8.ToArray());
            Assert.Equal(SecretOutcome.Created, (await before.GetRequiredService<SecretAdministration>().CreateAsync("pilot", "shared-next", Guid.NewGuid(),
                replacement, "creator", default, SecretPurpose.SharedApplicationCredential)).Outcome);
            var declaration = ProviderDeclarationTests.Declaration.Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal)
                .Replace("unreachable.invalid", "next.invalid", StringComparison.Ordinal)
                .Replace("\"secretRefs\":{", "\"secretRefs\":{\"system-access-credential\":{\"secretName\":\"shared-next\",\"version\":1},\"application-credential\":{\"secretName\":\"shared-next\",\"version\":1},", StringComparison.Ordinal);
            Assert.Equal(ProviderOutcome.Created, (await before.GetRequiredService<ProviderAdministration>().ApplyAsync(Encoding.UTF8.GetBytes(declaration),
                null, true, 1, "new-issuer", default)).Outcome);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await before.GetRequiredService<IProviderAliasStore>().ChangeAsync(scenario.Environment,
                ProviderAlias.Create("shared.primary"), new(scenario.Provider, ProviderConfigurationRevisionNumber.Create(1)),
                new(scenario.Provider, ProviderConfigurationRevisionNumber.Create(2)), "operator", default)).Outcome);
            var profile = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
                before.GetRequiredService<IConfigurationProfileImportStore>(), TimeProvider.System).ExecuteAsync("""
                {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"shared-layout","revision":2},"spec":{
                 "documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{"Mode":"replacement"}}},
                 "bindingRules":{"shared":{"slot":"service","rendererRef":"sample.shared-access/v1","options":{"consumer":7,"client":{"timeout":40,"label":"new-profile"}},
                  "documentProjections":[{"documentRef":"settings","path":"/Access"}]}}}}
                """u8.ToArray(), "profile-author"));
            var environment = (await before.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
            var assignments = before.GetRequiredService<IProfileAssignmentStore>();
            var target = new ProfileAssignmentTarget.Environment(); var prior = (await assignments.FindAsync(environment, target, default))!;
            Assert.Equal(AssignmentOutcome.Updated, (await assignments.ApplyAsync(environment, new(target, [new ProfileDecision.Use(profile.Revision.Reference)]),
                new AssignmentPrecondition.ExpectedConcurrencyToken(prior.Token), new("operator", DateTimeOffset.UtcNow, null), default)).Outcome);
        }
        scenario.Boundary.ResetMaterial();
        await using var restarted = Services(scenario.Connection, scenario.Boundary, "defaults");
        var authority = (await restarted.GetRequiredService<IConfigurationAccessStore>().AuthorizeAsync(grant.Credential, grant.Grant.Document.Id, default))!;
        var artifact = (await restarted.GetRequiredService<IConfigurationDeliveryDocumentStore>().ReadAsync(authority, default))!;
        Assert.Equal(encoded, ConfigurationDocumentCodec.Encode(artifact.Revision));
        Assert.Equal(0, scenario.Boundary.PayloadQueries);
        using var delivered = await restarted.GetRequiredService<DeliverConfiguration>().ExecuteAsync(grant.Credential, grant.Grant.Document.Id, default);
        Assert.Equal(ConfigurationDeliveryStatus.Delivered, delivered.Status);
        Assert.Equal(1, scenario.Boundary.MaterialReads); Assert.Equal(2, scenario.Boundary.PayloadQueries);
        using var json = JsonDocument.Parse(delivered.Json);
        Assert.Equal("frozen", json.RootElement.GetProperty("Mode"u8).GetString());
        var access = json.RootElement.GetProperty("Access"u8);
        Assert.Equal("unreachable.invalid", access.GetProperty("EndPoints"u8)[0].GetProperty("Host"u8).GetString());
        Assert.Equal("shared-application-test-material", access.GetProperty("Password"u8).GetString());
        Assert.Equal(0m, access.GetProperty("Consumer"u8).GetDecimal());
        Assert.Equal(5000m, access.GetProperty("ConnectTimeout"u8).GetDecimal());
        Assert.Equal(20m, access.GetProperty("Client"u8).GetProperty("timeout"u8).GetDecimal());
        Assert.False(access.GetProperty("Client"u8).TryGetProperty("label"u8, out _));
        Assert.Equal(encoded, ConfigurationDocumentCodec.Encode(artifact.Revision));
    }
}
