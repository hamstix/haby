using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorized_delivery_reuses_confirmed_output_across_direct_or_slot_plus_direct_requests_without_mutating_artifact(bool slotAndDirect)
    {
        var profile = JsonNode.Parse(RenderingProfile)!;
        profile["spec"]!["documents"]!.AsObject().Remove("settings");
        var scenario = await Prepare(full: true, render: true, renderingProfile: profile.ToJsonString(), configureManifest: manifest =>
        {
            foreach (var name in new[] { "api", "worker" })
                manifest["manifest"]!["spec"]!["components"]![name]!["documents"] = JsonNode.Parse("""{"settings":{"fileName":"appsettings.json","format":"json","template":{}}}"""u8);
            var binding = manifest["manifest"]!["spec"]!["components"]!["api"]!["bindings"]!["storage"]!.AsObject();
            binding["options"]!["sslMode"] = "disable";
            if (!slotAndDirect) binding.Remove("configurationSlot");
            var projections = new JsonArray();
            foreach (var path in slotAndDirect ? new[] { "/Second" } : new[] { "/First", "/Second" })
                projections.Add(new JsonObject { ["documentRef"] = "settings", ["path"] = path, ["rendererRef"] = PostgreSqlConnectionRendering.Renderer.Value });
            binding["documentProjections"] = projections;
        });
        Assert.Equal(2, scenario.Plan.Inputs.Configuration!.Layouts.Single(x => x.Component.Value == "api").BindingRequests.Count);
        var reads = new PayloadReads(); var formatter = new DeliveryFormatterFault();
        var catalog = new ConfigurationRenderingCatalog([new PostgreSqlConnectionRendererContract()], [new PostgreSqlConnectionRenderer()], PostgreSqlConnectionRendering.FormatterCatalog(), [formatter]);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            services => services.AddSingleton(catalog).AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(reads)));
        await host.StartAsync(); using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var admin = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await admin.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 })).Status);
        var document = await admin.MaterializeConfigurationAsync(DocumentRequest(scenario));
        Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.Created, document.Status);
        ConfigurationDocumentRow saved;
        await using (var verify = Context(scenario.Connection)) saved = await verify.ConfigurationDocuments.AsNoTracking().SingleAsync();
        var revision = ConfigurationDocumentCodec.Decode(saved.Payload, saved.Fingerprint);
        using (var associations = JsonDocument.Parse(revision.OutputAssociations))
        {
            Assert.Equal(2, associations.RootElement.GetArrayLength());
            Assert.True(associations.RootElement[0].GetRawText() == associations.RootElement[1].GetRawText());
        }
        var grant = await admin.CreateConfigurationAccessAsync(new() { Selector = DocumentRequest(scenario) });
        Assert.Equal(ConfigurationAccessStatus.Created, grant.Status);
        var initialReads = reads.Count; var initialFormats = formatter.Count;
        var before = await ConfigurationState(scenario);
        var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        for (var i = 0; i < 2; i++)
        {
            var reply = await runtime.DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
            using var json = JsonDocument.Parse(reply.Json.Memory);
            var first = slotAndDirect ? json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8) : json.RootElement.GetProperty("First"u8);
            var second = json.RootElement.GetProperty("Second"u8);
            Assert.True(first.GetString() == second.GetString(), "Repeated projections must receive the same complete connection string.");
            var connection = new Npgsql.NpgsqlConnectionStringBuilder(first.GetString());
            Assert.Equal(scenario.Role, connection.Username); Assert.Equal(scenario.Database, connection.Database); Assert.Equal(200, connection.Timeout);
            Assert.False(string.IsNullOrEmpty(connection.Password));
            Assert.Equal(initialReads + i + 1, reads.Count); Assert.Equal(initialFormats + 2 * (i + 1), formatter.Count);
        }
        Assert.True(before == await ConfigurationState(scenario), "Delivery changed protected provisioning state.");
        await using (var verify = Context(scenario.Connection))
        {
            var after = await verify.ConfigurationDocuments.AsNoTracking().SingleAsync();
            Assert.True(saved.Payload == after.Payload, "Delivery changed the saved artifact."); Assert.Equal(saved.Fingerprint, after.Fingerprint);
        }
        await host.StopAsync();
    }
}
