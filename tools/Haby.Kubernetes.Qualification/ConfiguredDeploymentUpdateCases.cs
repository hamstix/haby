using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class ConfiguredDeploymentUpdateCases
{
    private sealed class LostUpdateResponse : IOException;
    internal static async Task Run(Kubernetes admin, ServiceProvider services, DurableChangeSetCommit baseline, JsonNode envelope,
        string image, IInternalActionHandler identity, IInternalActionHandler create, InternalWorkloadConfiguration configuration,
        ConcurrentQueue<Guid> requests, CancellationToken token)
    {
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var secrets = services.GetRequiredService<ISecretStore>();
        var deploymentId = Guid.Parse(baseline.DeploymentId.ToString());
        DeploymentReceipt previous;
        Guid binding;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var action = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == deploymentId && x.Generation == baseline.Generation.Value && x.ActionContract == InternalDeploymentAction.Create, token);
            previous = DeploymentIntents.DecodeReceipt((await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == deploymentId && x.Generation == baseline.Generation.Value && x.OperationId == action.OperationId, token)).Payload);
            binding = (await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == action.ManagedIdentity, token)).Id;
        }
        var oldDocuments = RuntimeBootstrapCodec.Decode(previous.Projection.Runtime!.Bootstrap).Documents.Values.Select(Guid.Parse).ToArray();
        Console.WriteLine("Q243 configured update-target-envelope start");
        var targetImage = ConfiguredDeploymentUpdateTarget.PrepareEnvelope(envelope, image);
        Console.WriteLine("Q243 configured update-target-envelope completed");
        var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition], [KubernetesContract.Definition], [], []);
        Console.WriteLine("Q243 configured update-import start");
        var imported = await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(), catalog,
            services.GetRequiredService<TimeProvider>()).ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "qualification");
        if (imported is not ImportApplicationManifestResult.Succeeded succeeded)
        {
            Console.WriteLine("Q243 configured update-import failed");
            throw new InvalidOperationException("Update manifest refused.");
        }
        var revision = succeeded.ManifestRevisionRef;
        Console.WriteLine("Q243 configured update-import completed");
        Console.WriteLine("Q243 configured update-image-baseline start");
        var baselinePods = await admin.CoreV1.ListNamespacedPodAsync(previous.Namespace,
            labelSelector: DeploymentIntents.ManagedKey + "=" + previous.ManagedWorkload.ToString("N"), cancellationToken: token);
        var baselineImageId = baselinePods.Items.Where(x => x.Spec.Containers.Any(c => c.Name == previous.Projection.Container && c.Image == image))
            .SelectMany(x => x.Status.ContainerStatuses ?? [])
            .Where(x => x.Name == previous.Projection.Container && x.State?.Running is not null && !string.IsNullOrEmpty(x.ImageID))
            .Select(x => x.ImageID.Split('@')[^1]).Distinct(StringComparer.Ordinal).Single();
        Console.WriteLine("Q243 configured update-image-baseline completed");
        Console.WriteLine("Q243 configured update-plan start");
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        EnvironmentRecordId environment;
        await using (var db = await contexts.CreateDbContextAsync(token))
            environment = EnvironmentRecordId.Parse((await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == deploymentId, token)).EnvironmentRecordId.ToString());
        var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision,
            new DeploymentPrecondition.ExpectedGeneration(baseline.Generation), "qualification", token)).ChangeSet!;
        Console.WriteLine("Q243 configured update-plan completed");
        Check((await planner.CreateOperatorPlanAsync(change.Id, "qualification", token, handlers)).Status == DurablePlanStatus.UnsupportedShape,
            "U01-public-update-remains-closed");
        var plan = (await planner.CreateInternalPlanAsync(change.Id, "qualification", token, handlers)).Plan!;
        var accepted = (await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitInternalAsync(change.Id, plan.Id, "qualification", token, handlers) as DurableCommitResult.Committed)?.Commit
            ?? throw new InvalidOperationException("Internal update acceptance refused.");
        // Simulate permitted foreign fields after the accepted baseline. The sidecar
        // uses the original reference; the target Pod must resolve the :443 reference.
        var foreignPatch = new JsonArray(
            new JsonObject { ["op"] = "add", ["path"] = "/metadata/annotations/qualification.example~1foreign", ["value"] = "retained" },
            new JsonObject
            {
                ["op"] = "add",
                ["path"] = "/spec/template/spec/containers/0",
                ["value"] = new JsonObject
                { ["name"] = "foreign", ["image"] = image, ["command"] = new JsonArray("dotnet", "--info") }
            });
        await admin.AppsV1.PatchNamespacedDeploymentAsync(new(foreignPatch.ToJsonString(), V1Patch.PatchType.JsonPatch), previous.Name, previous.Namespace, cancellationToken: token);
        var wire = new PatchEvidence();
        var update = new InternalDeploymentAction(secrets, connection => new DeploymentKernel(connection.Binding,
            ct => KubernetesClientFactory.CreateFixture(connection, secrets, [new CapturePatch(wire)], ct)), update: true);
        var loseResponse = true;
        InternalDurableActions Actions() => new(contexts, [identity, create, update], services.GetServices<IProviderConfigurationCodec>(),
            services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), services.GetRequiredService<TimeProvider>(), point =>
            { if (point == InternalActionFault.AfterExternalResult && loseResponse) { loseResponse = false; throw new LostUpdateResponse(); } }, configuration);
        var actions = Actions();
        Check((await configuration.PrepareAsync(accepted, "qualification", token)).Prepared.Count == 2, "U02-new-exact-documents-prepared");
        var reused = await new InternalRuntimeIdentityPreparation(contexts, configuration, actions, identity.Contract.Action).PrepareAsync(accepted, token);
        Check(reused.Count == 1 && reused[0].Binding == binding && reused[0].Result.Outcome == InternalActionOutcome.Confirmed,
            "U03-stable-account-original-proof-new-generation-use");
        var published = await new InternalRuntimePublicationPreparation(new(contexts, configuration, actions, services.GetRequiredService<TimeProvider>()))
            .PrepareAsync(accepted, "qualification", token);
        Check(published.Completed.Count == 2 && published.Reason is null && wire.Writes == 0, "U04-new-rights-before-patch-old-rights-retained");
        var subject = new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main"));
        var key = await actions.AcceptAsync(accepted, subject, update.Contract.Action, "configured-update", token);
        var intent = DeploymentIntents.Decode((await actions.HistoryAsync(key, token))!.Intent.Payload);
        var target = RuntimeBootstrapCodec.Decode(intent.Target.Runtime!.Bootstrap);
        Check(target.BindingId == binding.ToString("D") && target.Documents.Count == 2 && !target.Documents.Values.Select(Guid.Parse).Intersect(oldDocuments).Any(),
            "U05-target-bootstrap-new-exact-revisions-stable-binding");
        requests.Clear();
        try { await actions.ExecuteAsync(key, token); throw new InvalidOperationException("Lost update confirmation was not exercised."); }
        catch (LostUpdateResponse) { }
        var result = await Actions().RecoverByObservationAsync(key, token);
        var response = DeploymentIntents.DecodeReceipt(result.Receipt ?? throw new InvalidOperationException("Update observation did not confirm."));
        var current = await admin.AppsV1.ReadNamespacedDeploymentAsync(previous.Name, previous.Namespace, cancellationToken: token);
        var projection = DeploymentObjects.Read(current);
        _ = DeploymentObjects.Receipt(projection, intent, ConfirmationSource.Observation);
        if (wire.Response is null) throw new InvalidOperationException("Conditional PATCH response unavailable.");
        var apiResponse = DeploymentObjects.Receipt(wire.Response, intent, ConfirmationSource.Response);
        Check(result.Outcome == InternalActionOutcome.Confirmed && response.Source == ConfirmationSource.Observation && wire.Writes == 1 &&
            current.Metadata.Uid == previous.Uid && current.Spec.Template.Spec.Containers.Single(x => x.Name == intent.Target.Container).Image == targetImage &&
            wire.Patch is not null && wire.Patch.Any(x => x!["path"]!.GetValue<string>() == "/metadata/resourceVersion") &&
            wire.Patch.Any(x => x!["path"]!.GetValue<string>() == "/metadata/uid") && apiResponse.Uid == response.Uid &&
            apiResponse.KubernetesGeneration == response.KubernetesGeneration,
            "U06-conditional-configured-patch-response-readback-lost-response-recovery");
        Check(current.Metadata.Annotations["qualification.example/foreign"] == "retained" && current.Spec.Template.Spec.Containers[0].Name == "foreign" &&
            current.Spec.Template.Spec.Containers[0].Image == image &&
            current.Spec.Template.Spec.Containers[0].Command.SequenceEqual(new[] { "dotnet", "--info" }) &&
            current.Spec.Template.Spec.Containers.Single(x => x.Name == intent.Target.Container).ImagePullPolicy is not null,
            "U07-sidecar-foreign-metadata-and-api-defaulting-preserved");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token); startup.CancelAfter(TimeSpan.FromSeconds(35));
        var started = false;
        var sameRuntimeImage = false;
        while (!started || !target.Documents.Values.Select(Guid.Parse).All(requests.Contains))
        {
            var pods = await admin.CoreV1.ListNamespacedPodAsync(previous.Namespace,
                labelSelector: DeploymentIntents.ManagedKey + "=" + intent.ManagedWorkload.ToString("N"), cancellationToken: startup.Token);
            foreach (var pod in pods.Items.Where(x => x.Spec.ServiceAccountName == intent.Target.Runtime.ServiceAccount &&
                x.Spec.Containers.Any(c => c.Name == intent.Target.Container && c.Image == targetImage && c.Env?.Any(e => e.Name == RuntimeBootstrap.Variable && e.Value == intent.Target.Runtime.Bootstrap) == true) &&
                x.Status.ContainerStatuses?.Any(c => c.Name == intent.Target.Container && c.State?.Running is not null) == true))
            {
                var status = pod.Status.ContainerStatuses.Single(x => x.Name == intent.Target.Container);
                if (string.IsNullOrEmpty(status.ImageID) || status.ImageID.Split('@')[^1] != baselineImageId)
                    throw new InvalidOperationException("Updated runtime image identity differs from the pinned baseline.");
                sameRuntimeImage = true;
                using var stream = await admin.CoreV1.ReadNamespacedPodLogAsync(pod.Metadata.Name, previous.Namespace, container: intent.Target.Container, cancellationToken: startup.Token);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                var buffer = new char[4097];
                var length = await reader.ReadBlockAsync(buffer.AsMemory(), startup.Token);
                if (length > 4096) throw new InvalidOperationException("Updated consumer marker output exceeds bound.");
                started |= new string(buffer, 0, length).Contains("primary-started", StringComparison.Ordinal);
            }
            if (started && target.Documents.Values.Select(Guid.Parse).All(requests.Contains)) break;
            await Task.Delay(100, startup.Token);
        }
        await using var verify = await contexts.CreateDbContextAsync(token);
        var state = await verify.ManagedObjectStates.AsNoTracking().SingleAsync(x => x.DeploymentId == deploymentId, token);
        Check(sameRuntimeImage && state.Payload == result.Receipt && state.Generation == accepted.Generation.Value && state.OriginGeneration == baseline.Generation.Value &&
            (await verify.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == deploymentId, token)).AppliedGeneration == baseline.Generation.Value &&
            await verify.RuntimeConfigurationPermissions.CountAsync(x => x.BindingId == binding && x.Status == "Published", token) == 4 &&
            await verify.RuntimeConfigurationPermissions.CountAsync(x => x.BindingId == binding && oldDocuments.Contains(x.RevisionId) && x.Status == "Published", token) == oldDocuments.Length &&
            (await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.Id == binding, token)).Status == "Prepared",
            "U08-runtime-new-revision-delivery-old-permissions-and-Applied-boundary");
    }

    private static void Check(bool condition, string id)
    {
        if (!condition) throw new InvalidOperationException("Configured update case failed: " + id);
        Console.WriteLine("PASS configured-" + id);
    }
    private sealed class PatchEvidence
    {
        // Only Deployment PATCH requests sent through this UPDATE kernel client.
        // This counter does not observe other clients or all network traffic.
        internal int Writes;
        internal JsonArray? Patch;
        internal JsonObject? Response;
    }
    private sealed class CapturePatch(PatchEvidence evidence) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var update = request.Method == HttpMethod.Patch && request.RequestUri!.AbsolutePath.Contains("/deployments/", StringComparison.Ordinal);
            if (update) { evidence.Writes++; evidence.Patch = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsArray(); }
            var response = await base.SendAsync(request, token);
            if (update && response.StatusCode == HttpStatusCode.OK)
                evidence.Response = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!.AsObject();
            return response;
        }
    }
}
