using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Domain;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ApplicationConfigurationTests
{
    private sealed class ConfigurationSecretMaterialReads : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("secret_payloads", StringComparison.Ordinal) &&
                !command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase)) Count++;
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Applied_public_Resource_static_projection_retains_exact_secret_after_profile_replacement()
    {
        var profile = JsonNode.Parse(RenderingProfile)!;
        profile["spec"]!["staticContributions"]!["server"] = JsonNode.Parse("""{"documentRef":"settings","path":"/Serilog","value":{"WriteTo":[{"Args":{}}]},"secretProjections":[{"path":"/WriteTo/0/Args/password","format":"string","secretRef":{"name":"logging-password","version":1}}]}"""u8);
        var reads = new ConfigurationSecretMaterialReads();
        SecretRef? exact = null;
        var scenario = await Prepare(full: true, render: true, renderingProfile: profile.ToJsonString(), planningTrap: reads,
            beforeProfile: async (services, _) =>
            {
                using var material = new SecretMaterial("canary-private"u8.ToArray());
                var created = await services.GetRequiredService<SecretAdministration>().CreateAsync("pilot", "logging-password",
                    Guid.NewGuid(), material, "tester", default, SecretPurpose.Configuration);
                Assert.Equal(SecretOutcome.Created, created.Outcome);
                exact = new SecretRef(created.Secret!.SecretId, Assert.Single(created.Secret.Versions).VersionId);
                reads.Count = 0;
            });
        var named = new ProfileNamedSecretRef("logging-password", 1);
        Assert.Equal(exact, scenario.Plan.Inputs.Configuration!.FrozenSecrets[named]);
        Assert.Equal(exact, ConfigurationPlanningSnapshotCodec.Decode(
            ConfigurationPlanningSnapshotCodec.Encode(scenario.Plan.Inputs.Configuration),
            scenario.Plan.Inputs.Revision).FrozenSecrets[named]);
        Assert.Equal(0, reads.Count);
        await AccessReady(scenario);
        await AccessExecute(scenario);
        // This is a public Resource deployment; the static grant requires an applied generation.
        await using (var eligibility = Context(scenario.Connection))
            Assert.Equal(1, await eligibility.ApplicationDeployments.AsNoTracking().Select(x => x.AppliedGeneration).SingleAsync());
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection,
            "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
                services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(reads)));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var admin = OperatorTestCredentials.Client(channel);
        var selection = DocumentRequest(scenario, exact: true);
        var document = await admin.MaterializeConfigurationAsync(selection);
        Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.Created, document.Status);
        var preview = await admin.PreviewConfigurationAsync(selection);
        Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.Found, preview.Status);
        using (var redacted = JsonDocument.Parse(preview.PreviewJson.Memory))
            Assert.Contains("/Serilog/WriteTo/0/Args/password", redacted.RootElement.GetProperty("redactedPaths"u8)
                .EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(0, reads.Count);
        var grant = await admin.CreateConfigurationAccessAsync(new() { Selector = selection });
        Assert.Equal(Haby.Operator.Contracts.ConfigurationAccessStatus.Created, grant.Status);
        var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        await Denied(runtime, ConfigurationDocumentRevisionId.New().ToString(), grant.Credential);
        Assert.Equal(0, reads.Count);
        ConfigurationDocumentRow saved;
        await using (var persisted = Context(scenario.Connection))
        {
            saved = await persisted.ConfigurationDocuments.AsNoTracking().SingleAsync(x => x.Component == "api");
        }
        var revision = ConfigurationDocumentCodec.Decode(saved.Payload, saved.Fingerprint);
        var tree = Assert.IsType<ConfigurationObject>(revision.Tree);
        var serilog = Assert.IsType<ConfigurationObject>(tree.Members["Serilog"]);
        var writeTo = Assert.IsType<ConfigurationArray>(serilog.Members["WriteTo"]);
        var args = Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(writeTo.Items[0]).Members["Args"]);
        var projected = Assert.IsType<ConfigurationProjectedSecret>(args.Members["password"]);
        Assert.Equal(exact, projected.Reference);
        Assert.Equal(ProfileSecretFormat.String, projected.Format);
        var delivered = await runtime.DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
        using (var json = JsonDocument.Parse(delivered.Json.Memory))
            Assert.Equal("canary-private", json.RootElement.GetProperty("Serilog"u8).GetProperty("WriteTo"u8)[0]
                .GetProperty("Args"u8).GetProperty("password"u8).GetString());
        Assert.Equal(2, reads.Count);
        await host.StopAsync();

        await using (var restarted = Services(scenario.Connection, full: true))
        {
            Assert.Equal(exact, (await restarted.GetRequiredService<IDurableDeploymentPlanner>()
                .FindPlanAsync(scenario.Plan.Id, default))!.Inputs.Configuration!.FrozenSecrets[named]);
            Assert.Equal(exact, (await restarted.GetRequiredService<IDurableDesiredStateCommitter>()
                .FindGenerationAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default))!
                .Inputs.Configuration!.FrozenSecrets[named]);
            using var replacementMaterial = new SecretMaterial("replacement-private"u8.ToArray());
            Assert.Equal(SecretOutcome.Created, (await restarted.GetRequiredService<SecretAdministration>().CreateAsync("pilot",
                "logging-password-v2", Guid.NewGuid(), replacementMaterial, "tester", default,
                SecretPurpose.Configuration)).Outcome);
            var next = JsonNode.Parse(profile.ToJsonString())!;
            next["metadata"]!["revision"] = 2;
            next["spec"]!["staticContributions"]!["server"]!["secretProjections"]![0]!["secretRef"]!["name"] = "logging-password-v2";
            var imported = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
                restarted.GetRequiredService<IConfigurationProfileImportStore>(), TimeProvider.System)
                .ExecuteAsync(Encoding.UTF8.GetBytes(next.ToJsonString()), "tester"));
            var environment = (await restarted.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
            var assignments = restarted.GetRequiredService<IProfileAssignmentStore>();
            var scope = new ProfileAssignmentTarget.Environment();
            var prior = (await assignments.FindAsync(environment, scope, default))!;
            Assert.Equal(AssignmentOutcome.Updated, (await assignments.ApplyAsync(environment,
                new(scope, [new ProfileDecision.Use(imported.Revision.Reference)]),
                new AssignmentPrecondition.ExpectedConcurrencyToken(prior.Token),
                new("tester", DateTimeOffset.UtcNow, null), default)).Outcome);
            using var retained = await restarted.GetRequiredService<ISecretStore>().ResolveBoundedAsync(
                new(new SecretOwner.Environment(scenario.Owner.EnvironmentRecordId), SecretPurpose.Configuration),
                exact!, 64, default);
            Assert.NotNull(retained);
            var retainedBytes = retained.CopyBytes();
            try { Assert.Equal("canary-private", Encoding.UTF8.GetString(retainedBytes)); }
            finally { Array.Clear(retainedBytes); }
        }
        await using (var persisted = Context(scenario.Connection))
        {
            var after = await persisted.ConfigurationDocuments.AsNoTracking().SingleAsync(x => x.Component == "api");
            Assert.Equal(saved.Payload, after.Payload);
            Assert.Equal(saved.Fingerprint, after.Fingerprint);
            Assert.Equal(1, await persisted.ApplicationDeployments.AsNoTracking().Select(x => x.AppliedGeneration).SingleAsync());
        }
        await using var secondHost = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection,
            "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(secondHost);
        using var secondChannel = GrpcChannel.ForAddress(secondHost.Urls.Single());
        var original = await new ConfigurationDelivery.ConfigurationDeliveryClient(secondChannel)
            .DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
        using (var json = JsonDocument.Parse(original.Json.Memory))
            Assert.Equal("canary-private", json.RootElement.GetProperty("Serilog"u8).GetProperty("WriteTo"u8)[0]
                .GetProperty("Args"u8).GetProperty("password"u8).GetString());
        await secondHost.StopAsync();
    }
}
