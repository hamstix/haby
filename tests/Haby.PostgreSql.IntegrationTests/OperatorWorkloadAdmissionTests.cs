using Grpc.Core;
using Grpc.Net.Client;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Admission_stage_does_not_report_historical_internal_work_as_not_submitted_or_read_receipts()
    {
        var scenario = await new DurableDeploymentPlanningTests(fixture).InternalScenarioAsync(initialOnly: true);
        await using var services = DurableDeploymentPlanningTests.InternalServices(scenario.Connection, scenario.Time);
        var admission = new PostgreSqlOperatorDeploymentAdmission(services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(),
            (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>(),
            services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), services.GetServices<IInternalActionHandler>());
        Assert.Equal(OperatorWorkloadSubmission.NotSubmitted, (await admission.ReadStageAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default))!.Submission);
        var handler = services.GetRequiredService<IInternalActionHandler>();
        await DurableDeploymentPlanningTests.Actions(services, handler).AcceptAsync(scenario.Commit,
            new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main")), Haby.Kubernetes.InternalDeploymentAction.Create, "tester", default);
        var result = await admission.ReadStageAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.Equal(OperatorWorkloadSubmission.Unknown, result!.Submission);
        Assert.Null(result.AppliedGeneration);
    }

    [Theory]
    [InlineData("initial-resource", true)]
    [InlineData("new-resource", false)]
    [InlineData("removed-resource", false)]
    [InlineData("later-subject", true)]
    public async Task Operator_Workload_shapes_refuse_before_acceptance_despite_unknown_authority_fields(string fault, bool initial)
    {
        await Assert.ThrowsAsync<AdmissionCheckedException>(() => new DurableDeploymentPlanningTests(fixture).InternalScenarioAsync(initialOnly: initial,
            invalidSecondWorkload: fault == "later-subject", configureManifest: envelope =>
            {
                var spec = envelope["manifest"]!["spec"]!;
                if (fault == "removed-resource") spec.AsObject().Remove("resources");
                if (fault is "initial-resource" or "new-resource")
                {
                    spec["resources"] ??= new System.Text.Json.Nodes.JsonObject();
                    spec["resources"]!["added"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"added"}}"""u8);
                }
            }, acceptGeneration: async (services, environment, reference) =>
            {
                var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
                string connection; long? baseline;
                await using (var read = await factory.CreateDbContextAsync())
                {
                    connection = read.Database.GetConnectionString()!;
                    baseline = await read.ApplicationDeployments.Select(x => (long?)x.DesiredGeneration).SingleOrDefaultAsync();
                }
                await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                using var channel = GrpcChannel.ForAddress(host.Urls.Single());
                var client = OperatorTestCredentials.Client(channel);
                var change = (await services.GetRequiredService<IDurableDeploymentPlanner>().CreateChangeSetAsync(environment, reference.ApplicationRecordId, reference,
                    baseline is null ? new DeploymentPrecondition.ExpectedAbsence() : new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(baseline.Value)), "tester", default)).ChangeSet!;
                using var bytes = new MemoryStream();
                using (var wire = new Google.Protobuf.CodedOutputStream(bytes, leaveOpen: true))
                {
                    wire.WriteTag(1, Google.Protobuf.WireFormat.WireType.LengthDelimited); wire.WriteString(change.Id.ToString());
                    wire.WriteTag(91, Google.Protobuf.WireFormat.WireType.Varint); wire.WriteBool(true);
                    wire.WriteTag(92, Google.Protobuf.WireFormat.WireType.LengthDelimited); wire.WriteString("forged-internal-receipt");
                    wire.WriteTag(93, Google.Protobuf.WireFormat.WireType.LengthDelimited); wire.WriteString("operator-admin");
                }
                Assert.Equal(PlanningStatus.UnsupportedShape, (await client.CreatePlanAsync(ChangeSetRequest.Parser.ParseFrom(bytes.ToArray()))).Status);
                await using var verify = await factory.CreateDbContextAsync();
                Assert.Equal(initial ? 0 : 1, await verify.DeploymentGenerations.CountAsync());
                Assert.Empty(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.WorkloadConfigurationRecipients.AsNoTracking().ToArrayAsync());
                throw new AdmissionCheckedException();
            }));
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("placement")]
    [InlineData("closed")]
    [InlineData("expired")]
    public async Task Operator_Workload_Check_and_Commit_revalidate_saved_support_and_freshness(string fault)
    {
        await Assert.ThrowsAsync<AdmissionCheckedException>(() => new DurableDeploymentPlanningTests(fixture).InternalScenarioAsync(initialOnly: true,
            acceptGeneration: async (services, environment, reference) =>
            {
                var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
                string connection;
                await using (var read = await factory.CreateDbContextAsync()) connection = read.Database.GetConnectionString()!;
                var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
                await using var host = OperatorHostApplication.Build(args);
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                using var channel = GrpcChannel.ForAddress(host.Urls.Single());
                var client = OperatorTestCredentials.Client(channel);
                var change = (await services.GetRequiredService<IDurableDeploymentPlanner>().CreateChangeSetAsync(environment,
                    reference.ApplicationRecordId, reference, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
                var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.Id.ToString() });
                Assert.Equal(PlanningStatus.Planned, plan.Status);
                if (fault == "closed") await client.ClosePlanAsync(new PlanRequest { PlanId = plan.Plan.PlanId });
                if (fault == "placement")
                {
                    var codec = new Haby.Kubernetes.Contracts.KubernetesPlacementCodec();
                    var declaration = codec.Decode("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"later","revision":1},"spec":{"namespace":"later"}}"""u8.ToArray());
                    var alias = ProviderAlias.Create("kubernetes.primary");
                    var key = new PlacementRevisionKey(environment, alias, declaration.Name, declaration.Revision);
                    await new PostgreSqlPlacementRevisionStore(factory, codec).CompareAndCreateAsync(key, declaration, new("tester", DateTimeOffset.UtcNow), default);
                    var selections = new PostgreSqlPlacementSelectionStore(factory, codec);
                    var target = Haby.Application.PlacementSelectionTarget.Create("environment", "", "", "");
                    var current = (await selections.FindAsync(environment, alias, target, default))!;
                    await selections.ApplyAsync(target, key, new PlacementSelectionPrecondition.ExpectedToken(current.Token), new("tester", DateTimeOffset.UtcNow, null), default);
                }
                await host.StopAsync();
                await using var restarted = OperatorHostApplication.Build(args, s =>
                {
                    if (fault == "expired") s.AddSingleton<TimeProvider>(new AdmissionFutureClock());
                    if (fault == "handler") s.AddSingleton<IOperatorDeploymentAdmission>(p => new PostgreSqlOperatorDeploymentAdmission(
                        p.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(), (PostgreSqlDurableDesiredStateCommitter)p.GetRequiredService<IDurableDesiredStateCommitter>(), factory, []));
                });
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
                using var freshChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
                client = OperatorTestCredentials.Client(freshChannel);
                Assert.NotEqual(PlanningStatus.Current, (await client.CheckPlanAsync(new PlanRequest { PlanId = plan.Plan.PlanId })).Status);
                Assert.Equal(plan.Plan.ProviderInputs, (await client.GetPlanAsync(new PlanRequest { PlanId = plan.Plan.PlanId })).Plan.ProviderInputs);
                var result = await client.CommitDesiredStateAsync(new CommitDesiredStateRequest { ChangeSetId = change.Id.ToString(), PlanId = plan.Plan.PlanId });
                Assert.Equal(fault switch { "closed" => CommitStatus.Closed, "expired" => CommitStatus.Expired, _ => CommitStatus.PlanInputUnavailable }, result.Status);
                if (fault == "handler")
                    Assert.Equal(PlanningStatus.UnsupportedShape, (await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.Id.ToString() })).Status);
                await using var verify = await factory.CreateDbContextAsync();
                Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
                throw new AdmissionCheckedException();
            }));
    }

    private sealed class AdmissionCheckedException : Exception;
    private sealed class AdmissionFutureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Operator_admits_supported_Workload_desired_state_without_execution(bool initial)
    {
        var diagnostic = new WorkloadAdmissionDiagnostics(initial);
        await diagnostic.RunAsync(async releaseRoot =>
        {
            diagnostic.At(WorkloadAdmissionCheckpoint.TrapBuild);
            var trap = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            diagnostic.At(WorkloadAdmissionCheckpoint.TrapStart);
            releaseRoot(() => { trap.Dispose(); return ValueTask.CompletedTask; });
            trap.Start();
            diagnostic.At(WorkloadAdmissionCheckpoint.TrapAccept);
            var contact = trap.AcceptTcpClientAsync();
            diagnostic.At(WorkloadAdmissionCheckpoint.ScenarioSetup);
            EnvironmentRecordId? fixtureEnvironment = null;
            Haby.Kubernetes.KubernetesProviderConfiguration? fixtureConfiguration = null;
            var scenario = await new DurableDeploymentPlanningTests(fixture).InternalScenarioAsync(initialOnly: initial, suppliedConfiguration: async (secrets, environment) =>
            {
                var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
                await secrets.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), secret, "operator-workload-no-effects-sentinel"u8.ToArray(), default);
                fixtureEnvironment = environment;
                fixtureConfiguration = new Haby.Kubernetes.KubernetesProviderConfiguration(new Uri("https://127.0.0.1:" + ((System.Net.IPEndPoint)trap.LocalEndpoint).Port), null, secret, null, null);
                return fixtureConfiguration;
            }, actualKubernetesProbe: async (secrets, environment, _, configuration, token) =>
            {
                // Setup-only deterministic double for this invocation, not production preflight evidence.
                token.ThrowIfCancellationRequested();
                var expectedEnvironment = fixtureEnvironment;
                var expectedConfiguration = fixtureConfiguration;
                if (expectedEnvironment is null || environment != expectedEnvironment || expectedConfiguration is null ||
                    configuration != expectedConfiguration ||
                    !string.Equals(configuration.Endpoint.AbsoluteUri, expectedConfiguration.Endpoint.AbsoluteUri, StringComparison.Ordinal) ||
                    expectedConfiguration.BearerToken is not { } secret)
                    return new(ProviderPreflightOutcome.ConfigurationUnavailable);
                using var material = await secrets.ResolveBoundedAsync(
                    new(new SecretOwner.Environment(expectedEnvironment), SecretPurpose.ProviderAdministrator), secret, 64, token);
                token.ThrowIfCancellationRequested();
                if (material is null) return new(ProviderPreflightOutcome.SecretUnavailable);
                var bytes = material.CopyBytes();
                try
                {
                    token.ThrowIfCancellationRequested();
                    return new(bytes.AsSpan().SequenceEqual("operator-workload-no-effects-sentinel"u8)
                        ? ProviderPreflightOutcome.Ready : ProviderPreflightOutcome.AuthenticationFailed);
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }, configureManifest: e => DurableDeploymentPlanningTests.ConfiguredDocuments(e), acceptGeneration: async (services, environment, reference) =>
            {
                diagnostic.At(WorkloadAdmissionCheckpoint.AdmissionServices);
                DurableChangeSetCommit? saved = null;
                await diagnostic.RunAsync(async releaseAdmission =>
                {
                    var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
                    long? baseline = null;
                    diagnostic.At(WorkloadAdmissionCheckpoint.BaselineRead);
                    await diagnostic.RunAsync(async releaseBaseline =>
                {
                    var read = await factory.CreateDbContextAsync();
                    releaseBaseline(read.DisposeAsync);
                    baseline = await read.ApplicationDeployments.Select(x => (long?)x.DesiredGeneration).SingleOrDefaultAsync();
                });
                    string connection = null!;
                    diagnostic.At(WorkloadAdmissionCheckpoint.ConnectionRead);
                    await diagnostic.RunAsync(async releaseConnection =>
                {
                    var read = await factory.CreateDbContextAsync();
                    releaseConnection(read.DisposeAsync);
                    connection = read.Database.GetConnectionString()!;
                });
                    var secretTrap = new AdmissionSecretTrap();
                    diagnostic.At(WorkloadAdmissionCheckpoint.HostBuild);
                    var host = OperatorHostApplication.Build([
                    "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], s => s.AddSingleton<ISecretStore>(secretTrap));
                    releaseAdmission(host.DisposeAsync);
                    diagnostic.At(WorkloadAdmissionCheckpoint.HostStart);
                    await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                    diagnostic.At(WorkloadAdmissionCheckpoint.Channel);
                    var channel = GrpcChannel.ForAddress(host.Urls.Single());
                    releaseAdmission(() => { channel.Dispose(); return ValueTask.CompletedTask; });
                    var client = OperatorTestCredentials.Client(channel);
                    diagnostic.At(WorkloadAdmissionCheckpoint.RequestBuild);
                    var request = new CreateChangeSetRequest
                    {
                        EnvironmentId = initial ? "initial" : "pilot",
                        Reference = new RevisionReference { ApplicationRecordId = reference.ApplicationRecordId.ToString(), RevisionNumber = reference.RevisionNumber.Value },
                        Precondition = baseline is null ? PlanningPrecondition.ExpectedAbsence : PlanningPrecondition.ExpectedGeneration
                    };
                    // Read the actual persisted Environment key rather than assume the fixture's name.
                    diagnostic.At(WorkloadAdmissionCheckpoint.EnvironmentRead);
                    await diagnostic.RunAsync(async releaseEnvironment =>
                {
                    var read = await factory.CreateDbContextAsync();
                    releaseEnvironment(read.DisposeAsync);
                    request.EnvironmentId = (await read.Environments.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(environment.ToString()))).EnvironmentId;
                });
                    if (baseline is { } expected) request.ExpectedGeneration = expected;
                    diagnostic.At(WorkloadAdmissionCheckpoint.AnonymousClient);
                    var anonymous = new ManifestOperator.ManifestOperatorClient(channel);
                    foreach (var headers in new[] { new Metadata(), new Metadata { { "authorization", "Bearer " + ConfigurationDeliveryCredential.Create(Guid.CreateVersion7()) } } })
                    {
                        diagnostic.At(WorkloadAdmissionCheckpoint.AnonymousDenied);
                        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await anonymous.CreateChangeSetAsync(request, headers))).StatusCode);
                    }
                    diagnostic.At(WorkloadAdmissionCheckpoint.ChangeSetCreate);
                    var change = (await client.CreateChangeSetAsync(request)).ChangeSet;
                    diagnostic.At(WorkloadAdmissionCheckpoint.ChangeSetPresent);
                    Assert.NotNull(change);
                    diagnostic.At(WorkloadAdmissionCheckpoint.PlanCreate);
                    var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSetId });
                    diagnostic.At(WorkloadAdmissionCheckpoint.PlanStatus);
                    Assert.Equal(PlanningStatus.Planned, plan.Status);
                    diagnostic.At(WorkloadAdmissionCheckpoint.PlanCurrent);
                    Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new PlanRequest { PlanId = plan.Plan.PlanId })).Status);
                    diagnostic.At(WorkloadAdmissionCheckpoint.DescriptionStatus);
                    Assert.Equal("available", plan.Plan.WorkloadDescription.Status);
                    diagnostic.At(WorkloadAdmissionCheckpoint.DescriptionItems);
                    Assert.Single(plan.Plan.WorkloadDescription.Items);
                    diagnostic.At(WorkloadAdmissionCheckpoint.DescriptionRedaction);
                    Assert.DoesNotContain("operator-workload-no-effects-sentinel", plan.ToString());
                    diagnostic.At(WorkloadAdmissionCheckpoint.OtherChangeSetCreate);
                    var wrong = (await client.CreateChangeSetAsync(request)).ChangeSet;
                    diagnostic.At(WorkloadAdmissionCheckpoint.PlanMembership);
                    Assert.Equal(CommitStatus.PlanDoesNotBelongToChangeSet, (await client.CommitDesiredStateAsync(new CommitDesiredStateRequest { ChangeSetId = wrong.ChangeSetId, PlanId = plan.Plan.PlanId })).Status);
                    var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
                    diagnostic.At(WorkloadAdmissionCheckpoint.NativeRequired);
                    if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
                    if (binary is not null)
                    {
                        await diagnostic.RunAsync(async releaseNative =>
                    {
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeChangeSet);
                        var created = JsonDocument.Parse(await RunNativeCommitCli(binary, host.Urls.Single(), ["changeset", "create", reference.ApplicationRecordId.ToString(), reference.RevisionNumber.Value.ToString(), .. (baseline is null ? new[] { "--expected-absence" } : new[] { "--expected-generation", baseline.Value.ToString() })], environment: request.EnvironmentId));
                        releaseNative(() => { created.Dispose(); return ValueTask.CompletedTask; });
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeChangeSetRead);
                        var nativeChange = created.RootElement.GetProperty("data"u8).GetProperty("changeSet"u8).GetProperty("changeSetId"u8).GetString()!;
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativePlan);
                        var nativePlan = JsonDocument.Parse(await RunNativeCommitCli(binary, host.Urls.Single(), ["plan", "create", nativeChange], environment: request.EnvironmentId));
                        releaseNative(() => { nativePlan.Dispose(); return ValueTask.CompletedTask; });
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeDescription);
                        Assert.Equal("available", nativePlan.RootElement.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("workloadDescription"u8).GetProperty("status"u8).GetString());
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeCheck);
                        await RunNativeCommitCli(binary, host.Urls.Single(), ["plan", "check", plan.Plan.PlanId]);
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeCommit);
                        var committed = JsonDocument.Parse(await RunNativeCommitCli(binary, host.Urls.Single(), ["changeset", "commit", change.ChangeSetId, "--plan", plan.Plan.PlanId]));
                        releaseNative(() => { committed.Dispose(); return ValueTask.CompletedTask; });
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeSubmission);
                        Assert.Equal("notSubmitted", committed.RootElement.GetProperty("data"u8).GetProperty("workloadStage"u8).GetProperty("submission"u8).GetString());
                    });
                    }
                    diagnostic.At(WorkloadAdmissionCheckpoint.Commit);
                    var accepted = await client.CommitDesiredStateAsync(new CommitDesiredStateRequest { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId });
                    diagnostic.At(WorkloadAdmissionCheckpoint.CommitStatus);
                    Assert.Equal(CommitStatus.Committed, accepted.Status);
                    diagnostic.At(WorkloadAdmissionCheckpoint.Submission);
                    Assert.Equal("notSubmitted", accepted.WorkloadStage.Submission);
                    diagnostic.At(WorkloadAdmissionCheckpoint.AppliedFlag);
                    Assert.Equal(baseline is not null, accepted.WorkloadStage.HasAppliedGeneration);
                    diagnostic.At(WorkloadAdmissionCheckpoint.FrozenDescription);
                    Assert.Equal(plan.Plan.WorkloadDescription, accepted.DesiredGeneration.WorkloadDescription);
                    diagnostic.At(WorkloadAdmissionCheckpoint.ActorAuthorization);
                    Assert.Equal(OperatorCredential.Actor((await host.Services.GetRequiredService<IOperatorAccessStore>().AuthorizeAsync(OperatorTestCredentials.Credential(host.Urls.Single()), default))!.Id), accepted.Commit.TechnicalActorId);
                    diagnostic.At(WorkloadAdmissionCheckpoint.ActorIdentity);
                    Assert.Equal(change.TechnicalActorId, accepted.Commit.TechnicalActorId);
                    var generationRequest = new GenerationRequest { DeploymentId = accepted.Commit.DeploymentId, Generation = accepted.Commit.Generation };
                    diagnostic.At(WorkloadAdmissionCheckpoint.ExecutionGuard);
                    Assert.NotEqual(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(generationRequest)).Status);
                    diagnostic.At(WorkloadAdmissionCheckpoint.NoProgress);
                    Assert.Null((await client.GetGenerationAsync(generationRequest)).Progress);
                    diagnostic.At(WorkloadAdmissionCheckpoint.HostStop);
                    await host.StopAsync();
                    diagnostic.At(WorkloadAdmissionCheckpoint.RestartBuild);
                    var restarted = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], s => s.AddSingleton<ISecretStore>(secretTrap));
                    releaseAdmission(restarted.DisposeAsync);
                    diagnostic.At(WorkloadAdmissionCheckpoint.RestartStart);
                    await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
                    diagnostic.At(WorkloadAdmissionCheckpoint.RestartChannel);
                    var freshChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
                    releaseAdmission(() => { freshChannel.Dispose(); return ValueTask.CompletedTask; });
                    client = OperatorTestCredentials.Client(freshChannel);
                    diagnostic.At(WorkloadAdmissionCheckpoint.GenerationRead);
                    var readBack = await client.GetGenerationAsync(generationRequest);
                    diagnostic.At(WorkloadAdmissionCheckpoint.GenerationIdentity);
                    Assert.Equal(accepted.DesiredGeneration, readBack.DesiredGeneration);
                    diagnostic.At(WorkloadAdmissionCheckpoint.PlanIdentity);
                    Assert.Equal(plan.Plan, (await client.GetPlanAsync(new PlanRequest { PlanId = plan.Plan.PlanId })).Plan);
                    if (binary is not null)
                    {
                        await diagnostic.RunAsync(async releaseNative =>
                    {
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeRead);
                        var text = await RunNativeCommitCli(binary, restarted.Urls.Single(), ["deployment", "generation", "get", generationRequest.DeploymentId, generationRequest.Generation.ToString()], format: "text");
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeReadText);
                        Assert.Contains("notSubmitted", text);
                        diagnostic.At(WorkloadAdmissionCheckpoint.NativeReadinessText);
                        Assert.Contains("Readiness is not established", text);
                    });
                    }
                    diagnostic.At(WorkloadAdmissionCheckpoint.Replay);
                    var retry = await client.CommitDesiredStateAsync(new CommitDesiredStateRequest { ChangeSetId = change.ChangeSetId, PlanId = Guid.CreateVersion7().ToString() });
                    diagnostic.At(WorkloadAdmissionCheckpoint.ReplayIdentity);
                    Assert.Equal(accepted, retry);
                    diagnostic.At(WorkloadAdmissionCheckpoint.ReceiptRead);
                    saved = await services.GetRequiredService<IDurableDesiredStateCommitter>().FindCommitAsync(DeploymentChangeSetId.Parse(change.ChangeSetId), default);
                    diagnostic.At(WorkloadAdmissionCheckpoint.ReceiptPresent);
                    Assert.NotNull(saved);
                    diagnostic.At(WorkloadAdmissionCheckpoint.SecretReads);
                    Assert.Equal(0, secretTrap.Reads);
                });
                diagnostic.At(WorkloadAdmissionCheckpoint.ScenarioSetup);
                return saved!;
            });
            diagnostic.At(WorkloadAdmissionCheckpoint.PublicConfigurationGuard);
            await DurableDeploymentPlanningTests.PublicConfigurationRefusesAsync(scenario);
            diagnostic.At(WorkloadAdmissionCheckpoint.VerificationServices);
            var verifyServices = DurableDeploymentPlanningTests.InternalServices(scenario.Connection, scenario.Time);
            releaseRoot(verifyServices.DisposeAsync);
            diagnostic.At(WorkloadAdmissionCheckpoint.VerificationContext);
            var verify = await verifyServices.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            releaseRoot(verify.DisposeAsync);
            diagnostic.At(WorkloadAdmissionCheckpoint.NoProviderContact);
            Assert.False(contact.IsCompleted, "Plan/Commit/Get contacted the Kubernetes boundary.");
            diagnostic.At(WorkloadAdmissionCheckpoint.DeploymentRead);
            var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
            diagnostic.At(WorkloadAdmissionCheckpoint.DesiredGeneration);
            Assert.Equal(initial ? 1 : 2, owner.DesiredGeneration);
            diagnostic.At(WorkloadAdmissionCheckpoint.AppliedGeneration);
            Assert.Equal(initial ? (long?)null : 1, owner.AppliedGeneration);
            diagnostic.At(WorkloadAdmissionCheckpoint.NoActionIntents);
            Assert.Empty(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
            diagnostic.At(WorkloadAdmissionCheckpoint.NoRecipients);
            Assert.Empty(await verify.WorkloadConfigurationRecipients.AsNoTracking().ToArrayAsync());
            diagnostic.At(WorkloadAdmissionCheckpoint.NoDocuments);
            Assert.Empty(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
            diagnostic.At(WorkloadAdmissionCheckpoint.NoIdentities);
            Assert.Empty(await verify.RuntimeIdentityBindings.AsNoTracking().ToArrayAsync());
            diagnostic.At(WorkloadAdmissionCheckpoint.NoPermissions);
            Assert.Empty(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
            diagnostic.At(WorkloadAdmissionCheckpoint.NoManagedWorkloads);
            Assert.Empty(await verify.ManagedWorkloads.AsNoTracking().ToArrayAsync());
            diagnostic.At(WorkloadAdmissionCheckpoint.TargetIdentity);
            var target = Guid.Parse(scenario.Commit.DeploymentId.ToString());
            diagnostic.At(WorkloadAdmissionCheckpoint.NoOperations);
            Assert.Empty(await verify.DurableOperations.AsNoTracking().Where(x => x.DeploymentId == target && x.Generation == scenario.Commit.Generation.Value).ToArrayAsync());
            diagnostic.At(WorkloadAdmissionCheckpoint.NoCheckpoints);
            Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().Where(x => x.DeploymentId == target && x.Generation == scenario.Commit.Generation.Value).ToArrayAsync());
            diagnostic.At(WorkloadAdmissionCheckpoint.NoJournalProgress);
            Assert.Null(await verifyServices.GetRequiredService<IDurableOperationJournal>().ProgressAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default));
        });
    }

    private sealed class AdmissionSecretTrap : ISecretStore
    {
        internal int Reads;
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new InvalidOperationException("Unexpected secret write.");
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new InvalidOperationException("Unexpected secret write.");
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new InvalidOperationException("Unexpected secret write.");
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token)
        { Interlocked.Increment(ref Reads); throw new InvalidOperationException("Admission resolved secret material."); }
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token)
        { Interlocked.Increment(ref Reads); throw new InvalidOperationException("Admission resolved secret material."); }
    }

}
