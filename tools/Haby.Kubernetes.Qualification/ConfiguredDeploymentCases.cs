using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Operator.Host;
using Haby.Operator.Contracts;
using Grpc.Net.Client;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql.IntegrationTests;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

internal static class ConfiguredDeploymentCases
{
    private sealed class LostConfirmation : IOException;
    internal static async Task Run(Kubernetes admin, Uri api, string directory, CancellationToken token, PostgreSqlFixture? fixture = null)
    {
        var hostAddress = IPAddress.Parse((await File.ReadAllTextAsync(Path.Combine(directory, "pod-host-address"), token)).Trim());
        var image = (await File.ReadAllTextAsync(Path.Combine(directory, "consumer-image"), token)).Trim();
        var ns = "haby223-" + Guid.NewGuid().ToString("N")[..12];
        var postgres = fixture ?? new PostgreSqlFixture();
        V1Namespace? ownedNamespace = null;
        Exception? primary = null;
        await ((Xunit.IAsyncLifetime)postgres).InitializeAsync();
        try
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Haby223 Disposable Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            using var leafKey = RSA.Create(2048);
            var leafRequest = new CertificateRequest("CN=Haby223 Delivery", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(hostAddress);
            leafRequest.CertificateExtensions.Add(san.Build());
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var publicLeaf = leafRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(45), RandomNumberGenerator.GetBytes(16));
            using var leaf = publicLeaf.CopyWithPrivateKey(leafKey);
            var certificatePath = Path.Combine(directory, "delivery.pfx");
            await File.WriteAllBytesAsync(certificatePath, leaf.Export(X509ContentType.Pfx), token);
            using var reservation = new TcpListener(IPAddress.Any, 0); reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var delivery = new RuntimeConfiguration($"https://{hostAddress}:{port}/", new("custom-ca", root.ExportCertificatePem()), new(TotalTimeoutSeconds: 30));
            var requests = new ConcurrentQueue<Guid>(); var reviews = 0; var revokeNextRead = 0;
            var deploymentWrites = 0; var loseConfirmation = false; var loseIdentityConfirmation = false;
            ownedNamespace = await admin.CoreV1.CreateNamespaceAsync(new() { Metadata = new() { Name = ns, Labels = new Dictionary<string, string> { ["haby.dev/qualification"] = ns } } }, cancellationToken: token);
            async Task<KubernetesProviderConfiguration> Configuration(ISecretStore store, EnvironmentRecordId environment)
            {
                async Task<SecretRef> Save(string file)
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, file), token);
                    try
                    {
                        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
                        await store.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), reference, bytes, token); return reference;
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                return new(api, await File.ReadAllTextAsync(Path.Combine(directory, "ca.crt"), token), null, await Save("admin.crt"), await Save("admin.key"));
            }
            ValueTask<ProviderPreflightResult> Preflight(ISecretStore store, EnvironmentRecordId environment, ProviderAliasBinding binding,
                KubernetesProviderConfiguration configuration, CancellationToken ct) => new KubernetesPreflightHandler(new KubernetesProviderConfigurationCodec(),
                    (scope, exact, cancellation) => KubernetesClientFactory.CreateFixture(new(new(scope.ToString(), binding.InstanceId.ToString(), binding.RevisionNumber.Value,
                        KubernetesProviderConfigurationCodec.Implementation), exact.Endpoint, exact.ServerCaPem, exact.BearerToken, exact.ClientCertificate, exact.ClientPrivateKey), store, [], cancellation))
                    .CheckTypedAsync(environment, configuration, ct);
            var scenario = await new DurableDeploymentPlanningTests(postgres).InternalScenarioAsync(Configuration, ns, actualKubernetesProbe: Preflight, initialOnly: true,
                configureManifest: envelope =>
                {
                    DurableDeploymentPlanningTests.ConfiguredDocuments(envelope, secondDocument: true);
                    var component = envelope["manifest"]!["spec"]!["components"]!["api"]!;
                    component["documents"]!["other"] = component["documents"]!["z-last"]!.DeepClone(); component["documents"]!.AsObject().Remove("z-last");
                    var properties = component["workloads"]!["main"]!["properties"]!;
                    properties["configuration"]!["documents"]![1] = "other";
                    properties["image"]!["reference"] = image; properties["containerName"] = "configured-consumer";
                    properties["overrides"] = new System.Text.Json.Nodes.JsonObject { ["minReadySeconds"] = 1 };
                }, runtimeConfigurationJson: ConfigurationJson(delivery), acceptGeneration: async (composition, environment, reference) =>
                {
                    var factory = composition.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
                    string connection, key;
                    await using (var db = await factory.CreateDbContextAsync(token))
                    {
                        connection = db.Database.GetConnectionString()!;
                        key = (await db.Environments.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(environment.ToString()), token)).EnvironmentId;
                    }
                    await using var admission = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Port=0", "--Haby:Tls:Enabled=false"]);
                    await OperatorTestCredentials.StartWithIssuedOperatorAsync(admission);
                    using var channel = GrpcChannel.ForAddress(admission.Urls.Single());
                    var client = OperatorTestCredentials.Client(channel);
                    var change = (await client.CreateChangeSetAsync(new CreateChangeSetRequest
                    {
                        EnvironmentId = key,
                        Reference = new() { ApplicationRecordId = reference.ApplicationRecordId.ToString(), RevisionNumber = reference.RevisionNumber.Value },
                        Precondition = PlanningPrecondition.ExpectedAbsence
                    }, cancellationToken: token)).ChangeSet;
                    var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSetId }, cancellationToken: token);
                    var committed = await client.CommitDesiredStateAsync(new CommitDesiredStateRequest { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId }, cancellationToken: token);
                    if (committed.Status != CommitStatus.Committed) throw new InvalidOperationException("Operator qualification acceptance refused.");
                    await admission.StopAsync(token);
                    return new DurableChangeSetCommit(DeploymentChangeSetId.Parse(committed.Commit.ChangeSetId), DeploymentPlanId.Parse(committed.Commit.PlanId),
                        ApplicationDeploymentId.Parse(committed.Commit.DeploymentId), DeploymentGeneration.Create(committed.Commit.Generation),
                        DateTimeOffset.Parse(committed.Commit.CommittedAtUtc), committed.Commit.TechnicalActorId);
                });
            await using var services = DurableDeploymentPlanningTests.InternalServices(scenario.Connection, scenario.Time);
            var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            var observationClock = new ObservationClock(services.GetRequiredService<TimeProvider>());
            var secrets = services.GetRequiredService<ISecretStore>();
            var configurationPreparation = DurableDeploymentPlanningTests.ConfigurationPreparation(services).Preparation;
            var identity = new InternalRuntimeIdentityAction(secrets, (connection, ct) => KubernetesClientFactory.CreateFixture(connection, secrets, [], ct));
            var deployment = new InternalDeploymentAction(secrets, connection => new DeploymentKernel(connection.Binding,
                ct => KubernetesClientFactory.CreateFixture(connection, secrets, [new CountCreates(() => Interlocked.Increment(ref deploymentWrites))], ct)));
            InternalDurableActions CreateActions() => new(contexts, [identity, deployment], services.GetServices<IProviderConfigurationCodec>(), services.GetServices<IExternalOperationOutputContract>(),
                services.GetRequiredService<DurableActionAdmission>(), services.GetRequiredService<DurableOperationSerialization>(), observationClock,
                fault: point =>
                {
                    if (point == InternalActionFault.AfterCompletionCommit && loseIdentityConfirmation) { loseIdentityConfirmation = false; throw new LostConfirmation(); }
                    if (point == InternalActionFault.AfterExternalResult && loseConfirmation) { loseConfirmation = false; throw new LostConfirmation(); }
                },
                configurationPreparation: configurationPreparation);
            var actions = CreateActions();
            var subject = new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main"));
            DurableOperationKey key = null!;
            DeploymentIntent intent = null!;
            RuntimeBootstrap exact = null!;
            RuntimePublicationResult later = null!;
            var publication = new InternalRuntimePublication(contexts, configurationPreparation, actions, services.GetRequiredService<TimeProvider>());
            async Task AdmissionRefuses(string request)
            {
                try { await actions.AcceptAsync(scenario.Commit, subject, deployment.Contract.Action, request, token); }
                catch (InvalidDataException) { return; }
                throw new InvalidOperationException("Configured admission unexpectedly succeeded.");
            }
            async Task RevokeLater()
            {
                var current = (await publication.ReadAsync(later.Receipt.Request.Key, token))!;
                await publication.ApplyAsync(later.Receipt.Request with
                {
                    Key = "consumer-revoke",
                    Operation = RuntimePublicationOperation.RevokePermission,
                    ExpectedToken = current.CurrentPermission!.Token,
                    Origin = null
                }, token);
            }
            Microsoft.AspNetCore.Builder.WebApplication? activeHost = null;
            using var firstSubmissionAudit = new ConfiguredSubmissionProbe();
            async Task WithHost(Func<Task> work)
            {
                await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection,
                    "--Haby:ListenAddress=0.0.0.0", "--Haby:Port=" + port, "--Haby:Tls:CertificatePath=" + certificatePath], collection =>
                {
                    firstSubmissionAudit.Register(collection);
                    collection.AddSingleton<InternalDurableActions>(_ => actions);
                    collection.RemoveAll<IRuntimeAuthenticator>();
                    collection.AddSingleton<IRuntimeAuthenticator>(p => new KubernetesRuntimeAuthenticator(p.GetRequiredService<ISecretStore>(), (connection, handlers, ct) =>
                        KubernetesClientFactory.CreateFixture(connection, p.GetRequiredService<ISecretStore>(), [.. handlers, new CountReviews(() => Interlocked.Increment(ref reviews))], ct)));
                    collection.AddSingleton<IRuntimeConfigurationStore>(p => new DeliveryBoundary(new PostgreSqlRuntimeConfigurationStore(p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>()),
                        id => requests.Enqueue(Guid.Parse(id.ToString())), async () => { if (Interlocked.Exchange(ref revokeNextRead, 0) == 1) await RevokeLater(); }));
                });
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                activeHost = host; await work(); activeHost = null;
                await host.StopAsync(token);
            }
            V1Pod? currentPod = null;
            RolloutResult? rolloutReady = null;
            async Task<V1Pod> Startup(string marker, string? excludedUid = null)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(35));
                while (true)
                {
                    var pods = await admin.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: wait.Token);
                    foreach (var pod in pods.Items.Where(x => x.Metadata.Uid != excludedUid && x.Status?.ContainerStatuses?.Any(c =>
                        c.Name == intent.Target.Container && (c.State?.Running is not null || c.State?.Terminated is not null || c.LastState?.Terminated is not null)) == true))
                    {
                        using var stream = await admin.CoreV1.ReadNamespacedPodLogAsync(pod.Metadata.Name, ns, container: intent.Target.Container, cancellationToken: wait.Token);
                        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                        var characters = new char[4097];
                        var length = await reader.ReadBlockAsync(characters.AsMemory(), wait.Token);
                        if (length > 4096) throw new InvalidOperationException("Consumer marker output bound exceeded.");
                        var log = new string(characters, 0, length);
                        if (log.Contains(marker, StringComparison.Ordinal))
                        {
                            if (marker != "primary-started" && log.Contains("primary-started", StringComparison.Ordinal)) throw new InvalidOperationException("Primary work bypassed mandatory retrieval.");
                            return pod;
                        }
                    }
                    await Task.Delay(100, wait.Token);
                }
            }
            await WithHost(async () =>
            {
                using var channel = GrpcChannel.ForAddress(activeHost!.Urls.Single(), new GrpcChannelOptions
                { HttpHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator } });
                var client = OperatorTestCredentials.Client(channel);
                var request = new GenerationRequest { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = scenario.Commit.Generation.Value };
                loseIdentityConfirmation = true;
                WorkloadSubmissionReply partial;
                var firstStatus = Grpc.Core.StatusCode.Unknown;
                firstSubmissionAudit.Begin(activeHost.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LoggerFilterOptions>>());
                try
                {
                    partial = await client.SubmitGenerationAsync(request, cancellationToken: token);
                    firstStatus = Grpc.Core.StatusCode.OK;
                }
                catch (Grpc.Core.RpcException failure)
                {
                    firstSubmissionAudit.ClientFailure(failure);
                    firstStatus = Enum.IsDefined(failure.StatusCode) ? failure.StatusCode : Grpc.Core.StatusCode.Unknown;
                    throw;
                }
                finally
                {
                    firstSubmissionAudit.End();
                    Console.WriteLine("Diagnostic configured first-submit-status: " + firstStatus);
                    Console.WriteLine("Diagnostic configured first-submit-audit-started: " + (firstSubmissionAudit.Started ? "true" : "false"));
                    firstSubmissionAudit.Write(Console.Out);
                }
                Check(partial.Outcome == "unavailable" && partial.Progress.Members.Single().Documents == "prepared", "C01-exact-two-documents-prepared");
                Check(partial.Progress.Members.Single().Identity == "confirmed", "C02-confirmed-stable-identity");
                await AdmissionRefuses("missing-publication");
                Check((await admin.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: token)).Items.Count == 0, "C03-missing-publication-no-Deployment");
                loseConfirmation = true;
                var uncertain = await client.SubmitGenerationAsync(request, cancellationToken: token);
                Check(uncertain.Outcome == "unavailable" && uncertain.Progress.Members.Single().Publication == "published", "C04-explicit-publication");
                await using (var db = await contexts.CreateDbContextAsync(token))
                {
                    var savedIntent = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.ActionContract == InternalDeploymentAction.Create, token);
                    key = new(scenario.Commit.DeploymentId, scenario.Commit.Generation, OperationId.Create(savedIntent.OperationId));
                }
                intent = DeploymentIntents.Decode((await actions.HistoryAsync(key, token))!.Intent.Payload);
                exact = RuntimeBootstrapCodec.Decode(intent.Target.Runtime!.Bootstrap);
                var requestsToPublish = await publication.PreparationRequestsAsync(scenario.Commit, "qualifier", token);
                later = (await publication.ReadAsync(requestsToPublish.Single(x => x.Origin!.Document == "settings").Key, token))!;
                var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
                if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) throw new InvalidOperationException("Published qualification CLI is required.");
                using var submission = JsonDocument.Parse(await OperatorHostIntegrationTests.RunNativeCommitCli(binary, activeHost.Urls.Single(),
                    ["deployment", "generation", "submit", request.DeploymentId, request.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
                Check(submission.RootElement.GetProperty("outcome"u8).GetString() == "submitted" && deploymentWrites == 1,
                    "C05-production-generated-create-confirmed");
                currentPod = await Startup("primary-started");
                Check(requests.ToArray().Distinct().Order().SequenceEqual(exact.Documents.Values.Select(Guid.Parse).Order()) && reviews >= 2,
                    "C06-real-projected-token-HTTPS-two-exact-documents-primary");
                var saved = await admin.AppsV1.ReadNamespacedDeploymentAsync(intent.Name, ns, cancellationToken: token);
                DeploymentObjects.Receipt(DeploymentObjects.Read(saved), intent, ConfirmationSource.Observation);
                Check(saved.Spec.Template.Spec.ServiceAccountName == intent.Target.Runtime.ServiceAccount && currentPod.Spec.AutomountServiceAccountToken == false,
                    "C07-complete-saved-projection-and-Pod-identity");
                rolloutReady = await actions.ObserveRolloutAsync(scenario.Commit, "rollout-ready", new(RequestMilliseconds: 30000), token);
                Check(rolloutReady.Ready && rolloutReady.Submitted is not null && rolloutReady.SavedReady is not null &&
                    intent.Target.MinReadySeconds == 1 && saved.Metadata.Uid == DeploymentIntents.DecodeReceipt((await actions.HistoryAsync(key, token))!.Output!.Payload).Uid,
                    "R01-controller-produced-available-minReadySeconds-exact-UID");
            });
            requests.Clear(); var beforeRestart = reviews;
            await WithHost(async () =>
            {
                var previous = currentPod!;
                await admin.CoreV1.DeleteNamespacedPodAsync(previous.Metadata.Name, ns, new V1DeleteOptions { Preconditions = new() { Uid = previous.Metadata.Uid }, GracePeriodSeconds = 0 }, cancellationToken: token);
                currentPod = await Startup("primary-started", previous.Metadata.Uid);
                Check(reviews >= beforeRestart + 2 && requests.ToArray().Distinct().Order().SequenceEqual(exact.Documents.Values.Select(Guid.Parse).Order()) &&
                    (await actions.HistoryAsync(key, token))!.Intent.Payload == DeploymentIntents.Encode(intent), "C08-restarted-Host-fresh-Pod-same-bootstrap");
                actions = CreateActions();
                var observed = await actions.ObserveRolloutAsync(scenario.Commit, "rollout-restarted", new(RequestMilliseconds: 30000), token);
                Check(observed.Ready && observed.SavedReady == rolloutReady!.SavedReady && observed.Members[0].Attempt == rolloutReady.Members[0].Attempt &&
                    observed.Members[0].Deadline == rolloutReady.Members[0].Deadline && deploymentWrites == 1,
                    "R02-reconstructed-observer-original-attempt-deadline-zero-writes");
            });
            requests.Clear(); Interlocked.Exchange(ref revokeNextRead, 1);
            await WithHost(async () =>
            {
                var previous = currentPod!;
                await admin.CoreV1.DeleteNamespacedPodAsync(previous.Metadata.Name, ns, new V1DeleteOptions { Preconditions = new() { Uid = previous.Metadata.Uid }, GracePeriodSeconds = 0 }, cancellationToken: token);
                currentPod = await Startup("runtime-startup-refused:access-denied", previous.Metadata.Uid);
                Check(requests.Count >= 2, "C09-later-document-revoke-no-primary");
                RolloutResult? pending = null;
                for (var n = 0; n < 15; n++)
                {
                    pending = await actions.ObserveRolloutAsync(scenario.Commit, "rollout-pending-" + n, new(MaximumRounds: 1), token);
                    if (!pending.Ready) break;
                    await Task.Delay(200, token);
                }
                Check(pending is { Ready: false } && pending.SavedReady == rolloutReady!.SavedReady &&
                    pending.Members[0].Observation.Outcome == RolloutOutcome.Pending, "R03-startup-refusal-is-current-incomplete-despite-historical-Ready");
                observationClock.Advance(TimeSpan.FromSeconds(intent.Target.ProgressDeadlineSeconds));
                var resumed = await actions.ResumeRolloutAsync(scenario.Commit, "rollout-resume", "qualifier",
                    [new(pending!.Members[0].Subject, pending.Members[0].Attempt!.Value)], token);
                Check(!resumed.Ready && resumed.Members[0].Attempt != pending.Members[0].Attempt &&
                    resumed.Members[0].Observation.Outcome == RolloutOutcome.NotObserved && deploymentWrites == 1,
                    "R04-audited-resume-renews-observation-only");
            });
            await AdmissionRefuses("revoked-publication");
            Check((await publication.ApplyAsync(later.Receipt.Request, token)).CurrentPermission!.Status == "Revoked", "C10-revoked-admission-and-no-publication-repair");
            await using var verify = await contexts.CreateDbContextAsync(token);
            Check((await verify.ApplicationDeployments.AsNoTracking().SingleAsync(token)).AppliedGeneration is null &&
                await verify.RuntimeIdentityBindings.CountAsync(token) == 1 && !await verify.ConfigurationAccess.AnyAsync(token), "C11-no-Applied-static-grant-or-new-identity");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var cleanup = new List<Exception>();
            if (ownedNamespace is not null)
            {
                try
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await admin.CoreV1.DeleteNamespaceAsync(ns, new V1DeleteOptions { Preconditions = new() { Uid = ownedNamespace.Metadata.Uid } }, cancellationToken: budget.Token);
                    while (true)
                    {
                        try { await admin.CoreV1.ReadNamespaceAsync(ns, cancellationToken: budget.Token); }
                        catch (k8s.Autorest.HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound) { break; }
                        await Task.Delay(100, budget.Token);
                    }
                    Console.WriteLine("PASS configured-owned-namespace-absent");
                }
                catch (Exception error) { cleanup.Add(error); }
            }
            try { await ((Xunit.IAsyncLifetime)postgres).DisposeAsync(); Console.WriteLine("PASS configured-owned-catalog-cleanup"); }
            catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count > 0) throw new AggregateException(primary is null ? cleanup : [primary, .. cleanup]);
        }
    }
    private static string ConfigurationJson(RuntimeConfiguration value)
    {
        // Reuse the canonical neutral bootstrap writer to obtain the same closed configuration fields.
        var bootstrap = new RuntimeBootstrap(1, Guid.CreateVersion7().ToString("D"),
            System.Collections.Immutable.ImmutableSortedDictionary<string, string>.Empty.Add("settings", Guid.CreateVersion7().ToString("D")), RuntimeBootstrap.TokenPath, value);
        using var json = JsonDocument.Parse(RuntimeBootstrapCodec.Encode(bootstrap));
        return "{\"endpoint\":" + json.RootElement.GetProperty("delivery"u8).GetProperty("endpoint"u8).GetRawText() + ",\"tls\":" +
            json.RootElement.GetProperty("delivery"u8).GetProperty("tls"u8).GetRawText() + ",\"client\":" + json.RootElement.GetProperty("client"u8).GetRawText() + "}";
    }
    private sealed class ObservationClock(TimeProvider source) : TimeProvider
    {
        private TimeSpan offset;
        public override DateTimeOffset GetUtcNow() => source.GetUtcNow() + offset;
        internal void Advance(TimeSpan value) => offset += value;
    }
    private static void Check(bool value, string id) { if (!value) throw new InvalidOperationException("Configured Deployment case failed: " + id); Console.WriteLine("PASS configured-" + id); }
    private sealed class CountReviews(Action count) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { if (request.RequestUri!.AbsolutePath.EndsWith("/tokenreviews", StringComparison.Ordinal)) count(); return base.SendAsync(request, token); }
    }
    private sealed class CountCreates(Action count) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/deployments", StringComparison.Ordinal)) count();
            return base.SendAsync(request, token);
        }
    }
    private sealed class DeliveryBoundary(IRuntimeConfigurationStore inner, Action<ConfigurationDocumentRevisionId> requested, Func<Task> beforeRead) : IRuntimeConfigurationStore
    {
        public ValueTask<RuntimeConfigurationAuthority?> AuthorizeAsync(RuntimeAuthenticatedContext identity, ConfigurationDocumentRevisionId revision, CancellationToken token)
        { requested(revision); return inner.AuthorizeAsync(identity, revision, token); }
        public async ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(RuntimeConfigurationAuthority authority, CancellationToken token)
        { await beforeRead(); return await inner.ReadAsync(authority, token); }
    }
}
