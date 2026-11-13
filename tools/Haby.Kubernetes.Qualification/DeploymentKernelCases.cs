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
        var ns = await admin.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new() { Name = name, Labels = new Dictionary<string, string> { ["haby.dev/qualification"] = name } } }, cancellationToken: token);
        try
        {
            using var secrets = new FixtureSecrets(environment);
            var cert = secrets.Add(File.ReadAllBytes(Path.Combine(directory, "admin.crt")));
            var key = secrets.Add(File.ReadAllBytes(Path.Combine(directory, "admin.key")));
            var connection = new KubernetesConnection(binding, endpoint, File.ReadAllText(Path.Combine(directory, "ca.crt")), null, cert, key);
            DeploymentKernel Kernel(KubernetesConnection? c = null, params DelegatingHandler[] handlers) => new(binding, ct => KubernetesClientFactory.CreateFixture(c ?? connection, secrets, handlers, ct));
            DeploymentIntent Intent(string objectName) => new(1, binding, "api", "main", "standard", 1, name, objectName,
                Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
                new("app", "registry.k8s.io/pause:3.10", 1, "RollingUpdate", "25%", "25%", 0, 600,
                    ImmutableSortedDictionary<string, string>.Empty.Add("pool.example/role", "api"), ["registry"]), null, null);
            async Task<V1Deployment> Read(DeploymentIntent i) => await admin.AppsV1.ReadNamespacedDeploymentAsync(i.Name, name, cancellationToken: token);
            async Task Mutate(DeploymentIntent i, string patch) => await admin.AppsV1.PatchNamespacedDeploymentAsync(new V1Patch(patch, V1Patch.PatchType.JsonPatch), i.Name, name, cancellationToken: token);
            var i = Intent("target");
            var created = await Kernel().ExecuteAsync(i, token);
            Check(created is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 1, Receipt.Source: ConfirmationSource.Response }, "K01-create-mtls");
            var occupied = await Kernel().ExecuteAsync(i, token);
            Check(occupied is { Outcome: KernelOutcome.Conflict, WriteDispatches: 0 }, "K02-occupied-no-adoption");
            var foreign = i with { Owner = Guid.NewGuid() };
            Check((await Kernel().ObserveAsync(foreign, token)) is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 }, "K03-foreign-owner");
            await Mutate(i, """[{"op":"add","path":"/metadata/annotations/unrelated","value":"preserve"},{"op":"add","path":"/spec/template/spec/containers/0","value":{"name":"injected","image":"registry.k8s.io/pause:3.10"}},{"op":"add","path":"/spec/template/spec/nodeSelector/injected","value":"preserve"},{"op":"add","path":"/spec/template/spec/imagePullSecrets/0","value":{"name":"injected"}}]""");
            var update = i with
            {
                ExpectedUid = created.Receipt!.Uid,
                LastConfirmed = i.Target,
                TargetGeneration = 2,
                Operation = Guid.NewGuid(),
                Target = i.Target with { Image = "registry.k8s.io/pause:3.9", Replicas = 2, NodeSelector = ImmutableSortedDictionary<string, string>.Empty, ImagePullSecrets = [] }
            };
            var updated = await Kernel().ExecuteAsync(update, token); var live = await Read(i);
            Check(updated is { Outcome: KernelOutcome.Confirmed, WriteDispatches: 1 } && updated.Receipt!.Uid == created.Receipt.Uid &&
                live.Spec.Template.Spec.Containers.Single(c => c.Name == "app").Image == update.Target.Image &&
                live.Spec.Template.Spec.Containers.Single(c => c.Name == "injected").Image == "registry.k8s.io/pause:3.10" &&
                live.Metadata.Annotations["unrelated"] == "preserve" && live.Spec.Template.Spec.NodeSelector.Count == 1 &&
                live.Spec.Template.Spec.NodeSelector["injected"] == "preserve" && live.Spec.Template.Spec.ImagePullSecrets.Single().Name == "injected", "K04-keyed-update-preservation-removal");
            Check(!live.Spec.Template.Metadata.Annotations?.ContainsKey(OperationKey) == true || live.Spec.Template.Metadata.Annotations is null, "K05-no-attempt-template-annotation");
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
            var replacementIntent = Intent("replacement"); var replacement = await Kernel().ExecuteAsync(replacementIntent, token);
            await admin.AppsV1.DeleteNamespacedDeploymentAsync(replacementIntent.Name, name, new V1DeleteOptions { Preconditions = new() { Uid = replacement.Receipt!.Uid }, PropagationPolicy = "Orphan" }, cancellationToken: token);
            for (var n = 0; n < 50; n++)
            {
                try { await Read(replacementIntent); await Task.Delay(100, token); }
                catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound) { break; }
            }
            // An administrator replacement has identical markers/projection but a different UID.
            await admin.AppsV1.CreateNamespacedDeploymentAsync(DeploymentObjects.Create(replacementIntent), name, cancellationToken: token);
            var replacementUpdate = replacementIntent with { ExpectedUid = replacement.Receipt.Uid, LastConfirmed = replacementIntent.Target };
            Check((await Kernel().ObserveAsync(replacementUpdate, token)) is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 }, "K14-replacement-UID-no-success");
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
                await admin.AppsV1.DeleteNamespacedDeploymentAsync(i.Name, name, new V1DeleteOptions { Preconditions = new() { Uid = created.Receipt.Uid }, PropagationPolicy = "Orphan" }, cancellationToken: token);
                for (var n = 0; n < 50; n++)
                {
                    try { await Read(i); await Task.Delay(100, token); }
                    catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound) { break; }
                }
                await admin.AppsV1.CreateNamespacedDeploymentAsync(DeploymentObjects.Create(next), name, cancellationToken: token);
            });
            var uidRaced = await Kernel(null, uidRace).ExecuteAsync(next, token);
            var afterUidRace = await Read(i);
            Check(uidRaced is { Outcome: KernelOutcome.Conflict, WriteDispatches: 1 } && uidRace.Dispatches == 1 &&
                afterUidRace.Metadata.Uid != created.Receipt.Uid && afterUidRace.Spec.Replicas == next.Target.Replicas &&
                (await Kernel().ObserveAsync(next, token)) is { Outcome: KernelOutcome.NotConfirmed, WriteDispatches: 0 }, "K24-real-UID-race-replacement-preserved");
            Console.WriteLine("Kernel cases: 24; stateless API object evidence only; no durable recovery or readiness.");
        }
        finally
        {
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
        Console.WriteLine((value ? "PASS " : "FAIL ") + "kernel-" + id); if (!value) throw new InvalidOperationException("Kernel assertion failed.");
    }
    private sealed class CountHandler : DelegatingHandler
    {
        internal int Writes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { if (request.Method != HttpMethod.Get) Writes++; return base.SendAsync(request, token); }
    }
    private sealed class AcceptedLossHandler : DelegatingHandler
    {
        internal int Dispatches { get; private set; }
        internal string? AcceptedUid { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post) return await base.SendAsync(request, token);
            Dispatches++; using var response = await base.SendAsync(request, token);
            if (response.StatusCode != HttpStatusCode.Created) throw new InvalidOperationException("Expected accepted create.");
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
