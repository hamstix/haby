using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using ConfigurationApplicationStatus = Haby.Operator.Contracts.ConfigurationApplicationStatus;
using Haby.Configuration.Contracts;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Published_Reference_adapter_uses_stock_authenticated_intake_confirmation_delivery_and_restart()
    {
        var diagnostic = new PublicReferenceFailureDiagnostics();
        await diagnostic.RunAsync(async cleanup =>
        {
            diagnostic.At(PublicReferenceCheckpoint.PublishedSample);
            var binary = Environment.GetEnvironmentVariable("HABY_COMPOSITION_SAMPLE_PATH");
            Assert.True(Path.IsPathFullyQualified(binary!) && File.Exists(binary), "The separately published nonfriend executable is required.");
            diagnostic.At(PublicReferenceCheckpoint.Catalog);
            var connection = await NewDatabaseAsync();
            var root = Path.Combine(Path.GetTempPath(), "haby275e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            cleanup(() => { Directory.Delete(root, true); return ValueTask.CompletedTask; });
            await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), """{"Logging":{"LogLevel":{"Default":"None"}}}""");
            string? credential = null;
            RevisionReference? revision = null;
            DurableCommitSummary? committed = null;
            ConfigurationAccessReply? grant = null;
            string? provider = null;
            for (var startup = 0; startup < 2; startup++)
            {
                diagnostic.Startup(startup);
                await diagnostic.RunAsync(async release =>
                {
                    diagnostic.At(PublicReferenceCheckpoint.Build);
                    var port = OperatorHostCompositionTests.AvailablePort();
                    var child = new CompositionProcess(binary!, connection, root,
                        ["--contentRoot=" + root, "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"]);
                    release(async () =>
                    {
                        try { await child.DisposeAsync(); }
                        finally { diagnostic.ChildExit(startup == 0 ? PublicReferenceChild.First : PublicReferenceChild.Restart, child.DiagnosticExitCode); }
                    });
                    await child.ExpectAsync("composition-built");
                    if (startup == 0)
                    {
                        diagnostic.At(PublicReferenceCheckpoint.Issuance);
                        await diagnostic.RunAsync(async issuedCleanup =>
                        {
                            var tokenPath = Path.Combine(root, "operator.token");
                            var issuance = new CompositionProcess(binary!, connection, root,
                            ["operator-token-issue", "--token-output-file", tokenPath]);
                            issuedCleanup(async () =>
                        {
                            try { await issuance.DisposeAsync(); }
                            finally { diagnostic.ChildExit(PublicReferenceChild.Issuance, issuance.DiagnosticExitCode); }
                        });
                            await issuance.RequireExitAsync(0);
                            credential = (await File.ReadAllTextAsync(tokenPath)).TrimEnd();
                        });
                    }
                    diagnostic.At(PublicReferenceCheckpoint.Start);
                    await child.SendAsync("start");
                    await child.ExpectAsync("composition-started");
                    using var channel = GrpcChannel.ForAddress("http://127.0.0.1:" + port);
                    var client = OperatorTestCredentials.Client(channel, credential!);
                    diagnostic.At(PublicReferenceCheckpoint.Authentication);
                    var anonymous = new ManifestOperator.ManifestOperatorClient(channel);
                    await AuthDenied(async () => await anonymous.CreateChangeSetAsync(new()));
                    var deliveryOnly = OperatorTestCredentials.Client(channel, ConfigurationDeliveryCredential.Create(Guid.CreateVersion7()));
                    await AuthDenied(async () => await deliveryOnly.CreateChangeSetAsync(new()));
                    if (startup == 0)
                    {
                        diagnostic.At(PublicReferenceCheckpoint.Setup);
                        Assert.Equal(EnvironmentStatus.Created, (await client.CreateEnvironmentAsync(new() { Name = "pilot" })).Status);
                        Assert.Equal(SecretStatus.Created, (await client.CreateSecretAsync(new()
                        {
                            Environment = "pilot",
                            Name = "shared",
                            Purpose = "SharedApplicationCredential",
                            RequestId = Guid.NewGuid().ToString(),
                            Material = ByteString.CopyFromUtf8("sample-shared-test-material")
                        })).Status);
                        var applied = await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(ReferenceProvider) });
                        Assert.Equal(ProviderStatus.Created, applied.Status);
                        provider = applied.Provider.InstanceId;
                        Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(new()
                        {
                            Environment = "pilot",
                            Alias = "sample.primary",
                            ExpectedAbsent = true,
                            Target = new() { Name = "sample", Revision = 1 }
                        })).Status);
                        Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(new() { Payload = ByteString.CopyFromUtf8(ReferenceProfile) })).Status);
                        Assert.Equal(ProfileAssignmentStatus.Created, (await client.ApplyProfileAssignmentAsync(new()
                        { Environment = "pilot", ExpectedAbsence = true, Payload = ByteString.CopyFromUtf8(ReferenceAssignment) })).Status);
                        revision = await ImportReference(client, "reference-consumer");
                        diagnostic.At(PublicReferenceCheckpoint.InvalidIntake);
                        await InvalidReferenceRequests(client, connection, revision);
                        diagnostic.At(PublicReferenceCheckpoint.ConsumerConfirmation);
                        committed = await ConfirmReference(client, revision);
                        Assert.Equal(ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new()
                        { Environment = "pilot", ApplicationId = "reference-consumer" })).Status);
                        grant = await client.CreateConfigurationAccessAsync(new()
                        {
                            Selector = new()
                            { Environment = "pilot", ApplicationId = "reference-consumer", ComponentId = "api", DocumentId = "settings" }
                        });
                        Assert.Equal(ConfigurationAccessStatus.Created, grant.Status);
                        diagnostic.At(PublicReferenceCheckpoint.PeerConfirmation);
                        var peer = await ImportReference(client, "reference-peer");
                        await ConfirmReference(client, peer);
                        Assert.Equal(ConfigurationApplicationStatus.Succeeded, (await client.MaterializeApplicationConfigurationAsync(new()
                        { Environment = "pilot", ApplicationId = "reference-peer" })).Status);
                    }
                    diagnostic.At(PublicReferenceCheckpoint.Reuse);
                    Assert.NotNull(committed);
                    var before = await ReferenceSnapshot(connection);
                    Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new()
                    { DeploymentId = committed.DeploymentId, Generation = committed.Generation })).Status);
                    Assert.Equal(before, await ReferenceSnapshot(connection));
                    diagnostic.At(PublicReferenceCheckpoint.Delivery);
                    var delivery = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
                    var document = new DeliverConfigurationRequest { RevisionId = grant!.Grant.Document.RevisionId };
                    var headers = new Metadata { { "authorization", "Bearer " + grant.Credential } };
                    Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () =>
                        await delivery.DeliverAsync(document, OperatorHeaders(credential!)))).StatusCode);
                    var delivered = await delivery.DeliverAsync(document, headers);
                    using (var json = JsonDocument.Parse(delivered.Json.Memory))
                    {
                        Assert.Equal("sample-shared-test-material", json.RootElement.GetProperty("Access"u8).GetProperty("credential"u8).GetString());
                        Assert.Equal("api", json.RootElement.GetProperty("Access"u8).GetProperty("label"u8).GetString());
                    }
                    diagnostic.At(PublicReferenceCheckpoint.Bindings);
                    var shared = await client.GetSharedCredentialBindingsAsync(new() { Environment = "pilot", ProviderInstanceId = provider!, Revision = 1 });
                    Assert.Equal(2, shared.Bindings.Count);
                    var use = shared.Bindings.Single(x => x.Use == "ApplicationDelivery");
                    Assert.Equal(4, use.Dependencies.Count);
                    Assert.DoesNotContain("sample-shared-test-material", shared.ToString());
                    if (startup == 1)
                    {
                        diagnostic.At(PublicReferenceCheckpoint.ClosedUpdate);
                        var closed = ReferenceChange(revision!);
                        closed.Precondition = PlanningPrecondition.ExpectedGeneration;
                        closed.ExpectedGeneration = committed.Generation;
                        Assert.Equal(PlanningStatus.ReferenceNotSupported, (await client.CreateChangeSetAsync(closed)).Status);
                        diagnostic.At(PublicReferenceCheckpoint.Preparation);
                        await ReferenceWorkloadPreparation(client, connection, committed, diagnostic);
                        diagnostic.At(PublicReferenceCheckpoint.Revocation);
                        Assert.Equal("Revoked", (await client.RevokeSharedCredentialUseAsync(new()
                        {
                            Environment = "pilot",
                            ProviderInstanceId = provider!,
                            Revision = 1,
                            Slot = use.Slot,
                            Use = use.Use,
                            SecretId = use.SecretId,
                            SecretVersionId = use.SecretVersionId
                        })).Status);
                        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await delivery.DeliverAsync(document, headers))).StatusCode);
                    }
                    diagnostic.At(PublicReferenceCheckpoint.Stop);
                    await child.SendAsync("stop");
                    await child.ExpectAsync("composition-stopped");
                    await child.RequireExitAsync(0);
                    await OperatorHostCompositionTests.RequireNotListeningAsync(port);
                });
            }
        });
    }

    private static readonly string ReferenceProvider = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"sample","environment":"pilot"},"spec":{
        "revision":1,"providerKind":"sample.service/v1","configurationContract":"sample.service.configuration/v1","implementationId":"sample.service.impl/v1",
        "properties":{"Endpoint":"https://service.example.invalid/"},"secretRefs":{"system":{"secretName":"shared","version":1},"application":{"secretName":"shared","version":1}}}}
        """;
    private const string ReferenceProfile = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"sample-layout","revision":1},"spec":{
        "documents":{"settings":{"fileName":"settings.json","format":"json","template":{"Mode":"frozen"}}},
        "bindingRules":{"service":{"slot":"service","rendererRef":"sample.access-json/v1","options":{"label":"profile"},"documentProjections":[{"documentRef":"settings","path":"/Access"}]}}}}
        """;
    private const string ReferenceAssignment = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfileAssignment","spec":{"target":{"kind":"environment"},
        "decisions":[{"kind":"use","profileRef":{"profileId":"sample-layout","revision":1}}]}}
        """;
    private static JsonNode ReferenceEnvelope(string application) => JsonNode.Parse("""
        {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"reference-consumer"},"spec":{
        "resources":{"left":{"type":"sample.reference/v1","providerRef":"sample.primary","properties":{}},"right":{"type":"sample.reference/v1","providerRef":"sample.primary","properties":{}}},
        "components":{"api":{"bindings":{"service":{"target":{"kind":"local-resource","resourceRef":"left"},"configurationSlot":"service","options":{"label":"api"}}}},
        "worker":{"bindings":{"service":{"target":{"kind":"local-resource","resourceRef":"right"},"configurationSlot":"service","options":{"label":"worker"}}}}}}}}
        """.Replace("reference-consumer", application, StringComparison.Ordinal))!;
    private static async Task<RevisionReference> ImportReference(ManifestOperator.ManifestOperatorClient client, string application)
    {
        var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(ReferenceEnvelope(application).ToJsonString()) });
        Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase);
        return imported.Created.Revision.Reference;
    }
    private static CreateChangeSetRequest ReferenceChange(RevisionReference revision)
    {
        var request = new CreateChangeSetRequest { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedAbsence, Acquisitions = new() };
        foreach (var resource in new[] { "left", "right" }) request.Acquisitions.Resources.Add(new ResourceAcquisitionMember()
        {
            Resource = resource,
            Mode = AcquisitionMode.Reference,
            Input = new() { Format = "sample.reference-input/v1", Json = "{\"Identity\":\"sample-service\"}" },
            SharingRequest = new() { Format = "sample.sharing/v1", Json = "{\"Identity\":\"sample-service\"}" }
        });
        return request;
    }
    private static async Task<DurableCommitSummary> ConfirmReference(ManifestOperator.ManifestOperatorClient client, RevisionReference revision)
    {
        var change = await client.CreateChangeSetAsync(ReferenceChange(revision));
        Assert.Equal(PlanningStatus.Created, change.Status);
        var plan = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, plan.Status);
        Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = plan.Plan.PlanId })).Status);
        var commit = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId, PlanId = plan.Plan.PlanId });
        Assert.Equal(CommitStatus.Committed, commit.Status);
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new()
        { DeploymentId = commit.Commit.DeploymentId, Generation = commit.Commit.Generation })).Status);
        return commit.Commit;
    }
    private static async Task InvalidReferenceRequests(ManifestOperator.ManifestOperatorClient client, string connection, RevisionReference revision)
    {
        var before = await ReferenceSnapshot(connection);
        foreach (var fault in new[] { "missing-mode", "unknown-mode", "import", "unbound-import", "missing-resource", "duplicate", "empty", "schema", "format", "member-bound", "encoded-aggregate-bound", "aggregate-bound" })
        {
            var request = ReferenceChange(revision);
            var member = request.Acquisitions.Resources[0];
            switch (fault)
            {
                case "missing-mode": member.Mode = AcquisitionMode.Unspecified; break;
                case "unknown-mode": member.Mode = (AcquisitionMode)99; break;
                case "import": member.Mode = AcquisitionMode.ImportUseAndRetain; break;
                case "unbound-import":
                    member.Mode = AcquisitionMode.ImportUseAndRetain; member.Input = null; member.SharingRequest = null;
                    member.ImportRequestId = Guid.NewGuid().ToString(); break;
                case "missing-resource": request.Acquisitions.Resources.RemoveAt(0); break;
                case "duplicate": request.Acquisitions.Resources.Add(member.Clone()); break;
                case "empty": request.Acquisitions.Resources.Clear(); break;
                case "schema": member.Input.Json = "{\"Identity\":\"wrong\"}"; break;
                case "format": member.Input.Format = "unsupported/v1"; break;
                case "member-bound": member.Input.Json = new string('x', 65537); break;
                case "encoded-aggregate-bound":
                    foreach (var item in request.Acquisitions.Resources)
                    { item.Input.Json = new string('\u20ac', 65536); item.SharingRequest.Json = new string('\u0001', 65536); }
                    break;
                case "aggregate-bound":
                    request.Acquisitions.Resources.Clear();
                    for (var i = 0; i < 24; i++) { var item = member.Clone(); item.Resource = "resource-" + i; item.Input.Json = new string('x', 60000); request.Acquisitions.Resources.Add(item); }
                    break;
            }
            if (fault == "aggregate-bound")
                Assert.Equal(StatusCode.ResourceExhausted, (await Assert.ThrowsAsync<RpcException>(async () => await client.CreateChangeSetAsync(request))).StatusCode);
            else Assert.Equal(fault == "unbound-import" ? PlanningStatus.ImportNotSupported : PlanningStatus.InvalidRequest, (await client.CreateChangeSetAsync(request)).Status);
            Assert.Equal(before, await ReferenceSnapshot(connection));
        }
        var stock = await client.ImportAsync(new()
        {
            OriginalEnvelope = ByteString.CopyFromUtf8(System.Text.Encoding.UTF8.GetString(Envelope("stock-reference-refusal"))
            .Replace("postgresql.primary", "sample.primary", StringComparison.Ordinal))
        });
        Assert.Equal(ImportReply.OutcomeOneofCase.Created, stock.OutcomeCase);
        var unsupported = ReferenceChange(stock.Created.Revision.Reference);
        unsupported.Acquisitions.Resources.Clear();
        unsupported.Acquisitions.Resources.Add(new ResourceAcquisitionMember
        {
            Resource = "db",
            Mode = AcquisitionMode.Reference,
            Input = new() { Format = "sample.reference-input/v1", Json = "{\"Identity\":\"sample-service\"}" }
        });
        Assert.Equal(PlanningStatus.ReferenceNotSupported, (await client.CreateChangeSetAsync(unsupported)).Status);
        Assert.Equal(before, await ReferenceSnapshot(connection));
        var empty = ReferenceEnvelope("empty-acquisition");
        empty["manifest"]!["spec"]!["resources"] = new JsonObject();
        empty["manifest"]!["spec"]!["components"] = new JsonObject();
        var emptyRevision = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(empty.ToJsonString()) });
        Assert.Equal(ImportReply.OutcomeOneofCase.Created, emptyRevision.OutcomeCase);
        var emptySet = await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = "pilot",
            Reference = emptyRevision.Created.Revision.Reference,
            Precondition = PlanningPrecondition.ExpectedAbsence,
            Acquisitions = new()
        });
        Assert.Equal(PlanningStatus.Created, emptySet.Status);
        var provision = ReferenceChange(revision); provision.Acquisitions = null;
        var legacy = await client.CreateChangeSetAsync(provision);
        Assert.Equal(PlanningStatus.Created, legacy.Status);
        Assert.Equal(PlanningStatus.ProviderCapabilityMismatch, (await client.CreatePlanAsync(new() { ChangeSetId = legacy.ChangeSet.ChangeSetId })).Status);
    }
    private static ManifestCatalogDbContext ReferenceReadContext(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static async Task<string> ReferenceSnapshot(string connection)
    {
        await using var db = ReferenceReadContext(connection);
        return string.Join(':', await db.DurableChangeSets.CountAsync(), await db.DurableOperationAttempts.CountAsync(),
            await db.OperationOutputs.CountAsync(), await db.ResourceManagement.CountAsync(), await db.GeneratedCredentialSlots.CountAsync(),
            await db.OperatorImportRequestRecords.CountAsync(), await db.ProtectedApplicationCredentials.CountAsync(), await db.ProtectedCredentialOwners.CountAsync());
    }
}
