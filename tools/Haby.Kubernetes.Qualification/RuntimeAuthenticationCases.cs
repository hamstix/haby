using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Kubernetes;
using k8s;
using k8s.Autorest;
using k8s.Models;

internal static class RuntimeAuthenticationCases
{
    internal static async Task Run(Kubernetes admin, ISecretStore secrets, IRuntimeIdentityStore store, Guid binding,
        Func<Task> revoke, CancellationToken token, IRuntimeIdentityStore? otherStore = null, Guid otherBinding = default,
        ISecretStore? otherSecrets = null, bool replaced = false, string scenario = "initial")
    {
        var trust = await store.ReadAsync(binding, token) ?? throw new InvalidOperationException("Runtime proof unavailable.");
        var original = RuntimeIdentityCodec.Receipt(trust.InventoryPayload);
        var account = original.Intent.Name; var ns = original.Intent.Namespace;
        var podName = "auth-" + Guid.NewGuid().ToString("N")[..12];
        V1Pod? pod = null; V1ServiceAccount? foreign = null;
        var attempts = new AttemptCounter();
        using var admission = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        RuntimeAuthentication Compose(Func<Task>? afterResponse = null, IRuntimeIdentityStore? selected = null)
        {
            var selectedSecrets = selected is null ? secrets : otherSecrets ?? secrets;
            var adapter = new KubernetesRuntimeAuthenticator(selectedSecrets, (connection, handlers, ct) =>
                KubernetesClientFactory.CreateFixture(connection, selectedSecrets, [.. handlers, new ReviewProbe(attempts, afterResponse)], ct));
            return new(selected ?? store, [adapter], admission, TimeProvider.System);
        }
        async Task<byte[]> Issue(string sa, string audience, bool bound)
        {
            var issued = await admin.CoreV1.CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest
            {
                Spec = new V1TokenRequestSpec
                {
                    Audiences = [audience],
                    ExpirationSeconds = 600,
                    BoundObjectRef = bound ? new V1BoundObjectReference { ApiVersion = "v1", Kind = "Pod", Name = pod!.Metadata.Name, Uid = pod.Metadata.Uid } : null
                }
            }, sa, ns, cancellationToken: token);
            return Encoding.ASCII.GetBytes(issued.Status.Token);
        }
        var lastResponseConfirmed = false;
        async Task<RuntimeAuthenticationOutcome> Authenticate(RuntimeAuthentication service, byte[] bytes, Guid? selectedBinding = null)
        {
            var before = attempts.Completed;
            try
            {
                var result = (await service.AuthenticateAsync(selectedBinding ?? binding, bytes, token)).Outcome;
                lastResponseConfirmed = attempts.Completed == before + 1;
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        bool IdentityRefused(RuntimeAuthenticationOutcome result)
        {
            Console.WriteLine($"Runtime refusal category [{scenario}]: {result}; successful HTTP response: {lastResponseConfirmed}");
            return lastResponseConfirmed && result is RuntimeAuthenticationOutcome.Unauthenticated or RuntimeAuthenticationOutcome.Unavailable;
        }
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("Runtime authentication qualification failed: " + label); Console.WriteLine($"PASS runtime-{scenario}-{label}"); }
        Exception? primary = null;
        try
        {
            pod = await admin.CoreV1.CreateNamespacedPodAsync(new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = podName },
                Spec = new V1PodSpec
                {
                    ServiceAccountName = account,
                    AutomountServiceAccountToken = false,
                    Containers = [new V1Container { Name = "app", Image = "registry.k8s.io/pause:3.10" }]
                }
            }, ns, cancellationToken: token);
            var service = Compose();
            if (replaced)
            {
                var actual = await admin.CoreV1.ReadNamespacedServiceAccountAsync(account, ns, cancellationToken: token);
                Check(actual.Metadata.Uid != original.Uid, "R10-actual-replaced-ServiceAccount-UID-differs-from-original-binding");
                Check(IdentityRefused(await Authenticate(service, await Issue(account, trust.Audience, true))),
                    "R11-real-new-credential-under-replaced-ServiceAccount-never-remaps-binding");
                return;
            }
            Check(await Authenticate(service, await Issue(account, trust.Audience, true)) == RuntimeAuthenticationOutcome.Authenticated, "R01-actual-persisted-preparation-real-Pod-bound-TokenReview");
            Check(await Authenticate(service, await Issue(account, trust.Audience, true)) == RuntimeAuthenticationOutcome.Authenticated, "R02-separate-fresh-bound-credential-same-binding-no-file-rotation-claim");
            Check(IdentityRefused(await Authenticate(service, await Issue(account, trust.Audience + ":foreign", true))), "R03-real-wrong-audience");
            Check(IdentityRefused(await Authenticate(service, await Issue(account, trust.Audience, false))), "R04-real-non-Pod-token");
            Check(IdentityRefused(await Authenticate(service, "invalid"u8.ToArray())), "R05-real-invalid-token");
            await ForeignNamespace(admin, service, binding, account, trust.Audience, scenario, attempts, token);
            if (otherStore is not null)
            {
                var bytes = await Issue(account, trust.Audience, true);
                try
                {
                    Check(IdentityRefused(await Authenticate(Compose(selected: otherStore), bytes, otherBinding)),
                        "R12-real-bound-token-cannot-authenticate-another-persisted-Provider-context");
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            var deletedCredential = await Issue(account, trust.Audience, true);
            try
            {
                await admin.CoreV1.DeleteNamespacedPodAsync(pod.Metadata.Name, ns, new V1DeleteOptions
                {
                    GracePeriodSeconds = 0,
                    Preconditions = new V1Preconditions { Uid = pod.Metadata.Uid }
                }, cancellationToken: token);
                pod = null;
                var elapsed = Stopwatch.StartNew(); RuntimeAuthenticationOutcome deleted;
                do
                {
                    var beforeResponse = attempts.Completed;
                    deleted = (await service.AuthenticateAsync(binding, deletedCredential, token)).Outcome;
                    lastResponseConfirmed = attempts.Completed == beforeResponse + 1;
                    if (deleted != RuntimeAuthenticationOutcome.Authenticated) break;
                    await Task.Delay(250, token);
                } while (elapsed.Elapsed < TimeSpan.FromSeconds(65));
                Check(IdentityRefused(deleted), "R13-real-deleted-bound-Pod-token-refused");
                Console.WriteLine("Runtime Pod deletion refusal observed milliseconds: " + elapsed.ElapsedMilliseconds);
            }
            finally { CryptographicOperations.ZeroMemory(deletedCredential); }
            pod = await admin.CoreV1.CreateNamespacedPodAsync(new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = podName },
                Spec = new V1PodSpec
                {
                    ServiceAccountName = account,
                    AutomountServiceAccountToken = false,
                    Containers = [new V1Container { Name = "app", Image = "registry.k8s.io/pause:3.10" }]
                }
            }, ns, cancellationToken: token);
            Check(await Authenticate(service, await Issue(account, trust.Audience, true)) == RuntimeAuthenticationOutcome.Authenticated, "R14-new-Pod-same-stable-ServiceAccount-binding-authenticates");
            foreign = await admin.CoreV1.CreateNamespacedServiceAccountAsync(new V1ServiceAccount { Metadata = new V1ObjectMeta { Name = podName } }, ns, cancellationToken: token);
            Check(IdentityRefused(await Authenticate(service, await Issue(foreign.Metadata.Name, trust.Audience, false))), "R06-real-foreign-ServiceAccount-UID");
            var before = attempts.Count;
            Check(await Authenticate(Compose(revoke), await Issue(account, trust.Audience, true)) == RuntimeAuthenticationOutcome.PermissionDenied, "R07-completed-inflight-binding-revoke-after-real-review");
            Check(attempts.Count == before + 1, "R08-single-real-attempt");
            before = attempts.Count;
            Check(await Authenticate(service, await Issue(account, trust.Audience, true)) == RuntimeAuthenticationOutcome.PermissionDenied && attempts.Count == before, "R09-revoked-before-review-no-cache-or-network");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (pod is not null)
            {
                try
                {
                    await admin.CoreV1.DeleteNamespacedPodAsync(pod.Metadata.Name, ns, new V1DeleteOptions
                    {
                        GracePeriodSeconds = 0,
                        Preconditions = new V1Preconditions { Uid = pod.Metadata.Uid }
                    }, cancellationToken: deadline.Token);
                }
                catch (Exception error) { cleanup = error; }
            }
            if (foreign is not null)
            {
                try
                {
                    await admin.CoreV1.DeleteNamespacedServiceAccountAsync(foreign.Metadata.Name, ns,
                    new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = foreign.Metadata.Uid } }, cancellationToken: deadline.Token);
                }
                catch (Exception error) { cleanup = cleanup is null ? error : new AggregateException(cleanup, error); }
            }
            if (cleanup is not null) throw primary is null ? cleanup : new AggregateException(primary, cleanup);
        }
    }
    private static async Task ForeignNamespace(Kubernetes admin, RuntimeAuthentication service, Guid binding, string account, string audience, string scenario, AttemptCounter attempts, CancellationToken token)
    {
        var name = "haby215-" + Guid.NewGuid().ToString("N")[..12];
        var ns = await admin.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = name } }, cancellationToken: token);
        Exception? primary = null;
        try
        {
            await admin.CoreV1.CreateNamespacedServiceAccountAsync(new V1ServiceAccount { Metadata = new V1ObjectMeta { Name = account } }, name, cancellationToken: token);
            var pod = await admin.CoreV1.CreateNamespacedPodAsync(new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = "bound" },
                Spec = new V1PodSpec
                {
                    ServiceAccountName = account,
                    AutomountServiceAccountToken = false,
                    Containers = [new V1Container { Name = "app", Image = "registry.k8s.io/pause:3.10" }]
                }
            }, name, cancellationToken: token);
            var issued = await admin.CoreV1.CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest
            {
                Spec = new V1TokenRequestSpec
                {
                    Audiences = [audience],
                    ExpirationSeconds = 600,
                    BoundObjectRef = new V1BoundObjectReference { ApiVersion = "v1", Kind = "Pod", Name = pod.Metadata.Name, Uid = pod.Metadata.Uid }
                }
            }, account, name, cancellationToken: token);
            var bytes = Encoding.ASCII.GetBytes(issued.Status.Token);
            try
            {
                var before = attempts.Completed;
                var result = (await service.AuthenticateAsync(binding, bytes, token)).Outcome;
                Console.WriteLine($"Runtime foreign namespace refusal category [{scenario}]: {result}");
                if (result is not (RuntimeAuthenticationOutcome.Unauthenticated or RuntimeAuthenticationOutcome.Unavailable) || attempts.Completed != before + 1)
                    throw new InvalidOperationException("Foreign namespace authenticated.");
                Console.WriteLine($"PASS runtime-{scenario}-R15-real-foreign-namespace-Pod-bound-token-refused");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await admin.CoreV1.DeleteNamespaceAsync(name, new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = ns.Metadata.Uid } }, cancellationToken: cleanup.Token);
            }
            catch (Exception error) { throw primary is null ? error : new AggregateException(primary, error); }
        }
    }
    private sealed class AttemptCounter { internal int Count, Completed; }
    private sealed class ReviewProbe(AttemptCounter counter, Func<Task>? afterResponse) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            counter.Count++;
            var result = await base.SendAsync(request, cancellationToken);
            if (result.IsSuccessStatusCode) counter.Completed++;
            try { if (afterResponse is not null) await afterResponse(); return result; }
            catch { result.Dispose(); throw; }
        }
    }
}
