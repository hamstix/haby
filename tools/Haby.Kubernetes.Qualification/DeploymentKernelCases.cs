using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using static Haby.Kubernetes.DeploymentIntents;

internal static class DeploymentKernelCases
{
    internal static async Task Run(Kubernetes admin, Uri endpoint, string directory, CancellationToken token)
    {
        var environment = EnvironmentRecordId.New();
        var binding = new KubernetesBinding(environment.ToString(), ProviderInstanceId.New().ToString(), 1, "haby.kubernetes/v1");
        var name = "haby200-" + Guid.NewGuid().ToString("N")[..12];
        Console.WriteLine("Q243 kernel namespace-create start");
        var ns = await admin.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new() { Name = name, Labels = new Dictionary<string, string> { ["haby.dev/qualification"] = name } } }, cancellationToken: token);
        Console.WriteLine("Q243 kernel namespace-created completed");
        try
        {
            using var secrets = new FixtureSecrets(environment);
            var cert = secrets.Add(File.ReadAllBytes(Path.Combine(directory, "admin.crt")));
            var key = secrets.Add(File.ReadAllBytes(Path.Combine(directory, "admin.key")));
            var connection = new KubernetesConnection(binding, endpoint, File.ReadAllText(Path.Combine(directory, "ca.crt")), null, cert, key);
            DeploymentKernel Kernel(KubernetesConnection? c = null, params DelegatingHandler[] handlers) => new(binding, ct => KubernetesClientFactory.CreateFixture(c ?? connection, secrets, handlers, ct));
            DeploymentIntent Intent(string objectName) => new(4, binding, "api", "main", "standard", 1, name, objectName,
                Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
                new("app", "registry.k8s.io/pause:3.10", 1, "RollingUpdate", "25%", "25%", 0, 600,
                    ImmutableSortedDictionary<string, string>.Empty.Add("pool.example/role", "api"), ["registry"], ImmutableSortedDictionary<string, Haby.Kubernetes.Contracts.ContainerPort>.Empty, new(null, null, null),
                    Resources: new(new("0.250", "134217728"), new("1", "1Gi"))), null, null);
            async Task<V1Deployment> Read(DeploymentIntent i) => await admin.AppsV1.ReadNamespacedDeploymentAsync(i.Name, name, cancellationToken: token);
            async Task Mutate(DeploymentIntent i, string patch) => await admin.AppsV1.PatchNamespacedDeploymentAsync(new V1Patch(patch, V1Patch.PatchType.JsonPatch), i.Name, name, cancellationToken: token);
            var i = Intent("target");
            i = i with { Target = i.Target with
            {
                Ports = i.Target.Ports.Add("web", new(8080, "TCP")).Add("dns", new(5353, "UDP")),
                Probes = new(new("web", "/health", "HTTP", 0, 10, 1, 1, 3), new("web", null, null, 0, 10, 1, 1, 3),
                    new("web", "/ready", "HTTPS", 0, 10, 1, 2, 5))
            } };
            Console.WriteLine("Q243 kernel K01 start");
            var created = await Kernel().ExecuteAsync(i, token);
            ResultEvidence(created);
            Check(created is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 1, Receipt.Source: ConfirmationSource.Response }, "K01-create-mtls");
            Console.WriteLine("Q243 kernel readback start");
            var resourceReadback = (await Read(i)).Spec.Template.Spec.Containers.Single(c => c.Name == "app").Resources;
            if (!DeploymentResources.Equivalent("250m", resourceReadback.Requests["cpu"].ToString()) ||
                !DeploymentResources.Equivalent("128Mi", resourceReadback.Requests["memory"].ToString()) ||
                !DeploymentResources.Equivalent("1", resourceReadback.Limits["cpu"].ToString()) ||
                !DeploymentResources.Equivalent("1Gi", resourceReadback.Limits["memory"].ToString()))
                throw new InvalidOperationException("Exact resource readback failed.");
            Console.WriteLine("Q243 kernel readback completed");
            var portReadback = (await Read(i)).Spec.Template.Spec.Containers.Single(c => c.Name == "app");
            Check(portReadback.Ports.Count == 2 && portReadback.Ports.Single(p => p.Name == "web").ContainerPort == 8080 &&
                portReadback.Ports.Single(p => p.Name == "web").Protocol == "TCP" && portReadback.Ports.Single(p => p.Name == "dns").Protocol == "UDP" &&
                portReadback.StartupProbe.HttpGet.Port.Value == "web" && portReadback.StartupProbe.HttpGet.Path == "/health" &&
                portReadback.StartupProbe.HttpGet.Scheme == "HTTP" && portReadback.StartupProbe.PeriodSeconds == 10 &&
                portReadback.LivenessProbe.TcpSocket.Port.Value == "web" && portReadback.LivenessProbe.TimeoutSeconds == 1 &&
                portReadback.ReadinessProbe.HttpGet.Scheme == "HTTPS" && portReadback.ReadinessProbe.SuccessThreshold == 2 &&
                portReadback.ReadinessProbe.FailureThreshold == 5, "K26-ports-probes-API-defaults-readback");
            var webIndex = portReadback.Ports.ToList().FindIndex(p => p.Name == "web");
            await Mutate(i, $$"""
                [{"op":"remove","path":"/spec/template/spec/containers/0/ports/{{webIndex}}/protocol"},
                 {"op":"remove","path":"/spec/template/spec/containers/0/startupProbe/httpGet/scheme"},
                 {"op":"remove","path":"/spec/template/spec/containers/0/startupProbe/periodSeconds"},
                 {"op":"remove","path":"/spec/template/spec/containers/0/startupProbe/timeoutSeconds"},
                 {"op":"remove","path":"/spec/template/spec/containers/0/startupProbe/successThreshold"},
                 {"op":"remove","path":"/spec/template/spec/containers/0/startupProbe/failureThreshold"}]
                """);
            var defaulted = (await Read(i)).Spec.Template.Spec.Containers.Single(c => c.Name == "app");
            var rootIntent = Intent("root-default") with { Target = i.Target with
            { Probes = new(i.Target.Probes.Startup! with { Path = "/" }, null, null) } };
            var rootCreated = await Kernel().ExecuteAsync(rootIntent, token);
            if (rootCreated.Outcome != KernelOutcome.Confirmed) throw new InvalidOperationException("Root default fixture creation failed.");
            await Mutate(rootIntent, """[{"op":"remove","path":"/spec/template/spec/containers/0/startupProbe/httpGet/path"}]""");
            var rootDefault = (await Read(rootIntent)).Spec.Template.Spec.Containers.Single(c => c.Name == "app");
            Check(defaulted.Ports.Single(p => p.Name == "web").Protocol == "TCP" && defaulted.StartupProbe.HttpGet.Scheme == "HTTP" &&
                defaulted.StartupProbe.PeriodSeconds == 10 && defaulted.StartupProbe.TimeoutSeconds == 1 && defaulted.StartupProbe.SuccessThreshold == 1 &&
                defaulted.StartupProbe.FailureThreshold == 3 && rootDefault.StartupProbe.HttpGet.Path == "/" &&
                (await Kernel().ObserveAsync(rootIntent, token)) is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 0 } &&
                (await Kernel().ObserveAsync(i, token)) is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 0 },
                "K29-server-defaulted-protocol-and-probe-fields-confirm");
            var occupied = await Kernel().ExecuteAsync(i, token);
            Check(occupied is { Outcome: KernelOutcome.Conflict, WriteDispatches: 0 }, "K02-occupied-no-adoption");
            var foreign = i with { Owner = Guid.NewGuid() };
            Check((await Kernel().ObserveAsync(foreign, token)) is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 }, "K03-foreign-owner");
            await Mutate(i, """[{"op":"add","path":"/spec/paused","value":true},{"op":"add","path":"/metadata/annotations/unrelated","value":"preserve"},{"op":"add","path":"/spec/template/spec/containers/0","value":{"name":"injected","image":"registry.k8s.io/pause:3.10"}},{"op":"add","path":"/spec/template/spec/nodeSelector/injected","value":"preserve"},{"op":"add","path":"/spec/template/spec/imagePullSecrets/0","value":{"name":"injected"}}]""");
            // Arrange a quiet API object before the positive field-ownership case. This is
            // fixture administration, not Workload pause support, Pod readiness or write retry.
            await Mutate(i, """[{"op":"add","path":"/spec/template/spec/containers/1/ports/0","value":{"name":"foreign","containerPort":9000,"protocol":"TCP"}}]""");
            string? previousVersion = null; var stableReads = 0;
            for (var n = 0; n < 50 && stableReads < 3; n++)
            {
                var version = (await Read(i)).Metadata.ResourceVersion;
                stableReads = version == previousVersion ? stableReads + 1 : 0;
                previousVersion = version; await Task.Delay(100, token);
            }
            if (stableReads < 3) throw new InvalidOperationException("Fixture object did not become quiet.");
            var update = i with
            {
                ExpectedUid = created.Receipt!.Uid,
                LastConfirmed = i.Target,
                TargetGeneration = 2,
                Operation = Guid.NewGuid(),
                Target = i.Target with { Image = "registry.k8s.io/pause:3.9", Replicas = 2, NodeSelector = ImmutableSortedDictionary<string, string>.Empty, ImagePullSecrets = [], Resources = null,
                    Ports = i.Target.Ports.Remove("dns").SetItem("web", new(8081, "TCP")),
                    Probes = new(new("web", null, null, 0, 10, 1, 1, 3), null, i.Target.Probes.Readiness) }
            };
            var updated = await Kernel().ExecuteAsync(update, token); ResultEvidence(updated); var live = await Read(i);
            Console.WriteLine($"Kernel K04 result: outcome={updated.Outcome}; code={updated.Code}; dispatches={updated.WriteDispatches}; uid-preserved={live.Metadata.Uid == created.Receipt.Uid}; paused-preserved={live.Spec.Paused == true}; containers={live.Spec.Template.Spec.Containers.Count}; nodes={live.Spec.Template.Spec.NodeSelector?.Count ?? 0}; pulls={live.Spec.Template.Spec.ImagePullSecrets?.Count ?? 0}");
            Check(updated is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 1 } && updated.Receipt!.Uid == created.Receipt.Uid &&
                live.Spec.Template.Spec.Containers.Single(c => c.Name == "app").Image == update.Target.Image &&
                live.Spec.Template.Spec.Containers.Single(c => c.Name == "injected").Image == "registry.k8s.io/pause:3.10" &&
                live.Metadata.Annotations["unrelated"] == "preserve" && live.Spec.Paused == true && live.Spec.Template.Spec.NodeSelector?.Count == 1 &&
                live.Spec.Template.Spec.NodeSelector["injected"] == "preserve" && live.Spec.Template.Spec.ImagePullSecrets is { Count: 1 } && live.Spec.Template.Spec.ImagePullSecrets.Single().Name == "injected", "K04-keyed-update-preservation-removal");
            Check(!live.Spec.Template.Metadata.Annotations?.ContainsKey(OperationKey) == true || live.Spec.Template.Metadata.Annotations is null, "K05-no-attempt-template-annotation");
            var patchedMain = live.Spec.Template.Spec.Containers.Single(c => c.Name == "app");
            Check(patchedMain.Ports.Count == 2 && patchedMain.Ports.Single(p => p.Name == "foreign").ContainerPort == 9000 &&
                patchedMain.Ports.Single(p => p.Name == "web").ContainerPort == 8081 && patchedMain.Ports.All(p => p.Name != "dns") &&
                patchedMain.StartupProbe.HttpGet is null && patchedMain.StartupProbe.TcpSocket.Port.Value == "web" &&
                patchedMain.LivenessProbe is null, "K27-owned-probe-action-port-change-removal-preserves-foreign");
            var lostPatch = update with { LastConfirmed = update.Target, Operation = Guid.NewGuid() };
            using (var patchLoss = new AcceptedLossHandler(patch: true))
            {
                var lost = await Kernel(null, patchLoss).ExecuteAsync(lostPatch, token);
                using var counter = new CountHandler();
                var recovered = await Kernel(null, counter).ObserveAsync(Decode(Encode(lostPatch)), token);
                Check(lost is { Outcome: KernelOutcome.Uncertain, WriteDispatches: 1 } && patchLoss.Dispatches == 1 &&
                    recovered is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 0 } && counter.Writes == 0 &&
                    recovered.Receipt!.Uid == created.Receipt.Uid, "K28-uncertain-patch-observes-exact-probes-without-second-write");
            }
            var next = update with { LastConfirmed = update.Target, Operation = Guid.NewGuid(), TargetGeneration = 3, Target = update.Target with { Replicas = 3 } };
            await Mutate(i, """[{"op":"add","path":"/spec/replicas","value":9}]""");
            Check((await Kernel().ExecuteAsync(next, token)) is { Outcome: KernelOutcome.Conflict, WriteDispatches: 0 }, "K06-owned-drift");
            await Mutate(i, """[{"op":"add","path":"/spec/replicas","value":2}]""");
            using var race = new RaceHandler(async () => await Mutate(i, """[{"op":"add","path":"/metadata/annotations/race","value":"preserve"}]"""));
            var raced = await Kernel(null, race).ExecuteAsync(next, token);
            Check(race.Dispatches == 1 && raced is { Outcome: KernelOutcome.Conflict, WriteDispatches: 1 } &&
                (await Read(i)).Metadata.Annotations["race"] == "preserve" && (await Read(i)).Spec.Replicas == 2, "K07-real-RV-race-ambiguous-422-no-retry");
            var wrongUid = next with { ExpectedUid = "foreign-uid", LastConfirmed = next.Target, Operation = Guid.NewGuid() };
            Check((await Kernel().ExecuteAsync(wrongUid, token)) is { Outcome: KernelOutcome.Conflict, WriteDispatches: 0 }, "K08-saved-UID-refusal");
            var uncertainIntent = Intent("uncertain"); using var loss = new AcceptedLossHandler();
            var uncertain = await Kernel(null, loss).ExecuteAsync(uncertainIntent, token);
            Check(uncertain is { Outcome: KernelOutcome.Uncertain, WriteDispatches: 1, Receipt: null } && loss.Dispatches == 1 && loss.AcceptedUid is not null, "K09-one-accepted-response-loss");
            var reloaded = Decode(Encode(uncertainIntent)); using var observeCounter = new CountHandler();
            var observation = await Kernel(null, observeCounter).ObserveAsync(reloaded, token);
            Check(observation is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 0, Receipt.Source: ConfirmationSource.Observation } &&
                observation.Receipt.Uid == loss.AcceptedUid && observation.Receipt.Operation == uncertainIntent.Operation && observeCounter.Writes == 0 &&
                uncertain.Outcome == KernelOutcome.Uncertain, "K10-stateless-reload-observe-zero-writes");
            await Mutate(uncertainIntent, """[{"op":"remove","path":"/metadata/annotations/haby.dev~1write-operation"}]""");
            Check((await Kernel().ObserveAsync(reloaded, token)) is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 }, "K11-missing-marker-no-success");
            await Mutate(uncertainIntent, """[{"op":"add","path":"/metadata/annotations/haby.dev~1write-operation","value":"foreign"}]""");
            Check((await Kernel().ObserveAsync(reloaded, token)) is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 }, "K12-wrong-marker-no-success");
            Check((await Kernel().ObserveAsync(Intent("absent"), token)) is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 }, "K13-absence-no-replay");
            var replacementPhase = "initial-create";
            try
            {
                K14Phase(replacementPhase, "start");
                var replacementIntent = Intent("replacement"); var replacement = await Kernel().ExecuteAsync(replacementIntent, token);
                ResultEvidence(replacement, replacementPhase);
                // An administrator replacement has identical markers/projection but a different UID.
                await DeploymentKernelReplacement.RunAsync(admin, name, replacementIntent.Name, replacement.Receipt!.Uid,
                    DeploymentObjects.Create(replacementIntent), token,
                    (phase, state) => { replacementPhase = phase; K14Phase(phase, state); }, K14Poll);
                replacementPhase = "old-uid-observe"; K14Phase(replacementPhase, "start");
                var replacementUpdate = replacementIntent with { ExpectedUid = replacement.Receipt.Uid, LastConfirmed = replacementIntent.Target };
                var replacementObservation = await Kernel().ObserveAsync(replacementUpdate, token);
                ResultEvidence(replacementObservation, replacementPhase);
                replacementPhase = "assertion"; K14Phase(replacementPhase, "start");
                var replacementMatches = replacementObservation is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 };
                K14Phase(replacementPhase, replacementMatches ? "passed" : "failed");
                Check(replacementMatches, "K14-replacement-UID-no-success");
            }
            catch (Exception failure)
            {
                // A diagnostic writer failure cannot replace the original K14 exception.
                try
                {
                    K14Failure(replacementPhase, failure);
                }
                catch { }
                throw;
            }
            using var unavailable = new UnavailableHandler();
            Check((await Kernel(null, unavailable).ObserveAsync(reloaded, token)) is { Outcome: KernelOutcome.ObservationUnavailable, WriteDispatches: 0 }, "K15-unavailable-not-nonexecution");
            // Scoped bearer identity has a namespaced Deployment Role, never cluster-wide authority.
            await admin.CoreV1.CreateNamespacedServiceAccountAsync(new V1ServiceAccount { Metadata = new() { Name = "kernel" } }, name, cancellationToken: token);
            await admin.RbacAuthorizationV1.CreateNamespacedRoleAsync(new V1Role { Metadata = new() { Name = "kernel" }, Rules = [new V1PolicyRule { ApiGroups = ["apps"], Resources = ["deployments"], Verbs = ["get", "create", "patch"] }] }, name, cancellationToken: token);
            await admin.RbacAuthorizationV1.CreateNamespacedRoleBindingAsync(new V1RoleBinding { Metadata = new() { Name = "kernel" }, RoleRef = new() { ApiGroup = "rbac.authorization.k8s.io", Kind = "Role", Name = "kernel" }, Subjects = [new Rbacv1Subject { Kind = "ServiceAccount", Name = "kernel", NamespaceProperty = name }] }, name, cancellationToken: token);
            var issued = await admin.CoreV1.CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest { Spec = new V1TokenRequestSpec { Audiences = [], ExpirationSeconds = 600 } }, "kernel", name, cancellationToken: token);
            var bearerRef = secrets.Add(Encoding.UTF8.GetBytes(issued.Status.Token));
            var bearerConnection = connection with { BearerToken = bearerRef, ClientCertificate = null, ClientPrivateKey = null };
            Check((await Kernel(bearerConnection).ExecuteAsync(Intent("bearer"), token)) is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 1 }, "K16-create-bearer");
            var invalidBearer = bearerConnection with { BearerToken = secrets.Add("invalid-token"u8.ToArray()) };
            Check((await Kernel(invalidBearer).ExecuteAsync(Intent("unauthorized"), token)) is { Outcome: KernelOutcome.Conflict, WriteDispatches: 0, Code: "api-access-denied" }, "K17-invalid-bearer");
            var production = new DeploymentKernel(connection, secrets);
            var productionResult = await production.ExecuteAsync(Intent("offline-ca"), token);
            Check(productionResult is { Outcome: KernelOutcome.Refused, WriteDispatches: 0, Code: "connection-unavailable" }, "K18-production-Online-revocation-no-fallback");
            var systemTrust = new DeploymentKernel(connection with { ServerCaPem = null }, secrets);
            Check((await systemTrust.ExecuteAsync(Intent("untrusted-system"), token)) is { Outcome: KernelOutcome.Refused, WriteDispatches: 0 }, "K19-production-system-trust-refusal");
            using var wrongKey = RSA.Create(2048);
            var keyMismatch = connection with { ClientPrivateKey = secrets.Add(Encoding.UTF8.GetBytes(wrongKey.ExportPkcs8PrivateKeyPem())) };
            Check((await Kernel(keyMismatch).ExecuteAsync(Intent("wrong-key"), token)) is { Outcome: KernelOutcome.Refused, WriteDispatches: 0 }, "K20-client-key-mismatch");
            Check(!Encode(uncertainIntent).Contains(issued.Status.Token, StringComparison.Ordinal) && !Encode(observation.Receipt!).Contains(issued.Status.Token, StringComparison.Ordinal) && secrets.Resolutions > 0, "K21-scoped-secret-exclusion");
            var wrongName = new DeploymentKernel(binding, ct => KubernetesClientFactory.CreateFixture(connection, secrets, [], ct, "wrong.haby-qualification.invalid"));
            Check((await wrongName.ExecuteAsync(Intent("wrong-name"), token)) is { Outcome: KernelOutcome.Refused, WriteDispatches: 0 }, "K22-new-factory-hostname-refusal");
            using var otherRootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=foreign-root", otherRootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var otherRoot = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            Check((await Kernel(connection with { ServerCaPem = otherRoot.ExportCertificatePem() }).ExecuteAsync(Intent("foreign-root"), token)) is { Outcome: KernelOutcome.Refused, WriteDispatches: 0 }, "K23-new-factory-custom-root-refusal");
            using var uidRace = new RaceHandler(async () =>
            {
                var phase = "uid-delete";
                try
                {
                    await DeploymentKernelReplacement.RunAsync(admin, name, i.Name, created.Receipt.Uid,
                        DeploymentObjects.Create(next), token,
                        (step, state) => { phase = step; ReplacementPhase("k24", step, state); },
                        (absent, attempts) => ReplacementPoll("k24", absent, attempts));
                }
                catch (Exception failure)
                {
                    try { ReplacementFailure("k24", phase, failure); } catch { }
                    throw;
                }
            });
            var uidRaced = await Kernel(null, uidRace).ExecuteAsync(next, token);
            var afterUidRace = await Read(i);
            Check(uidRaced is { Outcome: KernelOutcome.Conflict, WriteDispatches: 1 } && uidRace.Dispatches == 1 &&
                afterUidRace.Metadata.Uid != created.Receipt.Uid && afterUidRace.Spec.Replicas == next.Target.Replicas &&
                (await Kernel().ObserveAsync(next, token)) is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 }, "K24-real-UID-race-replacement-preserved");
            Console.WriteLine("Q243 kernel K25 start");
            var onlyLimit = Intent("limit-only");
            onlyLimit = onlyLimit with { Target = onlyLimit.Target with { Resources = new(null, new("1", "1Gi")) } };
            var limitResult = await Kernel().ExecuteAsync(onlyLimit, token);
            ResultEvidence(limitResult);
            var limitReadback = (await Read(onlyLimit)).Spec.Template.Spec.Containers.Single(c => c.Name == "app").Resources;
            Check(limitResult is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 1 } &&
                limitResult.Receipt!.Projection.Resources!.Requests is null &&
                (limitReadback.Requests is null || !limitReadback.Requests.ContainsKey("cpu") && !limitReadback.Requests.ContainsKey("memory")) &&
                DeploymentResources.Equivalent("1", limitReadback.Limits["cpu"].ToString()) &&
                DeploymentResources.Equivalent("1Gi", limitReadback.Limits["memory"].ToString()), "K25-limit-only-template-absence");
            Console.WriteLine("Kernel cases: 25; stateless API object evidence only; no durable recovery or readiness.");
        }
        finally
        {
            Console.WriteLine("Q243 kernel cleanup-namespace start");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var current = await admin.CoreV1.ReadNamespaceAsync(name, cancellationToken: cleanup.Token);
            Check(current.Metadata.Uid == ns.Metadata.Uid && current.Metadata.Labels["haby.dev/qualification"] == name, "cleanup-ownership");
            await admin.CoreV1.DeleteNamespaceAsync(name, new V1DeleteOptions { Preconditions = new() { Uid = ns.Metadata.Uid } }, cancellationToken: cleanup.Token);
            var absent = false;
            for (var n = 0; n < 100; n++)
            {
                try { await admin.CoreV1.ReadNamespaceAsync(name, cancellationToken: cleanup.Token); }
                catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound) { absent = true; break; }
                await Task.Delay(200, cleanup.Token);
            }
            Check(absent, "namespace-absence-after-UID-delete");
        }
    }
    private static void Check(bool value, string id)
    {
        var prefix = id.Split('-')[0];
        var stage = id switch
        {
            "cleanup-ownership" => "cleanup-ownership",
            "namespace-absence-after-UID-delete" => "cleanup-namespace",
            _ => prefix is "K01" or "K02" or "K03" or "K04" or "K05" or "K06" or "K07" or "K08" or "K09" or "K10" or "K11" or "K12" or "K13" or "K14" or "K15" or "K16" or "K17" or "K18" or "K19" or "K20" or "K21" or "K22" or "K23" or "K24" or "K25" or "K26" or "K27" or "K28" or "K29" ? prefix : "unknown"
        };
        Console.WriteLine($"Q243 kernel {stage} {(value ? "passed" : "failed")}");
        Console.WriteLine((value ? "PASS " : "FAIL ") + "kernel-" + id); if (!value) throw new InvalidOperationException("Kernel assertion failed.");
    }
    private static void K14Phase(string phase, string state) => ReplacementPhase("k14", phase, state);
    private static void K14Poll(bool observedAbsent, int attempts) => ReplacementPoll("k14", observedAbsent, attempts);
    private static void K14Failure(string phase, Exception failure) => ReplacementFailure("k14", phase, failure);
    private static void ReplacementPhase(string fixture, string phase, string state) => Console.WriteLine($"Q243 kernel {fixture} {phase} {state}");
    private static void ReplacementPoll(string fixture, bool observedAbsent, int attempts) =>
        Console.WriteLine($"Q243 kernel {fixture} absence-poll result {(observedAbsent ? "true" : "false")} {attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    private static void ReplacementFailure(string fixture, string phase, Exception failure) =>
        Console.WriteLine(DeploymentKernelReplacement.FailureEvidence(fixture, phase, failure));
    private static void ResultEvidence(KernelResult result, string? replacementPhase = null)
    {
        string[] codes = ["response-confirmed", "observation-confirmed", "evidence-not-confirmed", "response-unconfirmed", "invalid-input",
            "connection-unavailable", "dispatch-uncertain", "occupied-name", "owned-state-conflict", "uid-conflict", "api-conflict",
            "api-access-denied", "managed-object-missing", "action-budget", "action-canceled", "precondition-limit", "object-absent", "observation-unavailable"];
        var outcome = Enum.IsDefined(result.Outcome) ? result.Outcome.ToString() : "unknown";
        var code = codes.Contains(result.Code, StringComparer.Ordinal) ? result.Code : "unknown";
        var writes = result.WriteDispatches is >= 0 and <= 3 ? result.WriteDispatches.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
        Console.WriteLine(replacementPhase is null ? $"Q243 kernel result {outcome} {code} {writes}" :
            $"Q243 kernel k14 {replacementPhase} result {outcome} {code} {writes} {(result.Receipt is null ? "false" : "true")}");
    }
    private sealed class CountHandler : DelegatingHandler
    {
        internal int Writes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { if (request.Method != HttpMethod.Get) Writes++; return base.SendAsync(request, token); }
    }
    private sealed class AcceptedLossHandler(bool patch = false) : DelegatingHandler
    {
        internal int Dispatches { get; private set; }
        internal string? AcceptedUid { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != (patch ? HttpMethod.Patch : HttpMethod.Post)) return await base.SendAsync(request, token);
            Dispatches++; using var response = await base.SendAsync(request, token);
            if (response.StatusCode != (patch ? HttpStatusCode.OK : HttpStatusCode.Created)) throw new InvalidOperationException("Expected accepted write.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            AcceptedUid = json.RootElement.GetProperty("metadata"u8).GetProperty("uid"u8).GetString();
            throw new HttpRequestException("Injected accepted response loss.");
        }
    }
    private sealed class RaceHandler(Func<Task> race) : DelegatingHandler
    {
        internal int Dispatches { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Patch) { Dispatches++; if (Dispatches == 1) await race(); }
            return await base.SendAsync(request, token);
        }
    }
    private sealed class UnavailableHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new HttpRequestException("Injected observation unavailability.");
    }
    private sealed class FixtureSecrets(EnvironmentRecordId environment) : ISecretStore, IDisposable
    {
        private readonly Dictionary<SecretRef, byte[]> _values = []; internal int Resolutions { get; private set; }
        internal SecretRef Add(byte[] bytes) { var reference = new SecretRef(SecretId.New(), SecretVersionId.New()); _values.Add(reference, bytes); return reference; }
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition != new SecretDefinition(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator) || maximumBytes != 1_048_576) throw new InvalidOperationException("Invalid fixture secret scope.");
            Resolutions++; return ValueTask.FromResult(_values.TryGetValue(reference, out var value) && value.Length <= maximumBytes ? new SecretMaterial(value) : null);
        }
        public void Dispose() { foreach (var bytes in _values.Values) CryptographicOperations.ZeroMemory(bytes); }
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId owner, SecretRef reference, byte[] material, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
