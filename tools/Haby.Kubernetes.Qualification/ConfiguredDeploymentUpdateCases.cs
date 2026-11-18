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
using UpdateStage = ConfiguredDeploymentUpdateDiagnostics.Stage;
using UpdateState = ConfiguredDeploymentUpdateDiagnostics.State;

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
        var held = baselinePods.Items.Single(x => x.Metadata.DeletionTimestamp is null && x.Spec.Containers.Any(c => c.Name == previous.Projection.Container && c.Image == image));
        var holdDispatched = false;
        try
        {
            _ = await ConfiguredUpdateRetirement.HoldAsync(admin, previous, new V1PodList { Items = [held] }, () => holdDispatched = true, token);
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
                    { ["name"] = "foreign", ["image"] = image, ["command"] = new JsonArray("/bin/sh", "-c", "sleep 3600") }
                });
            await admin.AppsV1.PatchNamespacedDeploymentAsync(new(foreignPatch.ToJsonString(), V1Patch.PatchType.JsonPatch), previous.Name, previous.Namespace, cancellationToken: token);
            var wire = new PatchEvidence();
            var update = new InternalDeploymentAction(secrets, connection => new DeploymentKernel(connection.Binding,
                ct => KubernetesClientFactory.CreateFixture(connection, secrets, [new CapturePatch(wire)], ct)), update: true);
            var recordedUpdate = new ConfiguredDeploymentUpdateHandler(update,
                (phase, result) => Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Delegated(phase, result, wire.Writes)));
            var loseResponse = true;
            InternalDurableActions Actions() => new(contexts, [identity, create, recordedUpdate], services.GetServices<IProviderConfigurationCodec>(),
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
            InternalActionResult result;
            V1Deployment current;
            var stage = UpdateStage.Execute;
            void Begin(UpdateStage next)
            {
                stage = next;
                Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(stage, UpdateState.Start));
            }
            void Complete() => Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(stage, UpdateState.Completed));
            void Require(UpdateStage condition, Func<bool> predicate)
            {
                Begin(condition);
                var satisfied = predicate();
                Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(stage, satisfied ? UpdateState.Passed : UpdateState.Failed));
                if (!satisfied) throw new InvalidOperationException("Configured update diagnostic condition failed.");
            }
            try
            {
                Begin(UpdateStage.Execute);
                try
                {
                    var returned = await actions.ExecuteAsync(key, token);
                    Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(stage, UpdateState.Returned));
                    Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Result(stage, returned, wire.Writes));
                    Require(UpdateStage.ExpectedLoss, () => false);
                }
                catch (LostUpdateResponse)
                {
                    Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(UpdateStage.ExpectedLoss, UpdateState.Observed));
                    Complete();
                }
                Begin(UpdateStage.Recover);
                result = await Actions().RecoverByObservationAsync(key, token);
                Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Result(stage, result, wire.Writes));
                Complete();
                if (result.Outcome == InternalActionOutcome.NotConfirmed)
                    await DiagnoseUnconfirmed(admin, Actions(), accepted, subject, intent, wire, token);
                Require(UpdateStage.ReceiptPresence, () => result.Receipt is not null);
                Begin(UpdateStage.ReceiptDecode);
                var response = DeploymentIntents.DecodeReceipt(result.Receipt!);
                Complete();
                Begin(UpdateStage.Readback);
                current = await admin.AppsV1.ReadNamespacedDeploymentAsync(previous.Name, previous.Namespace, cancellationToken: token);
                Complete();
                Begin(UpdateStage.Projection);
                var projection = DeploymentObjects.Read(current);
                Complete();
                Begin(UpdateStage.ObservationReceipt);
                _ = DeploymentObjects.Receipt(projection, intent, ConfirmationSource.Observation);
                Complete();
                Require(UpdateStage.ApiResponsePresence, () => wire.Response is not null);
                Begin(UpdateStage.ApiResponseReceipt);
                var apiResponse = DeploymentObjects.Receipt(wire.Response!, intent, ConfirmationSource.Response);
                Complete();
                Require(UpdateStage.U06Outcome, () => result.Outcome == InternalActionOutcome.Confirmed);
                Require(UpdateStage.U06Source, () => response.Source == ConfirmationSource.Observation);
                Require(UpdateStage.U06PatchCount, () => wire.Writes == 1);
                Require(UpdateStage.U06SavedUid, () => current.Metadata.Uid == previous.Uid);
                Require(UpdateStage.U06TargetImage, () => current.Spec.Template.Spec.Containers.Single(x => x.Name == intent.Target.Container).Image == targetImage);
                Require(UpdateStage.U06PatchPresence, () => wire.Patch is not null);
                Require(UpdateStage.U06ResourceVersion, () => wire.Patch!.Any(x => x!["path"]!.GetValue<string>() == "/metadata/resourceVersion"));
                Require(UpdateStage.U06PatchUid, () => wire.Patch!.Any(x => x!["path"]!.GetValue<string>() == "/metadata/uid"));
                Require(UpdateStage.U06ResponseUid, () => apiResponse.Uid == response.Uid);
                Require(UpdateStage.U06ResponseGeneration, () => apiResponse.KubernetesGeneration == response.KubernetesGeneration);
                Console.WriteLine("PASS configured-U06-conditional-configured-patch-response-readback-lost-response-recovery");
            }
            catch
            {
                Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Failure(stage, wire.Writes));
                throw;
            }
            Check(current.Metadata.Annotations["qualification.example/foreign"] == "retained" && current.Spec.Template.Spec.Containers[0].Name == "foreign" &&
                current.Spec.Template.Spec.Containers[0].Image == image &&
                current.Spec.Template.Spec.Containers[0].Command.SequenceEqual(new[] { "/bin/sh", "-c", "sleep 3600" }) &&
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
            await verify.DisposeAsync();
            await ConfiguredUpdateRetirement.CompleteAsync(admin, contexts, Actions, accepted, previous, target, held, requests, token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (holdDispatched) await ConfiguredUpdateRetirement.ReleaseAsync(admin, previous.Namespace, held, cleanup.Token);
        }
    }

    private static async Task DiagnoseUnconfirmed(Kubernetes admin, InternalDurableActions actions, DurableChangeSetCommit accepted,
        ProviderInput.Workload subject, DeploymentIntent intent, PatchEvidence wire, CancellationToken token)
    {
        // Supplemental read-only diagnostics never replace the recovery result or its failure.
        Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Wire(wire.Status, wire.Writes));
        void Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate predicate, Action check) =>
            Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Inspect(predicate, check));
        Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate.Intent, () => DeploymentIntents.Validate(intent));
        try
        {
            var retained = await actions.ObserveRetainedIdentityAsync(accepted, subject, token);
            if (retained is null) Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Proof(ConfiguredDeploymentUpdateDiagnostics.Predicate.RuntimeAccount, false, null));
            else Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Result(UpdateStage.ProofRuntimeAccount, retained.Result, wire.Writes));
        }
        catch { Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Proof(ConfiguredDeploymentUpdateDiagnostics.Predicate.RuntimeAccount, false, null)); }
        if (wire.Response is not null)
            Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate.ResponseReceipt, () => DeploymentObjects.Receipt(wire.Response, intent, ConfirmationSource.Response));
        JsonObject current;
        try
        {
            // Exactly one additional Deployment GET, without writes or recovery retries.
            current = DeploymentObjects.Read(await admin.AppsV1.ReadNamespacedDeploymentAsync(intent.Name, intent.Namespace, cancellationToken: token));
            Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Proof(ConfiguredDeploymentUpdateDiagnostics.Predicate.Readback, true, null));
        }
        catch (WorkloadContractException error) { Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Proof(ConfiguredDeploymentUpdateDiagnostics.Predicate.Readback, false, error)); return; }
        catch { Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Proof(ConfiguredDeploymentUpdateDiagnostics.Predicate.Readback, false, null)); return; }
        Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate.Ownership, () => DeploymentObjects.Ownership(current, intent));
        Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate.Uid, () => DeploymentIntents.Need(intent.ExpectedUid is null ||
            DeploymentObjects.String(current, "metadata", "uid") == intent.ExpectedUid, "uid-conflict"));
        Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate.Baseline, () => DeploymentObjects.Matches(current, intent.LastConfirmed!));
        Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate.Target, () => DeploymentObjects.Matches(current, intent.Target));
        Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate.Marker, () => DeploymentIntents.Need(DeploymentObjects.HasMarkers(current, intent), "operation-marker"));
        Inspect(ConfiguredDeploymentUpdateDiagnostics.Predicate.Receipt, () => DeploymentObjects.Receipt(current, intent, ConfirmationSource.Observation));
        if (wire.Status == 422 && wire.Patch is not null)
        {
            var bootstrapTest = wire.Patch.SingleOrDefault(x => x?["op"]?.GetValue<string>() == "test" &&
                x["path"]?.GetValue<string>() is { } path && ConfiguredDeploymentPatchStatus.TestPathCategory(path) == "BootstrapValueTest");
            if (bootstrapTest is not null)
            {
                // Two diagnostic requests in this same contour. Server dry-run only:
                // empty control and the EXACT generated test, no mutation operations.
                // Neither request goes through/retries the UPDATE kernel client.
                async Task Probe(ConfiguredDeploymentPatchStatus.ProbeCase probe, JsonArray operations)
                {
                    int? status = null;
                    try
                    {
                        using var response = await admin.AppsV1.PatchNamespacedDeploymentWithHttpMessagesAsync(
                            new V1Patch(DeploymentPatchTransport.Serialize(operations), V1Patch.PatchType.JsonPatch), intent.Name, intent.Namespace,
                            dryRun: "All", cancellationToken: token);
                        status = (int)response.Response.StatusCode;
                    }
                    catch (k8s.Autorest.HttpOperationException error) { status = (int?)error.Response?.StatusCode; }
                    catch { }
                    Console.WriteLine(ConfiguredDeploymentPatchStatus.Probe(probe, status));
                }
                await Probe(ConfiguredDeploymentPatchStatus.ProbeCase.Control, []);
                await Probe(ConfiguredDeploymentPatchStatus.ProbeCase.BootstrapValueTest, [bootstrapTest.DeepClone()]);
            }
        }
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
        internal int? Status;
    }
    private sealed class CapturePatch(PatchEvidence evidence) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var update = request.Method == HttpMethod.Patch && request.RequestUri!.AbsolutePath.Contains("/deployments/", StringComparison.Ordinal);
            if (update) { evidence.Writes++; evidence.Status = null; evidence.Patch = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsArray(); }
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, token); }
            catch
            {
                if (update) Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Wire(null, evidence.Writes));
                throw;
            }
            if (update)
            {
                evidence.Status = (int)response.StatusCode;
                Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Wire(evidence.Status, evidence.Writes));
                if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    try
                    {
                        // Buffer without changing the content consumed by the SDK. Never
                        // retain/publish raw Status text or arbitrary message/field values.
                        await response.Content.LoadIntoBufferAsync(131072, token);
                        foreach (var record in ConfiguredDeploymentPatchStatus.Classify(response,
                            await response.Content.ReadAsStringAsync(token), evidence.Patch!)) Console.WriteLine(record);
                    }
                    catch { Console.WriteLine("Q243 configured update rejection Shape unknown"); }
                }
            }
            if (update && response.StatusCode == HttpStatusCode.OK)
                evidence.Response = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!.AsObject();
            return response;
        }
    }
}
