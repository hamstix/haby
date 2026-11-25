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
    internal static async Task Run(Kubernetes admin, Uri api, string directory, ConfiguredLifecycle lifetime, PostgreSqlFixture? fixture = null)
    {
        var token = lifetime.Functional.Token;
        var hostAddress = IPAddress.Parse((await File.ReadAllTextAsync(Path.Combine(directory, "pod-host-address"), token)).Trim());
        var image = (await File.ReadAllTextAsync(Path.Combine(directory, "consumer-image"), token)).Trim();
        var ns = "haby223-" + Guid.NewGuid().ToString("N")[..12];
        var postgres = fixture ?? new PostgreSqlFixture();
        V1Namespace? ownedNamespace = null;
        Exception? primary = null;
        var drainOwnershipRefused = false;
        DeploymentReceipt[]? drainReceipts = null;
        Console.WriteLine("Q243 configured catalog-init start");
        await ((Xunit.IAsyncLifetime)postgres).InitializeAsync();
        Console.WriteLine("Q243 configured catalog-init completed");
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
            var expectedDeploymentWrites = 1;
            var confirmedDrain = new List<DeploymentReceipt>();
            System.Text.Json.Nodes.JsonNode configuredEnvelope = null!;
            Console.WriteLine("Q243 configured namespace-create start");
            ownedNamespace = await admin.CoreV1.CreateNamespaceAsync(new() { Metadata = new() { Name = ns, Labels = new Dictionary<string, string> { ["haby.dev/qualification"] = ns } } }, cancellationToken: token);
            Console.WriteLine("Q243 configured namespace-created completed");
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
                    .CheckTypedAsync(new(environment, binding.InstanceId, binding.RevisionNumber), configuration, ct);
            Console.WriteLine("Q243 configured accepted-plan start");
            var scenario = await new DurableDeploymentPlanningTests(postgres).InternalScenarioAsync(Configuration, ns, actualKubernetesProbe: Preflight, initialOnly: true,
                configureManifest: envelope =>
                {
                    DurableDeploymentPlanningTests.ConfiguredDocuments(envelope, secondDocument: true);
                    var component = envelope["manifest"]!["spec"]!["components"]!["api"]!;
                    component["documents"]!["other"] = component["documents"]!["z-last"]!.DeepClone(); component["documents"]!.AsObject().Remove("z-last");
                    var properties = component["workloads"]!["main"]!["properties"]!;
                    properties["configuration"]!["documents"]![1] = "other";
                    properties["image"]!["reference"] = image; properties["containerName"] = "configured-consumer";
                    properties["overrides"] = System.Text.Json.Nodes.JsonNode.Parse("""{"minReadySeconds":1,"resources":{"requests":{"memory":"128Mi"},"limits":{"cpu":"1","memory":"512Mi"}}}"""u8);
                    configuredEnvelope = envelope.DeepClone();
                }, configurePlacement: DurableDeploymentPlanningTests.ApplyPortsAndProbes,
                runtimeConfigurationJson: ConfigurationJson(delivery), acceptGeneration: async (composition, environment, reference) =>
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
            Console.WriteLine("Q243 configured accepted-plan completed");
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
                Console.WriteLine("Q243 configured host-build start");
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
                Console.WriteLine("Q243 configured host-build completed");
                Console.WriteLine("Q243 configured host-start start");
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                Console.WriteLine("Q243 configured host-start completed");
                activeHost = host;
                try { await work(); activeHost = null; }
                catch { lifetime.Functional.Fail(); throw; }
                await host.StopAsync(lifetime.Token);
            }
            V1Pod? currentPod = null;
            RolloutResult? rolloutReady = null;
            DurableChangeSetCommit updateBaseline = null!;
            System.Text.Json.Nodes.JsonNode updateEnvelope = null!;
            async Task<V1Pod> Startup(string marker, string? excludedUid = null)
            {
                Console.WriteLine("Q243 configured pod-startup start");
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(35));
                while (true)
                {
                    var pods = await admin.CoreV1.ListNamespacedPodAsync(ns,
                        labelSelector: DeploymentIntents.ManagedKey + "=" + intent.ManagedWorkload.ToString("N"), cancellationToken: wait.Token);
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
                            Console.WriteLine("Q243 configured pod-startup completed");
                            return pod;
                        }
                    }
                    await Task.Delay(100, wait.Token);
                }
            }
            await WithHost(async () =>
            {
                var listener = activeHost!.Urls.Single();
                var localOperator = LocalOperatorEndpoint(listener);
                var issuedCredential = OperatorTestCredentials.Credential(listener);
                using var channel = GrpcChannel.ForAddress(localOperator, new GrpcChannelOptions
                { HttpHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator } });
                var client = OperatorTestCredentials.Client(channel, issuedCredential);
                var request = new GenerationRequest { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = scenario.Commit.Generation.Value };
                loseIdentityConfirmation = true;
                WorkloadSubmissionReply partial;
                var firstStatus = Grpc.Core.StatusCode.Unknown;
                firstSubmissionAudit.Begin(activeHost.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LoggerFilterOptions>>());
                try
                {
                    Console.WriteLine("Q243 configured first-submit start");
                    partial = await client.SubmitGenerationAsync(request, cancellationToken: token);
                    Console.WriteLine("Q243 configured first-submit completed");
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
                    Console.WriteLine("Q243 configured rpc " + (Enum.IsDefined(firstStatus) ? firstStatus.ToString() : "unknown"));
                    Console.WriteLine("Diagnostic configured first-submit-status: " + firstStatus);
                    Console.WriteLine("Diagnostic configured first-submit-audit-started: " + (firstSubmissionAudit.Started ? "true" : "false"));
                    firstSubmissionAudit.Write(Console.Out);
                }
                Check(partial.Outcome == "unavailable" && partial.Progress.Members.Single().Documents == "prepared", "C01-exact-two-documents-prepared");
                Check(partial.Progress.Members.Single().Identity == "confirmed", "C02-confirmed-stable-identity");
                await AdmissionRefuses("missing-publication");
                Check((await admin.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: token)).Items.Count == 0, "C03-missing-publication-no-Deployment");
                loseConfirmation = true;
                Console.WriteLine("Q243 configured uncertain-submit start");
                var uncertain = await client.SubmitGenerationAsync(request, cancellationToken: token);
                Console.WriteLine("Q243 configured uncertain-submit completed");
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
                Console.WriteLine("Q243 configured cli-submit start");
                using var submission = JsonDocument.Parse(await ConfiguredNativeCli.Run(lifetime, directory, binary, localOperator.AbsoluteUri,
                    ["deployment", "generation", "submit", request.DeploymentId, request.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)], credential: issuedCredential));
                Console.WriteLine("Q243 configured cli-submit completed");
                Check(submission.RootElement.GetProperty("outcome"u8).GetString() == "submitted" && deploymentWrites == 1,
                    "C05-production-generated-create-confirmed");
                currentPod = await Startup("primary-started");
                Check(requests.ToArray().Distinct().Order().SequenceEqual(exact.Documents.Values.Select(Guid.Parse).Order()) && reviews >= 2,
                    "C06-real-projected-token-HTTPS-two-exact-documents-primary");
                Console.WriteLine("Q243 configured readback start");
                var saved = await admin.AppsV1.ReadNamespacedDeploymentAsync(intent.Name, ns, cancellationToken: token);
                confirmedDrain.Add(DeploymentObjects.Receipt(DeploymentObjects.Read(saved), intent, ConfirmationSource.Observation));
                var actualResources = saved.Spec.Template.Spec.Containers.Single(c => c.Name == "configured-consumer").Resources;
                var actualMain = saved.Spec.Template.Spec.Containers.Single(c => c.Name == "configured-consumer");
                var podMain = currentPod.Spec.Containers.Single(c => c.Name == "configured-consumer");
                Check(actualMain.Ports.Count == 2 && actualMain.Ports.Single(p => p.Name == "web").ContainerPort == 8080 &&
                    actualMain.Ports.Single(p => p.Name == "web").Protocol == "TCP" && actualMain.Ports.Single(p => p.Name == "dns").Protocol == "UDP" &&
                    actualMain.StartupProbe.HttpGet.Port.Value == "web" && actualMain.StartupProbe.HttpGet.Path == "/health" &&
                    actualMain.StartupProbe.HttpGet.Scheme == "HTTP" && actualMain.StartupProbe.PeriodSeconds == 10 &&
                    actualMain.LivenessProbe.TcpSocket.Port.Value == "web" && actualMain.LivenessProbe.TimeoutSeconds == 1 &&
                    actualMain.ReadinessProbe.HttpGet.Path == "/health" && actualMain.ReadinessProbe.PeriodSeconds == 1 &&
                    podMain.StartupProbe.HttpGet.Scheme == "HTTP" && podMain.ReadinessProbe.SuccessThreshold == 1 &&
                    podMain.ReadinessProbe.FailureThreshold == 1, "C13-configured-Deployment-Pod-ports-probes-defaults");
                Check(saved.Spec.Template.Spec.ServiceAccountName == intent.Target.Runtime.ServiceAccount && currentPod.Spec.AutomountServiceAccountToken == false &&
                    intent.Target.Resources!.Requests!.Cpu is null &&
                    !actualResources.Requests.ContainsKey("cpu") &&
                    DeploymentResources.Equivalent("128Mi", actualResources.Requests["memory"].ToString()) &&
                    DeploymentResources.Equivalent("1", actualResources.Limits["cpu"].ToString()) &&
                    DeploymentResources.Equivalent("512Mi", actualResources.Limits["memory"].ToString()),
                    "C07-complete-saved-projection-and-Pod-identity");
                Console.WriteLine("Q243 configured readback completed");
                var podResources = currentPod.Spec.Containers.Single(c => c.Name == "configured-consumer").Resources;
                Check(DeploymentResources.Equivalent("1", podResources.Requests["cpu"].ToString()) &&
                    DeploymentResources.Equivalent("128Mi", podResources.Requests["memory"].ToString()) &&
                    DeploymentResources.Equivalent("1", podResources.Limits["cpu"].ToString()) &&
                    DeploymentResources.Equivalent("512Mi", podResources.Limits["memory"].ToString()), "C12-Pod-defaulted-resource-requests");
                rolloutReady = await actions.ObserveRolloutAsync(scenario.Commit, "rollout-ready", new(RequestMilliseconds: 30000), token);
                Check(rolloutReady.Ready && rolloutReady.Submitted is not null && rolloutReady.SavedReady is not null &&
                    intent.Target.MinReadySeconds == 1 && saved.Metadata.Uid == DeploymentIntents.DecodeReceipt((await actions.HistoryAsync(key, token))!.Output!.Payload).Uid,
                    "R01-controller-produced-available-minReadySeconds-exact-UID");
                // Renew only before Applied; completed proof reads cannot authorize a new attempt.
                observationClock.Advance(TimeSpan.FromSeconds(intent.Target.ProgressDeadlineSeconds));
                var renewedInternal = await actions.ResumeRolloutAsync(scenario.Commit, "rollout-resume", "qualifier",
                    [new(rolloutReady.Members[0].Subject, rolloutReady.Members[0].Attempt!.Value)], token);
                Check(!renewedInternal.Ready && renewedInternal.Members[0].Attempt != rolloutReady.Members[0].Attempt &&
                    renewedInternal.Members[0].Observation.Outcome == RolloutOutcome.NotObserved && deploymentWrites == 1,
                    "R04-audited-resume-renews-observation-only");
                rolloutReady = await actions.ObserveRolloutAsync(scenario.Commit, "rollout-renewed-internal", new(RequestMilliseconds: 30000), token);
                observationClock.Advance(TimeSpan.FromSeconds(intent.Target.ProgressDeadlineSeconds));
                using var renewed = JsonDocument.Parse(await ConfiguredNativeCli.Run(lifetime, directory, binary, localOperator.AbsoluteUri,
                    ["deployment", "generation", "resume", scenario.Commit.DeploymentId.ToString(), scenario.Commit.Generation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "--member", rolloutReady.Members[0].Subject + "=" + rolloutReady.Members[0].Attempt!.Value.ToString("D")], credential: issuedCredential));
                var renewal = renewed.RootElement.GetProperty("data"u8);
                Check(renewed.RootElement.GetProperty("outcome"u8).GetString() == "resumed" && !renewal.GetProperty("currentReady"u8).GetBoolean() &&
                    renewal.GetProperty("members"u8)[0].GetProperty("outcome"u8).GetString() == "NotObserved" && deploymentWrites == 1,
                    "R06-native-public-expired-resume-observation-only");
                rolloutReady = await actions.ObserveRolloutAsync(scenario.Commit, "rollout-renewed-public", new(RequestMilliseconds: 30000), token);
                using var waited = JsonDocument.Parse(await ConfiguredNativeCli.Run(lifetime, directory, binary, localOperator.AbsoluteUri,
                    ["deployment", "generation", "wait", request.DeploymentId, request.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)], credential: issuedCredential));
                Check(waited.RootElement.GetProperty("data"u8).GetProperty("currentReady"u8).GetBoolean() &&
                    waited.RootElement.GetProperty("data"u8).GetProperty("currentApplied"u8).GetBoolean() &&
                    waited.RootElement.GetProperty("data"u8).GetProperty("retirementId"u8).GetString() is not null && deploymentWrites == 1,
                    "R05-native-public-wait-whole-set-zero-writes");
                await using (var completed = await contexts.CreateDbContextAsync(token))
                {
                    var source = await completed.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == Guid.Parse(request.DeploymentId), token);
                    var proof = await InitialRetirementCompletion.RequireProofAsync(completed, source, token);
                    Check((await completed.ApplicationDeployments.AsNoTracking().SingleAsync(token)).AppliedGeneration == source.Generation &&
                        proof.FreshReady.Members.Length == 1 && proof.Publications.Length == 2, "A01-native-wait-exact-retirement-proof-and-Applied");
                }
                var disabledEnvelope = configuredEnvelope.DeepClone();
                var disabledProperties = disabledEnvelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!;
                disabledProperties["ports"] = new System.Text.Json.Nodes.JsonObject();
                disabledProperties["probes"] = new System.Text.Json.Nodes.JsonObject();
                var peer = await DurableDeploymentPlanningTests.AcceptBatchPeerAsync(services, scenario, disabledEnvelope);
                updateBaseline = peer;
                updateEnvelope = disabledEnvelope.DeepClone();
                await using (var peerRead = await contexts.CreateDbContextAsync(token))
                    updateEnvelope["manifest"]!["metadata"]!["id"] = (await peerRead.DeploymentGenerations.AsNoTracking().SingleAsync(x =>
                        x.DeploymentId == Guid.Parse(peer.DeploymentId.ToString()) && x.Generation == peer.Generation.Value, token)).Inputs().Providers.Declaration.ApplicationId.Value;
                var submittedPeer = await client.SubmitGenerationAsync(new GenerationRequest { DeploymentId = peer.DeploymentId.ToString(), Generation = peer.Generation.Value }, cancellationToken: token);
                DeploymentIntent disabledIntent;
                await using (var read = await contexts.CreateDbContextAsync(token))
                    disabledIntent = DeploymentIntents.Decode((await read.ActionIntents.AsNoTracking().SingleAsync(x =>
                        x.DeploymentId == Guid.Parse(peer.DeploymentId.ToString()) && x.ActionContract == InternalDeploymentAction.Create, token)).Payload);
                var disabledObject = await admin.AppsV1.ReadNamespacedDeploymentAsync(disabledIntent.Name, ns, cancellationToken: token);
                var disabledMain = disabledObject.Spec.Template.Spec.Containers.Single(c => c.Name == "configured-consumer");
                Check(submittedPeer.Outcome == "submitted" && deploymentWrites == 2 && disabledIntent.Target.Ports.Count == 0 &&
                    disabledMain.Ports is null or { Count: 0 } && disabledMain.StartupProbe is null &&
                    disabledMain.LivenessProbe is null && disabledMain.ReadinessProbe is null,
                    "B01-second-exact-configured-generation-submitted");
                confirmedDrain.Add(DeploymentObjects.Receipt(DeploymentObjects.Read(disabledObject), disabledIntent, ConfirmationSource.Observation));
                expectedDeploymentWrites = 2;
                var path = Path.Combine(directory, "batch-members.json");
                async Task WriteBatch(params BatchQualificationMember[] members) => await File.WriteAllTextAsync(path,
                    JsonSerializer.Serialize(new BatchQualificationFile(members), BatchQualificationMetadata.Default.BatchQualificationFile), token);
                var originalMember = new BatchQualificationMember(request.DeploymentId, request.Generation);
                var peerMember = new BatchQualificationMember(peer.DeploymentId.ToString(), peer.Generation.Value);
                await WriteBatch(originalMember, new(ApplicationDeploymentId.New().ToString(), 1));
                using var mixed = JsonDocument.Parse(await ConfiguredNativeCli.Run(lifetime, directory, binary, localOperator.AbsoluteUri,
                    ["deployment", "generation", "wait-batch", "--file", path], expectedExit: 1, credential: issuedCredential));
                var mixedData = mixed.RootElement.GetProperty("data"u8);
                Check(!mixedData.GetProperty("currentReady"u8).GetBoolean() && mixedData.GetProperty("members"u8).GetArrayLength() == 2 &&
                    mixedData.GetProperty("members"u8)[0].GetProperty("currentReady"u8).GetBoolean() &&
                    mixedData.GetProperty("members"u8)[1].GetProperty("outcome"u8).GetString() == "NotFound", "B02-native-batch-failure-preserves-another-fresh-success");
                await WriteBatch(originalMember, peerMember);
                var batchId = Guid.NewGuid().ToString("D");
                string[] batchArguments = ["deployment", "generation", "wait-batch", "--file", path, "--request-id", batchId];
                using var complete = JsonDocument.Parse(await ConfiguredNativeCli.Run(lifetime, directory, binary, localOperator.AbsoluteUri, batchArguments, credential: issuedCredential));
                Check(complete.RootElement.GetProperty("data"u8).GetProperty("currentReady"u8).GetBoolean() &&
                    complete.RootElement.GetProperty("data"u8).GetProperty("currentApplied"u8).GetBoolean() &&
                    complete.RootElement.GetProperty("data"u8).GetProperty("members"u8).EnumerateArray().All(x => x.GetProperty("currentReady"u8).GetBoolean() && x.GetProperty("currentApplied"u8).GetBoolean() &&
                        x.GetProperty("retirementId"u8).GetString() is not null) && deploymentWrites == 2,
                    "B03-native-batch-complete-fresh-exact-generations-zero-extra-writes");
                using var replayBatch = JsonDocument.Parse(await ConfiguredNativeCli.Run(lifetime, directory, binary, localOperator.AbsoluteUri, batchArguments, expectedExit: 1, credential: issuedCredential));
                Check(!replayBatch.RootElement.GetProperty("data"u8).GetProperty("currentReady"u8).GetBoolean() &&
                    replayBatch.RootElement.GetProperty("data"u8).GetProperty("members"u8).EnumerateArray().All(x => x.GetProperty("replayed"u8).GetBoolean() &&
                        x.GetProperty("originalRequestReady"u8).GetBoolean() && x.GetProperty("originalRequestApplied"u8).GetBoolean() &&
                        !x.GetProperty("currentReady"u8).GetBoolean() && !x.GetProperty("currentApplied"u8).GetBoolean()), "B04-native-batch-replay-history-is-not-fresh-ready");
            });
            requests.Clear(); var beforeRestart = reviews;
            await WithHost(async () =>
            {
                var previous = currentPod!;
                await admin.CoreV1.DeleteNamespacedPodAsync(previous.Metadata.Name, ns, new V1DeleteOptions { Preconditions = new() { Uid = previous.Metadata.Uid }, GracePeriodSeconds = 0 }, cancellationToken: token);
                currentPod = await Startup("primary-started", previous.Metadata.Uid);
                Check(reviews >= beforeRestart + 2 && requests.ToArray().Distinct().Order().SequenceEqual(exact.Documents.Values.Select(Guid.Parse).Order()) &&
                    (await actions.HistoryAsync(key, token))!.Intent.Payload == DeploymentIntents.Encode(intent), "C08-restarted-Host-fresh-Pod-same-bootstrap");
                await using (var completed = await contexts.CreateDbContextAsync(token))
                    Check((await completed.ApplicationDeployments.AsNoTracking().ToArrayAsync(token)).All(x => x.AppliedGeneration == x.DesiredGeneration),
                        "A02-runtime-retrieval-after-Applied-and-Host-restart");
                actions = CreateActions();
                var observed = await actions.ObserveRolloutAsync(scenario.Commit, "rollout-restarted", new(RequestMilliseconds: 30000), token);
                Check(observed.Ready && observed.SavedReady == rolloutReady!.SavedReady && observed.Members[0].Attempt == rolloutReady.Members[0].Attempt &&
                    observed.Members[0].Deadline == rolloutReady.Members[0].Deadline && deploymentWrites == expectedDeploymentWrites,
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
            });
            await AdmissionRefuses("revoked-publication");
            Check((await publication.ApplyAsync(later.Receipt.Request, token)).CurrentPermission!.Status == "Revoked", "C10-revoked-admission-and-no-publication-repair");
            await using var verify = await contexts.CreateDbContextAsync(token);
            Check((await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync(token)).All(x => x.AppliedGeneration == x.DesiredGeneration) &&
                await verify.RuntimeIdentityBindings.CountAsync(token) == expectedDeploymentWrites && !await verify.ConfigurationAccess.AnyAsync(token), "C11-Applied-preserves-publications-and-stable-identities");
            Check(await verify.GenerationStageCheckpoints.CountAsync(x => x.Stage == "RetirementCompleted", token) == expectedDeploymentWrites,
                "A03-revocation-keeps-historical-completion-without-regrant");
            // A separate accepted configured Application has a listening healthy process but an
            // intentionally failing readiness endpoint. No desired-state UPDATE or probe repair.
            await WithHost(async () =>
            {
                var blockedEnvelope = configuredEnvelope.DeepClone();
                DurableDeploymentPlanningTests.PortsAndProbes(blockedEnvelope);
                blockedEnvelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["probes"]!["readiness"]!["httpGet"]!["path"] = "/unready";
                blockedEnvelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["ports"]!.AsObject().Remove("dns");
                var blocked = await DurableDeploymentPlanningTests.AcceptBatchPeerAsync(services, scenario, blockedEnvelope);
                using var channel = GrpcChannel.ForAddress(LocalOperatorEndpoint(activeHost!.Urls.Single()), new GrpcChannelOptions
                { HttpHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator } });
                var client = OperatorTestCredentials.Client(channel, OperatorTestCredentials.Credential(activeHost.Urls.Single()));
                var submitted = await client.SubmitGenerationAsync(new GenerationRequest { DeploymentId = blocked.DeploymentId.ToString(), Generation = blocked.Generation.Value }, cancellationToken: token);
                if (submitted.Outcome != "submitted") throw new InvalidOperationException("Configured negative readiness submission failed.");
                await using (var read = await contexts.CreateDbContextAsync(token))
                {
                    var row = await read.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == Guid.Parse(blocked.DeploymentId.ToString()) && x.ActionContract == InternalDeploymentAction.Create, token);
                    intent = DeploymentIntents.Decode(row.Payload);
                }
                var pod = await Startup("primary-started");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                DeploymentReceipt? blockedReceipt = null;
                while (true)
                {
                    pod = await admin.CoreV1.ReadNamespacedPodAsync(pod.Metadata.Name, ns, cancellationToken: deadline.Token);
                    var state = pod.Status.ContainerStatuses?.SingleOrDefault(x => x.Name == "configured-consumer");
                    var deployment = await admin.AppsV1.ReadNamespacedDeploymentAsync(intent.Name, ns, cancellationToken: deadline.Token);
                    if (state is { Started: true, Ready: false, State.Running: not null } &&
                        deployment.Status.ObservedGeneration >= deployment.Metadata.Generation && deployment.Status.UpdatedReplicas == 1 &&
                        (deployment.Status.ReadyReplicas ?? 0) == 0)
                    {
                        blockedReceipt = DeploymentObjects.Receipt(DeploymentObjects.Read(deployment), intent, ConfirmationSource.Observation);
                        break;
                    }
                    await Task.Delay(100, deadline.Token);
                }
                var waited = await client.WaitGenerationAsync(new GenerationWaitRequest
                {
                    DeploymentId = blocked.DeploymentId.ToString(),
                    Generation = blocked.Generation.Value,
                    RequestId = "configured-unready",
                    Settings = new() { MaximumRounds = 1 }
                }, cancellationToken: token);
                await using var finalRead = await contexts.CreateDbContextAsync(token);
                Check(!waited.CurrentReady && !waited.CurrentApplied && waited.Members.Single().Outcome == "Pending" &&
                    pod.Spec.Containers.Single(c => c.Name == "configured-consumer").ReadinessProbe.HttpGet.Path == "/unready" &&
                    pod.Spec.Containers.Single(c => c.Name == "configured-consumer").Ports.Count == 1 &&
                    intent.Target.Ports.Count == 1 && intent.Target.Probes.Readiness!.Path == "/unready" &&
                    (await finalRead.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(blocked.DeploymentId.ToString()), token)).AppliedGeneration is null &&
                    deploymentWrites == 3, "R07-failing-configured-readiness-probe-blocks-fresh-Ready-Applied");
                confirmedDrain.Add(blockedReceipt ?? throw new InvalidOperationException("Confirmed fixture Deployment receipt required."));
            });
            await WithHost(() => ConfiguredDeploymentUpdateCases.Run(admin, services, updateBaseline, updateEnvelope, image,
                identity, deployment, configurationPreparation, requests, async ct =>
                {
                    drainOwnershipRefused = true;
                    try
                    {
                        drainReceipts = await ConfiguredDrainInventory.ReadAsync(contexts, confirmedDrain, ct);
                        drainOwnershipRefused = false;
                        await ConfiguredFixtureDrain.RunAsync(admin, ownedNamespace, drainReceipts, ct);
                    }
                    catch (ConfiguredFixtureDrain.OwnershipRefused) { drainOwnershipRefused = true; throw; }
                }, token, lifetime));
        }
        catch (Exception error) { lifetime.Functional.Fail(); primary = error; throw; }
        finally
        {
            var cleanup = new List<Exception>();
            if (ownedNamespace is not null)
            {
                try
                {
                    Console.WriteLine("Q243 configured cleanup-namespace start");
                    using var budget = lifetime.Start(ConfiguredLifecycle.Kind.NamespaceCleanup);
                    if (drainOwnershipRefused) throw new InvalidOperationException("Namespace deletion refused after drain ownership conflict.");
                    if (drainReceipts is not null) _ = await ConfiguredFixtureDrain.VerifyOwnedAsync(admin, ownedNamespace, drainReceipts, budget.Token);
                    await admin.CoreV1.DeleteNamespaceAsync(ns, new V1DeleteOptions { Preconditions = new() { Uid = ownedNamespace.Metadata.Uid } }, cancellationToken: budget.Token);
                    while (true)
                    {
                        try { await admin.CoreV1.ReadNamespaceAsync(ns, cancellationToken: budget.Token); }
                        catch (k8s.Autorest.HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound) { break; }
                        await Task.Delay(100, budget.Token);
                    }
                    Console.WriteLine("Q243 configured cleanup-namespace completed");
                    Console.WriteLine("PASS configured-owned-namespace-absent");
                    budget.Complete();
                }
                catch (Exception error) { Console.WriteLine("Q243 configured cleanup-namespace failed"); Console.WriteLine(ConfiguredRetirementDiagnostics.Failure("NamespaceCleanup", error)); cleanup.Add(error); }
            }
            Console.WriteLine("Q243 configured cleanup-catalog start");
            try { await ((Xunit.IAsyncLifetime)postgres).DisposeAsync(); Console.WriteLine("Q243 configured cleanup-catalog completed"); Console.WriteLine("PASS configured-owned-catalog-cleanup"); }
            catch (Exception error) { Console.WriteLine("Q243 configured cleanup-catalog failed"); cleanup.Add(error); }
            if (cleanup.Count > 0) throw ConfiguredLifecycle.Combine(primary, cleanup);
        }
    }
    // Only this fixture's local operator clients use loopback; the listener and Pod delivery address stay unchanged.
    internal static Uri LocalOperatorEndpoint(string listener)
    {
        var address = new Uri(listener);
        if (address.Scheme != "https" || address.Host != IPAddress.Any.ToString())
            throw new InvalidOperationException("Unexpected configured fixture listener.");
        return new UriBuilder(address) { Host = IPAddress.Loopback.ToString() }.Uri;
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
    internal static void Check(bool value, string id)
    {
        var prefix = id.Split('-')[0];
        var stage = prefix is "C01" or "C02" or "C03" or "C04" or "C05" or "C06" or "C07" or "C08" or "C09" or "C10" or "C11" or "C12" or "C13" or "R01" or "R02" or "R03" or "R04" or "R05" or "R06" or "R07" or "B01" or "B02" or "B03" or "B04" or "A01" or "A02" or "A03" ? prefix : "unknown";
        if (!value) Console.WriteLine($"Q243 configured {stage} failed");
        if (!value) throw new InvalidOperationException("Configured Deployment case failed: " + id);
        Console.WriteLine("PASS configured-" + id);
    }
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
