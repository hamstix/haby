using Haby.TestDiagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using ImportStatus = Haby.Operator.Contracts.ResourceImportRequestStatus;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    private sealed record AdditiveImportAuthority(Guid Id, ResourceOrigin Origin, ResourceLifecycleAuthority Authority, long SourceGeneration);
    private sealed record AdditiveImportCredential(Guid Id, Guid Secret, Guid Version, Guid? ConfirmedReceipt);

    [Theory]
    [InlineData("success")]
    [InlineData("partial")]
    [InlineData("correction")]
    [InlineData("omit-group")]
    [InlineData("omit-member")]
    [InlineData("omit-reference")]
    [InlineData("extra-retained")]
    public async Task Public_mixed_baseline_adds_two_protected_Import_groups_atomically_and_preserves_authority(string scenario)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_COMPOSITION_SAMPLE_PATH");
        Assert.True(Path.IsPathFullyQualified(binary!) && File.Exists(binary), "Published nonfriend composition sample required.");
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var before = await Snapshot(pair);
        var catalogName = "additive_public_" + Guid.NewGuid().ToString("N"); databases.Add(catalogName);
        await Sql(Control(), $"CREATE DATABASE \"{catalogName}\"");
        var catalog = new NpgsqlConnectionStringBuilder(Control()) { Database = catalogName }.ConnectionString;
        var root = Path.Combine(Path.GetTempPath(), "haby303-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), """{"Logging":{"LogLevel":{"Default":"None"}},"Sample":{"Value":"transition"}}""");
        try
        {
            string credential;
            await using (var issue = new OperatorHostIntegrationTests.CompositionProcess(binary!, catalog, root,
                ["operator-token-issue", "--token-output-file", Path.Combine(root, "operator.token")]))
                await issue.RequireExitAsync(0);
            credential = (await File.ReadAllTextAsync(Path.Combine(root, "operator.token"))).TrimEnd();
            DurableCommitSummary? committed = null;
            ConfigurationAccessReply? grant = null;
            long retainedApplied = 0;
            for (var startup = 0; startup < 2; startup++)
            {
                var port = OperatorHostCompositionTests.AvailablePort();
                await using var child = new OperatorHostIntegrationTests.CompositionProcess(binary!, catalog, root,
                    ["--contentRoot=" + root, "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"], retainDiagnostics: true);
                await child.ExpectAsync("composition-built"); await child.SendAsync("start"); await child.ExpectAsync("composition-started");
                using var channel = GrpcChannel.ForAddress("http://127.0.0.1:" + port);
                var client = OperatorTestCredentials.Client(channel, credential);
                if (startup == 0)
                {
                    var checkedPlans = 0;
                    var environment = await client.CreateEnvironmentAsync(new() { Name = "pilot" });
                    Assert.Equal(EnvironmentStatus.Created, environment.Status);
                    var control = new NpgsqlConnectionStringBuilder(Control());
                    await Secret("administrator", "ProviderAdministrator", control.Password!); control.Password = null;
                    await Secret("shared", "SharedApplicationCredential", "synthetic-shared-material");
                    var provider = JsonNode.Parse("""
                        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"postgresql","environment":"pilot"},"spec":{
                         "revision":1,"providerKind":"postgresql/v1","configurationContract":"haby.postgresql.connection/v1","implementationId":"haby.postgresql.database-checkpoint/v1",
                         "properties":{"host":"127.0.0.1","port":5432,"maintenanceDatabase":"postgres","administratorUserName":"postgres","transportMode":"disable"},
                         "secretRefs":{"administrator-password":{"secretName":"administrator","version":1}}}}
                        """u8)!;
                    provider["spec"]!["properties"]!["port"] = control.Port;
                    provider["spec"]!["implementationId"] = PostgreSqlDatabaseCheckpointCapability.ImplementationId;
                    Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(provider.ToJsonString()) })).Status);
                    Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "postgresql.primary", ExpectedAbsent = true, Target = new() { Name = "postgresql", Revision = 1 } })).Status);
                    var envelope = Declaration("transition-consumer");
                    var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(envelope.ToJsonString()) });
                    Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase);
                    var request = new CreateResourceImportChangeSetRequest
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        EnvironmentRecordId = environment.Environment.RecordId,
                        Reference = imported.Created.Revision.Reference,
                        InputFormat = PostgreSqlImportPreparer.NamesFormat,
                        InputJson = ByteString.CopyFromUtf8(PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema)).Json),
                        CredentialMaterial = ByteString.CopyFromUtf8(pair.Password)
                    };
                    var accepted = await client.CreateResourceImportChangeSetAsync(request); request.CredentialMaterial = ByteString.Empty;
                    Assert.Equal(ImportStatus.Created, accepted.Status);
                    var baseline = await Confirm(accepted.Request.ChangeSetId);
                    Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = baseline.DeploymentId, Generation = baseline.Generation })).Status);
                    AdditiveImportAuthority[] authority;
                    AdditiveImportCredential[] originalCredentials;
                    await using (var baselineRead = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                    {
                        authority = await baselineRead.ResourceManagement.AsNoTracking().OrderBy(x => x.Resource).Select(x => new AdditiveImportAuthority(x.Id, x.Origin, x.Authority, x.SourceGeneration)).ToArrayAsync();
                        originalCredentials = await baselineRead.ProtectedApplicationCredentials.AsNoTracking().Select(x => new AdditiveImportCredential(x.Id, x.Secret, x.Version, x.ConfirmedReceipt)).ToArrayAsync();
                    }
                    Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8("""
                        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"sample","environment":"pilot"},"spec":{
                         "revision":1,"providerKind":"sample.service/v1","configurationContract":"sample.service.configuration/v1","implementationId":"sample.service.impl/v1",
                         "properties":{"Endpoint":"https://service.example.invalid/"},"secretRefs":{"system":{"secretName":"shared","version":1},"application":{"secretName":"shared","version":1}}}}
                        """) })).Status);
                    await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "sample.primary", ExpectedAbsent = true, Target = new() { Name = "sample", Revision = 1 } });
                    envelope["applicationVersion"] = "v2";
                    envelope["manifest"]!["spec"]!["resources"]!["service"] = JsonNode.Parse("""{"type":"sample.reference/v1","providerRef":"sample.primary","properties":{}}"""u8);
                    envelope["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]!["service"] = JsonNode.Parse("""
                        {"target":{"kind":"local-resource","resourceRef":"service"},"options":{"label":"combined"},"documentProjections":[{"documentRef":"settings","path":"/Service","rendererRef":"sample.access-json/v1"}]}
                        """u8);
                    var next = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(envelope.ToJsonString()) });
                    Assert.Equal(ImportReply.OutcomeOneofCase.Created, next.OutcomeCase);
                    var changeRequest = new CreateChangeSetRequest
                    {
                        EnvironmentId = "pilot",
                        Reference = next.Created.Revision.Reference,
                        Precondition = PlanningPrecondition.ExpectedGeneration,
                        ExpectedGeneration = baseline.Generation,
                        Acquisitions = new()
                    };
                    changeRequest.Acquisitions.Resources.Add(new ResourceAcquisitionMember
                    {
                        Resource = "service",
                        Mode = AcquisitionMode.Reference,
                        Input = new() { Format = "sample.reference-input/v1", Json = "{\"Identity\":\"sample-service\"}" }
                    });
                    await OperatorHostIntegrationTests.AuthDenied(async () => await new ManifestOperator.ManifestOperatorClient(channel).CreateChangeSetAsync(changeRequest));
                    var change = await client.CreateChangeSetAsync(changeRequest); Assert.Equal(PlanningStatus.Created, change.Status);
                    committed = await Confirm(change.ChangeSet.ChangeSetId);
                    Assert.NotEqual(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                    Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed.DeploymentId, Generation = committed.Generation })).Status);
                    Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                    var retainedGeneration = committed.Generation; retainedApplied = retainedGeneration;
                    var replayScenario = scenario is "omit-group" or "omit-member" or "omit-reference" or "extra-retained";
                    envelope["applicationVersion"] = "v3";
                    if (replayScenario)
                    {
                        Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8("""
                            {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"sample-next","environment":"pilot"},"spec":{
                             "revision":1,"providerKind":"sample.service/v1","configurationContract":"sample.service.configuration/v1","implementationId":"sample.service.impl/v1",
                             "properties":{"Endpoint":"https://service.example.invalid/"},"secretRefs":{"system":{"secretName":"shared","version":1},"application":{"secretName":"shared","version":1}}}}
                            """) })).Status);
                        await client.SetProviderAliasAsync(new() { Environment = "pilot", Alias = "sample.next", ExpectedAbsent = true, Target = new() { Name = "sample-next", Revision = 1 } });
                        envelope["manifest"]!["spec"]!["resources"]!["new-service"] = JsonNode.Parse("""{"type":"sample.reference/v1","providerRef":"sample.next","properties":{}}"""u8);
                    }
                    foreach (var resource in new[] { "archive", "audit" })
                    {
                        envelope["manifest"]!["spec"]!["resources"]![resource] = JsonNode.Parse("""{"type":"sample.import/v1","providerRef":"sample.primary","properties":{}}"""u8);
                        envelope["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]![resource] = JsonNode.Parse($$"""
                            {"target":{"kind":"local-resource","resourceRef":"{{resource}}"},"options":{"label":"{{resource}}"},"documentProjections":[{"documentRef":"settings","path":"/{{resource}}","rendererRef":"sample.import-json/v1"}]}
                            """);
                    }
                    foreach (var resource in new[] { "archive-proof", "audit-proof" })
                        envelope["manifest"]!["spec"]!["resources"]![resource] = JsonNode.Parse("""{"type":"sample.import/v1","providerRef":"sample.primary","properties":{}}"""u8);
                    var third = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(envelope.ToJsonString()) });
                    Assert.Equal(ImportReply.OutcomeOneofCase.Created, third.OutcomeCase);
                    async Task<string> Stage(string resource, string material, string? previous = null)
                    {
                        var stageRequest = new CreateResourceImportChangeSetRequest
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            EnvironmentRecordId = environment.Environment.RecordId,
                            Reference = third.Created.Revision.Reference,
                            InputFormat = "sample.import-names/v1",
                            CredentialSlot = "application-password",
                            InputJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { Resource = resource, Identity = "sample-service", Companion = resource + "-proof" })),
                            CredentialMaterial = ByteString.CopyFromUtf8(material)
                        };
                        if (previous is not null) stageRequest.Correction = new() { PreviousRequestId = previous, ExpectedAbsence = true, Reason = ResourceImportCorrectionReason.InputCorrection };
                        var staged = await client.StageResourceImportAsync(stageRequest);
                        Assert.Equal(ImportStatus.Pending, staged.Status);
                        Assert.Equal(ImportStatus.Pending, (await client.StageResourceImportAsync(stageRequest)).Status);
                        var changedMaterial = stageRequest.Clone(); changedMaterial.CredentialMaterial = ByteString.CopyFromUtf8("different-material");
                        Assert.Equal(ImportStatus.Conflict, (await client.StageResourceImportAsync(changedMaterial)).Status);
                        var wrongOwner = stageRequest.Clone(); wrongOwner.EnvironmentRecordId = Haby.Domain.EnvironmentRecordId.New().ToString();
                        Assert.Equal(ImportStatus.Conflict, (await client.StageResourceImportAsync(wrongOwner)).Status);
                        var wrongSlot = stageRequest.Clone(); wrongSlot.CredentialSlot = "provider-administrator";
                        Assert.Equal(ImportStatus.ImportNotSupported, (await client.StageResourceImportAsync(wrongSlot)).Status);
                        await OperatorHostIntegrationTests.AuthDenied(async () => await new ManifestOperator.ManifestOperatorClient(channel).StageResourceImportAsync(stageRequest));
                        return stageRequest.RequestId;
                    }
                    var originals = new[] { await Stage("archive", "synthetic-import-material"),
                        await Stage("audit", scenario == "partial" ? "rejected-material" : "synthetic-import-material") };
                    CreateChangeSetRequest Request(string[] ids)
                    {
                        var request = new CreateChangeSetRequest
                        {
                            EnvironmentId = "pilot",
                            Reference = third.Created.Revision.Reference,
                            Precondition = PlanningPrecondition.ExpectedGeneration,
                            ExpectedGeneration = retainedGeneration,
                            Acquisitions = new()
                        };
                        for (var index = 0; index < ids.Length; index++)
                            foreach (var resource in index == 0 ? new[] { "archive", "archive-proof" } : new[] { "audit", "audit-proof" })
                                request.Acquisitions.Resources.Add(new ResourceAcquisitionMember { Resource = resource, Mode = AcquisitionMode.ImportUseAndRetain, ImportRequestId = ids[index] });
                        if (replayScenario)
                            request.Acquisitions.Resources.Add(new ResourceAcquisitionMember { Resource = "new-service", Mode = AcquisitionMode.Reference,
                                Input = new() { Format = "sample.reference-input/v1", Json = "{\"Identity\":\"sample-service\"}" } });
                        return request;
                    }
                    int changesBefore;
                    await using (var beforeInvalid = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                        changesBefore = await beforeInvalid.DurableChangeSets.CountAsync();
                    Assert.Equal(PlanningStatus.ImportNotSupported, (await client.CreateChangeSetAsync(Request([originals[0], Guid.NewGuid().ToString()]))).Status);
                    await using (var afterInvalid = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                    {
                        Assert.Equal(changesBefore, await afterInvalid.DurableChangeSets.CountAsync());
                        Assert.Equal(2, await afterInvalid.ProtectedCredentialOwners.CountAsync(x => x.Resource == "archive" || x.Resource == "audit"));
                        Assert.Equal(retainedGeneration, (await afterInvalid.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
                        Assert.Empty(await afterInvalid.ImportSupersessions.ToArrayAsync());
                    }
                    var invalid = Request([originals[1], originals[0]]);
                    Assert.Equal(PlanningStatus.ImportNotSupported, (await client.CreateChangeSetAsync(invalid)).Status);
                    var newChange = await client.CreateChangeSetAsync(Request(originals));
                    Assert.Equal(PlanningStatus.Created, newChange.Status);
                    Assert.Equal(newChange.ChangeSet.ChangeSetId, (await client.CreateChangeSetAsync(Request(originals))).ChangeSet.ChangeSetId);
                    if (replayScenario)
                    {
                        var altered = Request(originals);
                        foreach (var member in altered.Acquisitions.Resources.ToArray())
                            if (scenario == "omit-group" && member.Resource is "audit" or "audit-proof" ||
                                scenario == "omit-member" && member.Resource == "audit-proof" ||
                                scenario == "omit-reference" && member.Resource == "new-service")
                                altered.Acquisitions.Resources.Remove(member);
                        if (scenario == "extra-retained") altered.Acquisitions.Resources.Add(changeRequest.Acquisitions.Resources.Single().Clone());
                        Assert.NotEqual(PlanningStatus.Created, (await client.CreateChangeSetAsync(altered)).Status);
                        Assert.Equal(newChange.ChangeSet.ChangeSetId, (await client.CreateChangeSetAsync(Request(originals))).ChangeSet.ChangeSetId);
                        await child.SendAsync("stop"); await child.ExpectAsync("composition-stopped"); await child.RequireExitAsync(0);
                        break;
                    }
                    if (scenario == "correction")
                    {
                        var candidates = new[] { new[] { await Stage("archive", "synthetic-import-material", originals[0]), await Stage("audit", "synthetic-import-material", originals[1]) },
                            new[] { await Stage("archive", "synthetic-import-material", originals[0]), await Stage("audit", "synthetic-import-material", originals[1]) } };
                        var plans = new List<(string Change, string Plan)>();
                        foreach (var candidate in candidates)
                        {
                            var correction = await client.CreateChangeSetAsync(Request(candidate)); Assert.Equal(PlanningStatus.Created, correction.Status);
                            var planned = await client.CreatePlanAsync(new() { ChangeSetId = correction.ChangeSet.ChangeSetId }); Assert.Equal(PlanningStatus.Planned, planned.Status);
                            Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = planned.Plan.PlanId })).Status);
                            plans.Add((correction.ChangeSet.ChangeSetId, planned.Plan.PlanId));
                        }
                        await using (var beforeAcceptance = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                        {
                            Assert.Equal(originals.OrderBy(x => x), (await beforeAcceptance.ProtectedCredentialOwners.AsNoTracking().Where(x => x.Resource == "archive" || x.Resource == "audit").Select(x => x.Request.ToString()).ToArrayAsync()).OrderBy(x => x));
                            Assert.Empty(await beforeAcceptance.ImportSupersessions.ToArrayAsync());
                        }
                        var competing = await Task.WhenAll(plans.Select(async p => await client.CommitDesiredStateAsync(new() { ChangeSetId = p.Change, PlanId = p.Plan })));
                        Assert.Single(competing, x => x.Status == CommitStatus.Committed);
                        Assert.Single(competing, x => x.Status != CommitStatus.Committed);
                        committed = competing.Single(x => x.Status == CommitStatus.Committed).Commit;
                        foreach (var original in originals) Assert.Equal(ImportStatus.Superseded, (await client.GetResourceImportRequestAsync(new() { RequestId = original })).Status);
                        await using var acceptedCorrection = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                        var audits = await acceptedCorrection.ImportSupersessions.AsNoTracking().ToArrayAsync();
                        Assert.Equal(2, audits.Length);
                        Assert.Equal(originals.OrderBy(x => x), audits.Select(x => x.PreviousRequest.ToString()).OrderBy(x => x));
                        Assert.All(audits, x => Assert.Equal(committed.Generation, x.Generation));
                        foreach (var audit in audits) Assert.True(await acceptedCorrection.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x => x.Request == audit.SuccessorRequest && x.Stage == audit.SuccessorStage));
                    }
                    else
                    {
                        committed = await Confirm(newChange.ChangeSet.ChangeSetId);
                        Assert.Equal(newChange.ChangeSet.ChangeSetId, (await client.CreateChangeSetAsync(Request(originals))).ChangeSet.ChangeSetId);
                    }
                    Assert.NotEqual(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                    var outcome = await client.ExecuteGenerationAsync(new() { DeploymentId = committed.DeploymentId, Generation = committed.Generation });
                    string progress;
                    await using (var readProgress = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                        progress = JsonSerializer.Serialize(await readProgress.DurableOperations.AsNoTracking().Where(x => x.Generation == committed.Generation)
                            .Select(x => new { x.OperationId, x.State, x.ReasonCode, x.AttemptNumber }).ToArrayAsync());
                    if (scenario == "partial")
                    {
                        Assert.NotEqual(GenerationExecutionStatus.Succeeded, outcome.Status);
                        Assert.NotEqual(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                        await using var partial = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                        Assert.Equal(retainedGeneration, (await partial.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                        Assert.True(2 == await partial.ProtectedApplicationCredentials.AsNoTracking().CountAsync(x => x.ConfirmedReceipt != null), progress);
                        Assert.Equal(2, await partial.OperationOutputs.AsNoTracking().CountAsync(x => x.Generation == committed.Generation));
                        await child.SendAsync("stop"); await child.ExpectAsync("composition-stopped"); await child.RequireExitAsync(0);
                        continue;
                    }
                    Assert.True(outcome.Status == GenerationExecutionStatus.Succeeded, outcome.Status + " " + progress);
                    Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                    grant = await client.CreateConfigurationAccessAsync(new() { Selector = new() { Environment = "pilot", ApplicationId = "transition-consumer", ComponentId = "worker", DocumentId = "settings" } });
                    Assert.Equal(ConfigurationAccessStatus.Created, grant.Status);
                    await using var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                    Assert.Equal(authority, await verify.ResourceManagement.AsNoTracking().Where(x => x.Resource != "service" && x.Resource != "archive" && x.Resource != "audit" && x.Resource != "archive-proof" && x.Resource != "audit-proof").OrderBy(x => x.Resource).Select(x => new AdditiveImportAuthority(x.Id, x.Origin, x.Authority, x.SourceGeneration)).ToArrayAsync());
                    Assert.Equal(originalCredentials, await verify.ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Resource != "archive" && x.Resource != "audit").Select(x => new AdditiveImportCredential(x.Id, x.Secret, x.Version, x.ConfirmedReceipt)).ToArrayAsync());
                    async Task Secret(string name, string purpose, string material) => Assert.Equal(SecretStatus.Created, (await client.CreateSecretAsync(new() { Environment = "pilot", Name = name, Purpose = purpose, RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8(material) })).Status);
                    async Task<DurableCommitSummary> Confirm(string change)
                    {
                        var plan = await client.CreatePlanAsync(new() { ChangeSetId = change }); Assert.Equal(PlanningStatus.Planned, plan.Status);
                        var stage = ++checkedPlans == 1 ? CompositionDiagnosticStage.InitialProtectedImportBaselineCheckPlan : CompositionDiagnosticStage.AdditiveCheckPlan;
                        Assert.Equal(PlanningStatus.Current, (await child.AtDiagnosticStageAsync(stage,
                            async () => await client.CheckPlanAsync(new() { PlanId = plan.Plan.PlanId }))).Status);
                        var commit = await client.CommitDesiredStateAsync(new() { ChangeSetId = change, PlanId = plan.Plan.PlanId }); Assert.Equal(CommitStatus.Committed, commit.Status); return commit.Commit;
                    }
                }
                if (scenario == "partial")
                {
                    string completedAttempts;
                    await using (var beforeRetry = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                        completedAttempts = JsonSerializer.Serialize(await beforeRetry.DurableOperations.AsNoTracking().Where(x => x.Generation == committed!.Generation && x.OperationId.StartsWith("archive"))
                            .OrderBy(x => x.OperationId).Select(x => new { x.OperationId, x.CurrentAttemptId, x.AttemptNumber }).ToArrayAsync());
                    Assert.NotEqual(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed!.DeploymentId, Generation = committed.Generation })).Status);
                    Assert.NotEqual(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = "transition-consumer" })).Status);
                    await using var afterRetry = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                    Assert.Equal(completedAttempts, JsonSerializer.Serialize(await afterRetry.DurableOperations.AsNoTracking().Where(x => x.Generation == committed.Generation && x.OperationId.StartsWith("archive"))
                        .OrderBy(x => x.OperationId).Select(x => new { x.OperationId, x.CurrentAttemptId, x.AttemptNumber }).ToArrayAsync()));
                    Assert.Equal(2, await afterRetry.OperationOutputs.AsNoTracking().CountAsync(x => x.Generation == committed.Generation));
                    Assert.Equal(2, await afterRetry.ProtectedApplicationCredentials.AsNoTracking().CountAsync(x => x.ConfirmedReceipt != null));
                    Assert.Equal(retainedApplied, (await afterRetry.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                    await child.SendAsync("stop"); await child.ExpectAsync("composition-stopped"); await child.RequireExitAsync(0);
                    continue;
                }
                Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed!.DeploymentId, Generation = committed.Generation })).Status);
                var delivery = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
                var delivered = await delivery.DeliverAsync(new() { RevisionId = grant!.Grant.Document.RevisionId }, new Metadata { { "authorization", "Bearer " + grant.Credential } });
                using var json = JsonDocument.Parse(delivered.Json.Memory);
                var connection = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("Connection"u8).GetString());
                Assert.True(connection.Password == pair.Password, "Retained credential changed."); connection.Password = null;
                Assert.Equal("synthetic-shared-material", json.RootElement.GetProperty("Service"u8).GetProperty("credential"u8).GetString());
                Assert.Equal("synthetic-import-material", json.RootElement.GetProperty("archive"u8).GetProperty("credential"u8).GetString());
                Assert.Equal("synthetic-import-material", json.RootElement.GetProperty("audit"u8).GetProperty("credential"u8).GetString());
                await child.SendAsync("stop"); await child.ExpectAsync("composition-stopped"); await child.RequireExitAsync(0);
                if (startup == 1 && scenario == "success")
                {
                    int beforeRows;
                    await using (var beforeMigration = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                        beforeRows = await beforeMigration.OperatorImportRequestRecords.AsNoTracking().CountAsync();
                    await using (var migration = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options))
                    {
                        var failure = await Assert.ThrowsAsync<PostgresException>(() => migration.GetService<IMigrator>().MigrateAsync("20261123233005_SharedReferenceConfigurationDelivery"));
                        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
                    }
                    await using var afterMigration = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(catalog).Options);
                    Assert.Equal(beforeRows, await afterMigration.OperatorImportRequestRecords.AsNoTracking().CountAsync());
                    Assert.Contains("20261126050307_ComposableProtectedImportRequests", await afterMigration.Database.GetAppliedMigrationsAsync());
                }
            }
            Assert.True(before == await Snapshot(pair), "Transition mutated existing PostgreSQL state.");
            Assert.True(await PasswordUnchanged(passwordGuard, pair), "Transition changed existing password verifier.");
        }
        finally { Directory.Delete(root, true); }
    }
}
