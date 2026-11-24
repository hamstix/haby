using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class SharedReferenceDeliveryTests
{
    [Fact]
    public async Task Runtime_preparation_reuses_confirmed_Reference_resources_without_new_external_effects()
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
        await new PostgreSqlPlacementSelectionStore(contexts, codec).ApplyAsync(PlacementSelectionTarget.Create("environment", "", "", ""), reference,
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
        var prepared = await preparation.PrepareAsync(commit, "operator", default);
        Assert.Null(prepared.Failure);
        Assert.Equal(2, prepared.Prepared.Count);
        Assert.Equal(0, scenario.Boundary.PayloadQueries);
        Assert.Equal(observations, scenario.Boundary.Observations);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            Assert.Equal(4, await verify.ConfigurationSharedUses.CountAsync());
            Assert.Equal(2, await verify.WorkloadConfigurationRecipients.CountAsync());
        }
        var handler = DurableDeploymentPlanningTests.ImportedWorkloadHandlers().Identity;
        var actions = new InternalDurableActions(contexts, [handler], services.GetServices<IProviderConfigurationCodec>(),
            services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), TimeProvider.System, configurationPreparation: preparation,
            reuseVerifiers: services.GetServices<IDurableResourceReuseVerifier>());
        var identities = await new InternalRuntimeIdentityPreparation(contexts, preparation, actions, handler.Contract.Action).PrepareAsync(commit, default);
        Assert.Equal(2, identities.Count);
        Assert.All(identities, x => Assert.Equal(InternalActionOutcome.Confirmed, x.Result.Outcome));
        var publication = new InternalRuntimePublication(contexts, preparation, actions, TimeProvider.System);
        var requests = await publication.PreparationRequestsAsync(commit, "operator", default);
        Assert.Equal(2, requests.Length);
        foreach (var request in requests) Assert.Equal(RuntimePublicationOutcome.Published, (await publication.ApplyAsync(request, default)).Receipt.Outcome);
        using var admission = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        var authenticator = new ExactRuntimeIdentity();
        var runtime = new DeliverRuntimeConfiguration(new(new PostgreSqlRuntimeIdentityStore(contexts, actions), [authenticator], admission, TimeProvider.System),
            new PostgreSqlRuntimeConfigurationStore(contexts), new(services.GetRequiredService<ISecretStore>(), services.GetRequiredService<ConfigurationRenderingCatalog>(),
                services.GetServices<IConfigurationResolutionBinding>(), services.GetRequiredService<IConfigurationApplicationAuthorization>()));
        foreach (var request in requests)
        {
            scenario.Boundary.ResetMaterial();
            using var delivered = await runtime.ExecuteAsync(request.Binding, ConfigurationDocumentRevisionId.Parse(request.Revision!.Value.ToString()),
                Encoding.UTF8.GetBytes(request.Binding.ToString("N")), default);
            Assert.Equal(RuntimeDeliveryOutcome.Delivered, delivered.Outcome);
            Assert.Equal(1, scenario.Boundary.MaterialReads);
            Assert.Equal(2, scenario.Boundary.PayloadQueries);
        }
        await using var host = await DeliveryHost(scenario, actions);
        await RpcDelivery(host, scenario, baselineDocuments, requests, allowed: true);
        await using (var maintenance = await contexts.CreateDbContextAsync())
        {
            var owner = Guid.Parse(imported.ManifestRevisionRef.ApplicationRecordId.ToString());
            // Trusted test-only maintenance; no new removal API or direct caller authority.
            Assert.Equal(1, await maintenance.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM resource_management_heads WHERE application_record_id = {owner} AND resource_local_id = {"left"}"));
        }
        foreach (var request in requests.OrderBy(x => x.Origin!.Component == "worker" ? 0 : 1))
        {
            scenario.Boundary.ResetMaterial();
            using var own = await runtime.ExecuteAsync(request.Binding, ConfigurationDocumentRevisionId.Parse(request.Revision!.Value.ToString()),
                Encoding.UTF8.GetBytes(request.Binding.ToString("N")), default);
            var active = request.Origin!.Component == "worker";
            Assert.Equal(active ? RuntimeDeliveryOutcome.Delivered : RuntimeDeliveryOutcome.PermissionDenied, own.Outcome);
            Assert.Equal(active ? 1 : 0, scenario.Boundary.MaterialReads);
            Assert.Equal(active ? 2 : 0, scenario.Boundary.PayloadQueries);
        }
        Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await services.GetRequiredService<ISharedCredentialAdministration>()
            .RevokeAsync(Scope(scenario, SharedCredentialUse.ApplicationDelivery), "revoker", default));
        await RpcDelivery(host, scenario, baselineDocuments, requests, allowed: false);
        foreach (var request in requests)
        {
            scenario.Boundary.ResetMaterial();
            using var denied = await runtime.ExecuteAsync(request.Binding, ConfigurationDocumentRevisionId.Parse(request.Revision!.Value.ToString()),
                Encoding.UTF8.GetBytes(request.Binding.ToString("N")), default);
            Assert.Equal(RuntimeDeliveryOutcome.PermissionDenied, denied.Outcome);
            Assert.Equal(0, scenario.Boundary.PayloadQueries);
        }
    }
    private sealed class ExactRuntimeIdentity : IRuntimeAuthenticator
    {
        public RuntimeAuthenticatorContract Contract => new("haby.kubernetes.token-review/v1", "haby.kubernetes.runtime-validation/v1", 1,
            KubernetesProviderConfigurationCodec.Kind, KubernetesProviderConfigurationCodec.Shape, 2, KubernetesProviderConfigurationCodec.Implementation);
        public bool Validate(RuntimeTrust trust) => trust.Authenticator == Contract.Authenticator;
        public ValueTask<RuntimeAuthenticationOutcome> AuthenticateAsync(RuntimeTrust trust, ReadOnlyMemory<byte> credential, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(credential.Span.SequenceEqual(Encoding.UTF8.GetBytes(trust.Binding.ToString("N")))
                ? RuntimeAuthenticationOutcome.Authenticated : RuntimeAuthenticationOutcome.Unauthenticated);
        }
    }

}
