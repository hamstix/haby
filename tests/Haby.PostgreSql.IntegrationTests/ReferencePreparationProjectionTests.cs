using Grpc.Net.Client;
using Haby.Operator.Contracts;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class SharedReferenceDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_Reference_preparation_inspection_preserves_target_proof_after_interruption(bool publication)
    {
        var scenario = await Database();
        var baselineDocuments = await Materialize(scenario);
        var baseline = baselineDocuments.Commit;
        await using var services = Services(scenario.Connection, scenario.Boundary);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await services.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(scenario.Environment),
            SecretPurpose.ProviderAdministrator), secret, "test-runtime-administration"u8.ToArray(), default);
        var configuration = new KubernetesProviderConfiguration(new Uri("https://127.0.0.1:6443"), null, secret, null, null);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(scenario.Environment, configuration, default);
        await providers.AddRevisionAsync(scenario.Environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        var alias = ProviderAlias.Create("kubernetes.primary");
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(scenario.Environment,
            alias, null, new(instance, ProviderConfigurationRevisionNumber.Create(1)), "operator", default)).Outcome);
        var codec = new KubernetesPlacementCodec();
        var placement = codec.Decode(Encoding.UTF8.GetBytes("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard","revision":1},"spec":{
             "namespace":"test","runtimeConfiguration":{"endpoint":"https://configuration.example.invalid/","tls":{"mode":"system"}}}}
            """));
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var reference = new PlacementRevisionKey(scenario.Environment, alias, placement.Name, placement.Revision);
        await new PostgreSqlPlacementRevisionStore(contexts, codec).CompareAndCreateAsync(reference, placement, new("operator", DateTimeOffset.UtcNow), default);
        await new PostgreSqlPlacementSelectionStore(contexts, codec).ApplyAsync(Haby.Application.PlacementSelectionTarget.Create("environment", "", "", ""), reference,
            new PlacementSelectionPrecondition.ExpectedAbsence(), new("operator", DateTimeOffset.UtcNow, null), default);
        var declaration = Declaration("reference-consumer");
        declaration["applicationVersion"] = "2";
        foreach (var component in declaration["manifest"]!["spec"]!["components"]!.AsObject())
            component.Value!["workloads"] = JsonNode.Parse("""
                {"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/app:v1"},"configuration":{"documents":["settings"]}}}}
                """u8);
        var catalog = new ManifestContractCatalog([new(Type, "{\"type\":\"object\",\"additionalProperties\":false}", OptionsSchema)], [KubernetesContract.Definition], [], [new(RendererId)]);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes(declaration.ToJsonString()), "manifest-author"));
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var input = new DurableActionPayload("sample.reference-input/v1", System.Text.Json.JsonSerializer.Serialize(new ReferenceInput("verified-service"), AdapterJson.Default.ReferenceInput));
        var sharing = new DurableActionPayload("sample.sharing/v1", System.Text.Json.JsonSerializer.Serialize(new Sharing("verified-service"), AdapterJson.Default.Sharing));
        var acquisitions = new ResourceAcquisitionIntent(new[] { "left", "right" }.Select(r => new ResourceAcquisitionRequest(ManifestLocalId.Create(r), ResourceAcquisitionMode.Reference, input) { SharingRequest = sharing }).ToArray());
        var change = (await planner.CreateAcquisitionChangeSetAsync(scenario.Environment, imported.ManifestRevisionRef.ApplicationRecordId,
            imported.ManifestRevisionRef, acquisitions, "operator", default, new DeploymentPrecondition.ExpectedGeneration(baseline.Generation))).ChangeSet!;
        scenario.Boundary.ResetMaterial();
        var observations = scenario.Boundary.Observations;
        var plan = await planner.CreateInternalPlanAsync(change.Id, "operator", default, services.GetServices<IInternalActionHandler>().ToArray());
        Assert.Equal(DurablePlanStatus.Planned, plan.Status);
        Assert.Empty(plan.Plan!.Graph.Nodes);
        Assert.Equal(2, plan.Plan.Graph.ReusedResources.Count);
        Assert.Equal(0, scenario.Boundary.PayloadQueries);
        Assert.Equal(observations, scenario.Boundary.Observations);
        var commit = Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitInternalAsync(change.Id, plan.Plan.Id, "operator", default, services.GetServices<IInternalActionHandler>().ToArray())).Commit;
        var preparation = new InternalWorkloadConfiguration(contexts, planner, services.GetRequiredService<PostgreSqlConfigurationDocumentStore>(),
            services.GetServices<IInternalActionHandler>().ToArray(), services.GetServices<IExternalOperationOutputContract>(),
            services.GetServices<IProviderConfigurationCodec>(), services.GetRequiredService<DurableActionAdmission>(), services.GetServices<IDurableResourceReuseVerifier>());
        Assert.Null((await preparation.PrepareAsync(commit, "operator", default)).Failure);
        var handler = DurableDeploymentPlanningTests.ImportedWorkloadHandlers().Identity;
        var actions = new InternalDurableActions(contexts, [handler], services.GetServices<IProviderConfigurationCodec>(),
            services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), TimeProvider.System, configurationPreparation: preparation,
            reuseVerifiers: services.GetServices<IDurableResourceReuseVerifier>());
        using var cancellation = new CancellationTokenSource();
        var identity = new InternalRuntimeIdentityPreparation(contexts, preparation, actions, handler.Contract.Action);
        if (publication)
        {
            Assert.Equal(2, (await identity.PrepareAsync(commit, default)).Count);
            var calls = 0;
            var publisher = new InternalRuntimePublication(contexts, preparation, actions, TimeProvider.System, _ => { calls++; cancellation.Cancel(); });
            var interrupted = await new InternalRuntimePublicationPreparation(publisher).PrepareAsync(commit, "operator", cancellation.Token);
            Assert.True(interrupted.Cancelled); Assert.Single(interrupted.Completed); Assert.Equal(1, calls);
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => identity.PrepareAsync(commit, cancellation.Token));
        }
        await using var host = await DeliveryHost(scenario, actions);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var exact = new GenerationRequest { DeploymentId = commit.DeploymentId.ToString(), Generation = commit.Generation.Value };
        var current = await client.GetGenerationAsync(exact);
        Assert.Equal(CommitStatus.Found, current.Status); Assert.Null(current.Progress);
        Assert.Equal("available", current.DesiredGeneration.WorkloadDescription.Status);
        Assert.Equal(2, current.DesiredGeneration.ReusedResources.Count);
        Assert.Equal(2, current.WorkloadSubmission.Members.Count); Assert.True(current.WorkloadSubmission.HasResourcesReadyId);
        Assert.All(current.WorkloadSubmission.Members, member => Assert.Equal("prepared", member.Documents));
        var accepted = new DurableCommitSummary
        {
            DeploymentId = exact.DeploymentId,
            Generation = exact.Generation,
            PlanId = commit.PlanId.ToString(),
            ChangeSetId = commit.ChangeSetId.ToString(),
            TechnicalActorId = commit.ActorId
        };
        var revision = new RevisionReference { ApplicationRecordId = imported.ManifestRevisionRef.ApplicationRecordId.ToString(), RevisionNumber = imported.ManifestRevisionRef.RevisionNumber.Value };
        var lines = new List<string>(); var diagnostic = new PublicReferenceFailureDiagnostics(lines.Add);
        diagnostic.At(PublicReferenceCheckpoint.PreparationInspection);
        var submits = 0; var inspections = 0;
        try
        {
            await diagnostic.RunAsync(async _ =>
            {
                var result = await ReferencePreparationContinuation.RunAsync(exact, accepted, revision, baseline.Generation.Value,
                    input =>
                    {
                        Assert.Equal(exact, input); submits++;
                        if (submits == 1) return Task.FromResult(new WorkloadSubmissionReply { Outcome = "cancelled", Reason = "cancelled", Stage = publication ? "Publication" : "Identity", RequiresInspection = true });
                        // Only the policy's call boundary is simulated. Persisted preparation and both authenticated reads are real.
                        var completed = current.WorkloadSubmission.Clone();
                        foreach (var member in completed.Members) { member.Identity = "confirmed"; member.Publication = "published"; member.RuntimeAuthority = "available"; }
                        return Task.FromResult(new WorkloadSubmissionReply { Outcome = "stopped", Stage = "Deployment", Progress = completed });
                    }, async input => { inspections++; return await client.GetGenerationAsync(input); }, (_, _) => { }, diagnostic, input => ReferencePreparationCatalog.ReadAsync(scenario.Connection, input, accepted, revision, diagnostic));
                Assert.Equal("Deployment", result.Final.Stage);
            });
        }
        finally { foreach (var line in lines) Console.WriteLine(line); }
        var captured = await ReferencePreparationCatalog.ReadAsync(scenario.Connection, exact, accepted, revision);
        foreach (var fault in new Func<ReferencePreparationCatalogProof, ReferencePreparationCatalogProof>[]
        {
            proof => proof with { Generation = proof.Generation + 1 },
            proof => proof with { Ready = new(Guid.NewGuid(), proof.Deployment, proof.Generation, proof.Fingerprint, proof.Ready.ConfirmedAt, proof.Ready.Resources) },
            proof => proof with { Fingerprint = "v6:" + new string('0', 64) },
            proof => proof with { Reused = proof.Reused.Select((r, i) => i == 0 ? r with { ManagementId = Guid.NewGuid() } : r).ToArray() }
        })
        {
            var deniedSubmits = 0;
            var error = await Record.ExceptionAsync(() => ReferencePreparationContinuation.RunAsync(exact, accepted, revision, baseline.Generation.Value,
                _ => { deniedSubmits++; return Task.FromResult(new WorkloadSubmissionReply { Outcome = "cancelled", Reason = "cancelled", Stage = "Publication", RequiresInspection = true }); },
                async input => await client.GetGenerationAsync(input), (_, _) => { }, readResources: _ => Task.FromResult(fault(captured))));
            Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error); Assert.Equal(1, deniedSubmits);
        }
        Assert.Equal(2, submits); Assert.Equal(1, inspections);
        Assert.Equal(observations, scenario.Boundary.Observations);
    }
}
