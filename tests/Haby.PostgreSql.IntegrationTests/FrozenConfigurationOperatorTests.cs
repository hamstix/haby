using System.Data.Common;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task Generated_actual_Host_plans_exact_revision_scopes_refusals_and_native_safe_outcomes_without_secret_reads()
    {
        var setup = await PrepareConfigurationAsync();
        var trap = new ConfigurationSecretReadTrap();
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + setup.Connection,
            "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddSingleton(Haby.Tests.ConfigurationRenderingSamples.Catalog("postgresql.database/v1alpha1", producerAction: Haby.PostgreSql.PostgreSqlDatabaseCreateAction.RegistrationKey))
                .AddDbContextFactory<ManifestCatalogDbContext>(options => options.AddInterceptors(trap)));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = OperatorTestCredentials.Client(channel);
        var env = new Haby.Operator.Contracts.ProfileAssignmentTarget { Kind = "environment" };
        var app = new Haby.Operator.Contracts.ProfileAssignmentTarget { Kind = "application", ApplicationId = "operator-example" };
        var worker = new Haby.Operator.Contracts.ProfileAssignmentTarget { Kind = "component", ApplicationId = "operator-example", ComponentRef = "worker" };
        var source = Node(false);
        source["metadata"]!["revision"] = 2;
        source["spec"]!["bindingRules"]!["connection"]!["documentProjections"]!.AsArray().RemoveAt(1);
        source["spec"]!["staticContributions"]!["logging"]!["value"]!["Level"] = "private-config-value";
        Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(new() { Payload = ByteString.CopyFrom(Bytes(source)) })).Status);
        // Later import has a renamed Component. Assignment applicability still uses the exact requested revision.
        var later = JsonNode.Parse(Envelope)!;
        later["applicationVersion"] = "9";
        later["manifest"]!["spec"]!["components"] = new JsonObject { ["renamed"] = new JsonObject() };
        var laterImported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(Bytes(later)) });
        Assert.NotNull(laterImported.Created);
        // Highest version, most recently imported version, checked assignment revision and
        // the planned revision deliberately differ. CheckedRevision is audit, not applicability.
        var checkedVersion = JsonNode.Parse(Envelope)!;
        checkedVersion["applicationVersion"] = "3";
        checkedVersion["manifest"]!["spec"]!["components"] = new JsonObject { ["worker"] = new JsonObject() };
        Assert.NotNull((await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(Bytes(checkedVersion)) })).Created);
        await Apply(worker, 2, "3");
        var change = (await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = "pilot",
            Precondition = PlanningPrecondition.ExpectedAbsence,
            Reference = new() { ApplicationRecordId = setup.Revision.ApplicationRecordId.ToString(), RevisionNumber = setup.Revision.RevisionNumber.Value }
        })).ChangeSet;
        var created = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, created.Status);
        Assert.DoesNotContain("private-config-value", created.ToString()); Assert.DoesNotContain("template", created.ToString());
        var planner = host.Services.GetRequiredService<IDurableDeploymentPlanner>();
        var saved = (await planner.FindPlanAsync(DeploymentPlanId.Parse(created.Plan.PlanId), default))!;
        var frozen = saved.Inputs.Configuration!;
        Assert.Equal(new[] { "api", "worker" }, frozen.Layouts.Select(x => x.Component.Value));
        Assert.Equal(1, frozen.Layouts.Single(x => x.Component.Value == "api").Documents.Single().Origin.Profile!.Revision);
        Assert.Equal(2, frozen.Layouts.Single(x => x.Component.Value == "worker").Documents.Single().Origin.Profile!.Revision);
        await Native("current", 0, "plan", "check", created.Plan.PlanId);
        await Apply(app, 2);
        Assert.Equal(PlanningStatus.PlanStale, (await client.CheckPlanAsync(new() { PlanId = created.Plan.PlanId })).Status);
        Assert.Equal(saved.Inputs, (await planner.FindPlanAsync(saved.Id, default))!.Inputs);
        Assert.Equal(CommitStatus.PlanStale, (await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSetId, PlanId = created.Plan.PlanId })).Status);
        await Native("planStale", 1, "plan", "check", created.Plan.PlanId);
        var updated = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, updated.Status);
        var secret = source.DeepClone(); secret["metadata"]!["revision"] = 3;
        secret["spec"]!["staticContributions"]!["logging"]!["secretProjections"] = JsonNode.Parse("""
            [{"path":"/Token","format":"string","secretRef":{"name":"unresolved-private-selector","version":77}}]
            """u8);
        Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(new() { Payload = ByteString.CopyFrom(Bytes(secret)) })).Status);
        // Effective Application replacement suppresses the secret-bearing Environment history.
        await Apply(env, 3);
        Assert.Equal(PlanningStatus.Planned, (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSetId })).Status);
        await Apply(app, null);
        var refusal = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSetId });
        Assert.Equal(PlanningStatus.ConfigurationRefused, refusal.Status); Assert.Null(refusal.Plan);
        Assert.Contains("profile.secret-projections.unsupported", refusal.ConfigurationErrorCodes);
        Assert.DoesNotContain("unresolved-private-selector", refusal.ToString()); Assert.DoesNotContain("private-config-value", refusal.ToString());
        await Native("configurationRefused", 1, "plan", "create", change.ChangeSetId);
        // Existing immutable Plans remain readable and carry their accepted layout after current expansion refuses.
        Assert.Equal(PlanningStatus.PlanStale, (await client.GetPlanAsync(new() { PlanId = created.Plan.PlanId })).Status);
        Assert.Equal(frozen, (await planner.FindPlanAsync(saved.Id, default))!.Inputs.Configuration);
        await Apply(app, null, disable: true);
        var renamedChange = (await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = "pilot",
            Precondition = PlanningPrecondition.ExpectedAbsence,
            Reference = laterImported.Created.Revision.Reference
        })).ChangeSet;
        var disabled = await client.CreatePlanAsync(new() { ChangeSetId = renamedChange.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, disabled.Status);
        var renamedSnapshot = (await planner.FindPlanAsync(DeploymentPlanId.Parse(disabled.Plan.PlanId), default))!.Inputs.Configuration!;
        Assert.Empty(Assert.Single(renamedSnapshot.Layouts).Documents);
        Assert.Equal("renamed", Assert.Single(renamedSnapshot.Layouts).Component.Value);
        Assert.DoesNotContain(renamedSnapshot.Scopes, x => x.Target is Haby.Domain.ProfileAssignmentTarget.Component c && c.ComponentRef.Value == "worker");
        await Apply(app, null);
        Assert.Equal(PlanningStatus.ConfigurationRefused, (await client.CreatePlanAsync(new() { ChangeSetId = renamedChange.ChangeSetId })).Status);
        await host.StopAsync();

        async Task Apply(Haby.Operator.Contracts.ProfileAssignmentTarget target, int? revision, string? version = null, bool disable = false)
        {
            var prior = await client.GetProfileAssignmentAsync(new() { Environment = "pilot", Target = target });
            var t = new JsonObject { ["kind"] = target.Kind };
            if (target.ApplicationId.Length != 0) t["applicationId"] = target.ApplicationId;
            if (target.ComponentRef.Length != 0) t["componentRef"] = target.ComponentRef;
            var declaration = new JsonObject
            {
                ["apiVersion"] = "haby.dev/v1alpha1",
                ["kind"] = "ConfigurationProfileAssignment",
                ["spec"] = new JsonObject
                {
                    ["target"] = t,
                    ["decisions"] = disable ? new JsonArray(new JsonObject { ["kind"] = "disable", ["profileId"] = "layout" }) : revision is null ? new JsonArray() :
                    new JsonArray(new JsonObject { ["kind"] = "use", ["profileRef"] = new JsonObject { ["profileId"] = "layout", ["revision"] = revision } })
                }
            };
            var request = new ApplyProfileAssignmentRequest { Environment = "pilot", Payload = ByteString.CopyFrom(Bytes(declaration)) };
            if (prior.Assignment is null) request.ExpectedAbsence = true; else request.ExpectedConcurrencyToken = prior.Assignment.ConcurrencyToken;
            if (version is not null) request.ApplicationVersion = version;
            Assert.Contains((await client.ApplyProfileAssignmentAsync(request)).Status, new[] { ProfileAssignmentStatus.Created, ProfileAssignmentStatus.Updated });
        }
        async Task Native(string outcome, int exit, params string[] args)
        {
            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
            if (binary is null) return;
            var config = Path.Combine(Path.GetTempPath(), "haby-config-planning-" + Guid.NewGuid().ToString("N") + ".yaml");
            await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: pilot\ninstances:\n  local:\n    endpoint: {host.Urls.Single()}\n    token: {OperatorTestCredentials.Credential(host.Urls.Single())}\ncontexts:\n  pilot:\n    instance: local\n    environmentId: pilot\n");
            try
            {
                foreach (var format in new[] { "text", "json" })
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in args.Concat(new[] { "--config", config, "-o", format })) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                    Assert.Equal(exit, process.ExitCode); Assert.Equal("", await stderr);
                    var text = await stdout; Assert.Contains(outcome, text);
                    Assert.DoesNotContain("private-config-value", text); Assert.DoesNotContain("unresolved-private-selector", text);
                }
            }
            finally { File.Delete(config); }
        }
    }

    private sealed class ConfigurationSecretReadTrap : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Assert.DoesNotContain("secret_payloads", command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
