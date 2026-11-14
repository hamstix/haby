using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql.IntegrationTests;
using Haby.Tests;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

internal static class DurableActionCases
{
    private sealed class InjectedCrash : Exception;
    private enum ReplacementPhase { ReadOriginal, DeleteOriginal, ObserveDeletion, CreateReplacement, ObserveForeign }
    private static readonly ProviderInput Subject = new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main"));
    internal static async Task Run(Kubernetes admin, Uri endpoint, string directory, CancellationToken token)
    {
        // Reuse the ordinary owned PostgreSQL fixture and real public P2 baseline.
        // This tool is test composition, never a production dependency or a second launcher.
        var postgres = new PostgreSqlFixture();
        await postgres.InitializeAsync();
        var name = "haby202-" + Guid.NewGuid().ToString("N")[..12];
        V1Namespace? ns = null;
        Exception? primary = null;
        try
        {
            ns = await admin.CoreV1.CreateNamespaceAsync(new() { Metadata = new() { Name = name, Labels = new Dictionary<string, string> { ["haby.dev/qualification"] = name } } }, cancellationToken: token);
            var setup = new DurableDeploymentPlanningTests(postgres);
            async Task<KubernetesProviderConfiguration> Configuration(ISecretStore store, EnvironmentRecordId environment)
            {
                async Task<SecretRef> Save(string file)
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, file), token);
                    try
                    {
                        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
                        await store.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), reference, bytes, token);
                        return reference;
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                return new(endpoint, await File.ReadAllTextAsync(Path.Combine(directory, "ca.crt"), token), null,
                    await Save("admin.crt"), await Save("admin.key"));
            }
            async ValueTask<ProviderPreflightResult> Preflight(ISecretStore store, EnvironmentRecordId environment,
                ProviderAliasBinding target, KubernetesProviderConfiguration configuration, CancellationToken cancellationToken)
            {
                var binding = new KubernetesBinding(environment.ToString(), target.InstanceId.ToString(), target.RevisionNumber.Value,
                    KubernetesProviderConfigurationCodec.Implementation);
                var connection = new KubernetesConnection(binding, configuration.Endpoint, configuration.ServerCaPem,
                    configuration.BearerToken, configuration.ClientCertificate, configuration.ClientPrivateKey);
                try
                {
                    // Actual fixture credentials, chain/hostname checks and authenticated API
                    // permissions. No Deployment or other persistent workload is created.
                    using var client = await KubernetesClientFactory.CreateFixture(connection, store, [], cancellationToken);
                    await client.Client.Version.GetCodeAsync(cancellationToken);
                    var identity = await client.Client.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: cancellationToken);
                    if (string.IsNullOrEmpty(identity.Status?.UserInfo?.Username) || identity.Status.UserInfo.Username == "system:anonymous")
                        return new(ProviderPreflightOutcome.AuthenticationFailed);
                    await client.Client.CoreV1.ReadNamespaceAsync(name, cancellationToken: cancellationToken);
                    foreach (var verb in new[] { "get", "create", "patch" })
                    {
                        var review = await client.Client.AuthorizationV1.CreateSelfSubjectAccessReviewAsync(new V1SelfSubjectAccessReview
                        { Spec = new() { ResourceAttributes = new() { NamespaceProperty = name, Group = "apps", Resource = "deployments", Verb = verb } } }, cancellationToken: cancellationToken);
                        if (review.Status?.Allowed != true) return new(ProviderPreflightOutcome.InsufficientPrivilege);
                    }
                    return new(ProviderPreflightOutcome.Ready);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Unauthorized) { return new(ProviderPreflightOutcome.AuthenticationFailed); }
                catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Forbidden) { return new(ProviderPreflightOutcome.InsufficientPrivilege); }
                catch (Exception error) when (error is ArgumentException or CryptographicException) { return new(ProviderPreflightOutcome.ConfigurationUnavailable); }
                catch (Exception error) when (error is HttpRequestException or HttpOperationException) { return new(ProviderPreflightOutcome.ServerUnavailable); }
            }
            async Task<(DurableDeploymentPlanningTests.InternalScenario Scenario, DurableOperationKey Key, Counter Counter)> Accepted(Action<InternalActionFault>? fault = null, bool initialOnly = false)
            {
                Console.WriteLine(initialOnly ? "Durable phase: imported-initial-Workload-only-generation" : "Durable phase: arrange-real-Resource-and-accepted-mixed-generation");
                var scenario = await setup.InternalScenarioAsync(Configuration, name, actualKubernetesProbe: Preflight, initialOnly: initialOnly);
                await using var services = DurableDeploymentPlanningTests.InternalServices(scenario.Connection, scenario.Time);
                var counter = new Counter(scenario.Connection);
                var actions = Compose(services, counter, fault);
                var key = await actions.AcceptAsync(scenario.Commit, Subject, InternalDeploymentAction.Create, "create", token);
                counter.Key = key;
                return (scenario, key, counter);
            }

            // API acceptance followed by a deliberately dropped response. Preserve catalog,
            // restart the service on a real pg_dump/restore and learn the original UID by read.
            var lost = await Accepted(); lost.Counter.DropResponse = true;
            await using (var services = DurableDeploymentPlanningTests.InternalServices(lost.Scenario.Connection, lost.Scenario.Time))
                Check((await Compose(services, lost.Counter).ExecuteAsync(lost.Key, token)).Outcome == InternalActionOutcome.Uncertain, "D01-accepted-response-lost");
            var restored = await Restore(postgres, lost.Scenario.Connection, token);
            lost.Counter.Connection = restored; lost.Counter.DropResponse = false;
            await using (var services = DurableDeploymentPlanningTests.InternalServices(restored, lost.Scenario.Time))
            {
                await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync(token);
                var actions = Compose(services, lost.Counter);
                var result = await actions.RecoverByObservationAsync(lost.Key, token);
                var history = (await actions.HistoryAsync(lost.Key, token))!;
                Check(result.Outcome == InternalActionOutcome.Confirmed && DeploymentIntents.DecodeReceipt(result.Receipt!).Uid == lost.Counter.AcceptedUid &&
                    DeploymentIntents.DecodeReceipt(result.Receipt!).Source == ConfirmationSource.Observation && lost.Counter.Writes == 1 &&
                    history.Attempts is [var attempt] && attempt.Outcome == "Uncertain" && history.Observations is [var observation] && observation.Confirmed &&
                    history.Output?.Payload == result.Receipt, "D02-restored-observation-original-uid-one-write");
                var reads = lost.Counter.Reads;
                Check((await actions.ExecuteAsync(lost.Key, token)).Receipt == result.Receipt && lost.Counter.Reads == reads && lost.Counter.Writes == 1,
                    "D03-terminal-replay-no-api");
                await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync(token);
                Check((await verify.ApplicationDeployments.AsNoTracking().SingleAsync(token)).AppliedGeneration == 1, "D04-applied-remains-baseline");
            }

            var absent = await Accepted();
            await using (var services = DurableDeploymentPlanningTests.InternalServices(absent.Scenario.Connection, absent.Scenario.Time))
            {
                try { await Compose(services, absent.Counter, point => { if (point == InternalActionFault.AfterMarkerCommit) throw new IOException("Injected interruption."); }).ExecuteAsync(absent.Key, token); throw new InvalidOperationException("Missing interruption."); }
                catch (IOException) { }
            }
            await using (var services = DurableDeploymentPlanningTests.InternalServices(absent.Scenario.Connection, absent.Scenario.Time))
            {
                var actions = Compose(services, absent.Counter); absent.Counter.Unavailable = true;
                Check((await actions.RecoverByObservationAsync(absent.Key, token)).Outcome == InternalActionOutcome.Unavailable, "D05-unavailable-observation-no-replay");
                absent.Counter.Unavailable = false;
                var result = await actions.ExecuteAsync(absent.Key, token); var history = (await actions.HistoryAsync(absent.Key, token))!;
                Check(result.Outcome == InternalActionOutcome.NotConfirmed && absent.Counter.Writes == 0 && history.Operation.State == "NeedsAttention" &&
                    history.Attempts is [var attempt] && attempt.Outcome == "Uncertain" && history.Observations.Count == 2 && history.Observations.All(x => !x.Confirmed) && history.Output is null,
                    "D06-marker-before-send-absence-stays-uncertain-zero-writes");
            }

            var interrupted = await Accepted();
            await using (var services = DurableDeploymentPlanningTests.InternalServices(interrupted.Scenario.Connection, interrupted.Scenario.Time))
            {
                try { await Compose(services, interrupted.Counter, point => { if (point == InternalActionFault.AfterExternalResult) throw new IOException("Injected interruption."); }).ExecuteAsync(interrupted.Key, token); throw new InvalidOperationException("Missing interruption."); }
                catch (IOException) { }
            }
            await using (var services = DurableDeploymentPlanningTests.InternalServices(interrupted.Scenario.Connection, interrupted.Scenario.Time))
            {
                var actions = Compose(services, interrupted.Counter); var before = (await actions.HistoryAsync(interrupted.Key, token))!;
                Check(before.Output is null && before.Operation.State == "Dispatching", "D07-accepted-before-receipt-crash");
                var intent = DeploymentIntents.Decode(before.Intent.Payload);
                async Task Patch(string patch) => await admin.AppsV1.PatchNamespacedDeploymentAsync(new V1Patch(patch, V1Patch.PatchType.JsonPatch), intent.Name, name, cancellationToken: token);
                await Patch("[{\"op\":\"replace\",\"path\":\"/metadata/annotations/haby.dev~1write-operation\",\"value\":\"" + Guid.NewGuid().ToString("N") + "\"}]");
                Check((await actions.RecoverByObservationAsync(interrupted.Key, token)).Outcome == InternalActionOutcome.NotConfirmed && interrupted.Counter.Writes == 1, "D08-foreign-marker-never-confirms");
                await Patch("[{\"op\":\"replace\",\"path\":\"/metadata/annotations/haby.dev~1write-operation\",\"value\":\"" + intent.Operation.ToString("N") + "\"},{\"op\":\"replace\",\"path\":\"/spec/template/spec/containers/0/image\",\"value\":\"registry.example/foreign:v1\"}]");
                Check((await actions.RecoverByObservationAsync(interrupted.Key, token)).Outcome == InternalActionOutcome.NotConfirmed && interrupted.Counter.Writes == 1, "D09-foreign-projection-never-confirms");
                await Patch("[{\"op\":\"replace\",\"path\":\"/spec/template/spec/containers/0/image\",\"value\":\"" + intent.Target.Image + "\"}]");
                var result = await actions.RecoverByObservationAsync(interrupted.Key, token);
                Check(result.Outcome == InternalActionOutcome.Confirmed && interrupted.Counter.Writes == 1 &&
                    (await actions.HistoryAsync(interrupted.Key, token))!.Attempts is [var attempt] && attempt.Outcome == "Uncertain", "D10-restart-confirms-without-rewriting-attempt");
            }
            var replaced = await Accepted(); replaced.Counter.DropResponse = true;
            await using (var services = DurableDeploymentPlanningTests.InternalServices(replaced.Scenario.Connection, replaced.Scenario.Time))
            {
                var actions = Compose(services, replaced.Counter);
                Check((await actions.ExecuteAsync(replaced.Key, token)).Outcome == InternalActionOutcome.Uncertain, "replacement-fixture-original-uncertain");
                var saved = (await actions.HistoryAsync(replaced.Key, token))!;
                var intent = DeploymentIntents.Decode(saved.Intent.Payload);
                var phase = ReplacementPhase.ReadOriginal;
                void Stage(ReplacementPhase value) { phase = value; Console.WriteLine($"Durable replacement phase: {phase}"); }
                try
                {
                    Stage(ReplacementPhase.ReadOriginal);
                    var live = await admin.AppsV1.ReadNamespacedDeploymentAsync(intent.Name, name, cancellationToken: token);
                    if (string.IsNullOrEmpty(live.Metadata.Uid)) throw new InvalidOperationException("Original Deployment identity is unavailable.");
                    Stage(ReplacementPhase.DeleteOriginal);
                    await admin.AppsV1.DeleteNamespacedDeploymentAsync(intent.Name, name, new V1DeleteOptions { Preconditions = new() { Uid = live.Metadata.Uid }, PropagationPolicy = "Orphan" }, cancellationToken: token);
                    Stage(ReplacementPhase.ObserveDeletion);
                    await DeploymentReplacementObservation.WaitAsync(live.Metadata.Uid, async ct =>
                    {
                        try
                        {
                            return (await admin.AppsV1.ReadNamespacedDeploymentAsync(intent.Name, name, cancellationToken: ct)).Metadata.Uid
                            ?? throw new InvalidOperationException("Observed Deployment identity is unavailable.");
                        }
                        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound) { return null; }
                    }, ct => Task.Delay(100, ct), token);
                    Console.WriteLine("Durable replacement status: original-absent");
                    Stage(ReplacementPhase.CreateReplacement);
                    var foreign = await admin.AppsV1.CreateNamespacedDeploymentAsync(new V1Deployment
                    {
                        ApiVersion = "apps/v1",
                        Kind = "Deployment",
                        Spec = live.Spec,
                        Metadata = new()
                        {
                            Name = intent.Name,
                            NamespaceProperty = name,
                            Labels = new Dictionary<string, string> { [DeploymentIntents.OwnerKey] = Guid.NewGuid().ToString("N"), [DeploymentIntents.ManagedKey] = intent.ManagedWorkload.ToString("N") },
                            Annotations = new Dictionary<string, string> { [DeploymentIntents.OperationKey] = Guid.NewGuid().ToString("N"), [DeploymentIntents.GenerationKey] = intent.TargetGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                        }
                    }, name, cancellationToken: token);
                    replaced.Counter.DropResponse = false;
                    Stage(ReplacementPhase.ObserveForeign);
                    var result = await actions.RecoverByObservationAsync(replaced.Key, token);
                    Check(foreign.Metadata.Uid != live.Metadata.Uid && result.Outcome == InternalActionOutcome.NotConfirmed && replaced.Counter.Writes == 1 &&
                        (await actions.HistoryAsync(replaced.Key, token))!.Output is null, "D11-foreign-replacement-uid-never-adopted");
                }
                catch (HttpOperationException error)
                {
                    var status = error.Response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "401",
                        HttpStatusCode.Forbidden => "403",
                        HttpStatusCode.NotFound => "404",
                        HttpStatusCode.Conflict => "409",
                        HttpStatusCode.TooManyRequests => "429",
                        HttpStatusCode.InternalServerError => "500",
                        _ => "other"
                    };
                    Console.WriteLine($"Durable replacement failure: phase={phase}; status={status}");
                    throw;
                }
            }
            var initial = await Accepted(initialOnly: true);
            await using (var services = DurableDeploymentPlanningTests.InternalServices(initial.Scenario.Connection, initial.Scenario.Time))
            {
                var actions = Compose(services, initial.Counter);
                var result = await actions.ExecuteAsync(initial.Key, token);
                var history = (await actions.HistoryAsync(initial.Key, token))!;
                Check(result.Outcome == InternalActionOutcome.Confirmed && history.Attempts is [var attempt] && attempt.Outcome == "Succeeded" &&
                    history.Output?.Payload == result.Receipt && initial.Counter.Writes == 1 && initial.Counter.Barriers == 1,
                    "D14-initial-create-real-api-confirmed-one-write");
                var replay = await actions.ExecuteAsync(initial.Key, token);
                await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync(token);
                var generation = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(token);
                var proof = await DurableAggregateCompletion.ReadAsync(verify, generation, token, true);
                Check(replay.Receipt == result.Receipt && initial.Counter.Writes == 1 && proof is not null && proof.Resources.Count == 0 &&
                    (await verify.ApplicationDeployments.AsNoTracking().SingleAsync(token)).AppliedGeneration is null &&
                    !await verify.ResourceManagement.AnyAsync(token) && await verify.ProviderInstances.CountAsync(token) == 1,
                    "D15-initial-empty-proof-replay-no-Resource-no-Applied");
            }
            var initialLost = await Accepted(initialOnly: true); initialLost.Counter.DropResponse = true;
            await using (var services = DurableDeploymentPlanningTests.InternalServices(initialLost.Scenario.Connection, initialLost.Scenario.Time))
                Check((await Compose(services, initialLost.Counter).ExecuteAsync(initialLost.Key, token)).Outcome == InternalActionOutcome.Uncertain,
                    "D16-initial-lost-response-preserves-uncertainty");
            var initialRestored = await Restore(postgres, initialLost.Scenario.Connection, token);
            initialLost.Counter.Connection = initialRestored; initialLost.Counter.DropResponse = false;
            await using (var services = DurableDeploymentPlanningTests.InternalServices(initialRestored, initialLost.Scenario.Time))
            {
                await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync(token);
                var actions = Compose(services, initialLost.Counter);
                var result = await actions.RecoverByObservationAsync(initialLost.Key, token);
                var history = (await actions.HistoryAsync(initialLost.Key, token))!;
                await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync(token);
                var proof = await DurableAggregateCompletion.ReadAsync(verify, await verify.DeploymentGenerations.AsNoTracking().SingleAsync(token), token, true);
                Check(result.Outcome == InternalActionOutcome.Confirmed && DeploymentIntents.DecodeReceipt(result.Receipt!).Uid == initialLost.Counter.AcceptedUid &&
                    history.Attempts is [var attempt] && attempt.Outcome == "Uncertain" && history.Observations is [var observation] && observation.Confirmed &&
                    initialLost.Counter.Writes == 1 && history.Output?.Payload == result.Receipt && proof?.Id == history.Intent.CheckpointId &&
                    (await verify.ApplicationDeployments.AsNoTracking().SingleAsync(token)).AppliedGeneration is null,
                    "D17-initial-restored-observation-retains-empty-proof-null-Applied");
            }
            var initialAbsent = await Accepted(initialOnly: true);
            await using (var services = DurableDeploymentPlanningTests.InternalServices(initialAbsent.Scenario.Connection, initialAbsent.Scenario.Time))
            {
                try { await Compose(services, initialAbsent.Counter, point => { if (point == InternalActionFault.AfterMarkerCommit) throw new IOException("Injected interruption."); }).ExecuteAsync(initialAbsent.Key, token); throw new InvalidOperationException("Missing interruption."); }
                catch (IOException) { }
            }
            await using (var services = DurableDeploymentPlanningTests.InternalServices(initialAbsent.Scenario.Connection, initialAbsent.Scenario.Time))
            {
                var actions = Compose(services, initialAbsent.Counter);
                var result = await actions.RecoverByObservationAsync(initialAbsent.Key, token);
                var history = (await actions.HistoryAsync(initialAbsent.Key, token))!;
                Check(result.Outcome == InternalActionOutcome.NotConfirmed && initialAbsent.Counter.Writes == 0 && history.Output is null &&
                    history.Operation.State == "NeedsAttention" && history.Attempts is [var attempt] && attempt.Outcome == "Uncertain",
                    "D18-initial-marker-before-send-absence-no-replay");
            }
            async Task<(DurableDeploymentPlanningTests.InternalScenario Scenario, DurableOperationKey Key, Counter Counter)> RuntimeAccepted(
                bool initial = true, string? targetNamespace = null,
                Func<ISecretStore, EnvironmentRecordId, Task<KubernetesProviderConfiguration>>? configuration = null)
            {
                var scenario = await setup.InternalScenarioAsync(configuration ?? Configuration, targetNamespace ?? name,
                    actualKubernetesProbe: configuration is null ? Preflight : null, initialOnly: initial,
                    configureManifest: e => DurableDeploymentPlanningTests.ConfiguredDocuments(e, secondDocument: true));
                await using var services = DurableDeploymentPlanningTests.InternalServices(scenario.Connection, scenario.Time);
                var documents = await DurableDeploymentPlanningTests.ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "qualifier", token);
                if (documents.Failure is not null || documents.Prepared.Count != 2) throw new InvalidOperationException("Exact runtime documents unavailable.");
                var counter = new Counter(scenario.Connection);
                var actions = ComposeRuntime(services, counter);
                var key = await actions.AcceptAsync(scenario.Commit, Subject, InternalRuntimeIdentityAction.Create, "runtime-identity", token);
                counter.Key = key; return (scenario, key, counter);
            }
            async Task<(Guid Binding, string Audience, string Receipt)> RuntimeSaved(string connection)
            {
                await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
                var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(token);
                var inventory = await db.OwnedObjects.AsNoTracking().SingleAsync(token);
                if (await db.ConfigurationAccess.AnyAsync(token)) throw new InvalidOperationException("Runtime preparation granted static access.");
                return (binding.Id, binding.Audience, inventory.Payload);
            }
            var accountsBefore = (await admin.CoreV1.ListNamespacedServiceAccountAsync(name, cancellationToken: token)).Items.Count;
            var rolesBefore = (await admin.RbacAuthorizationV1.ListNamespacedRoleBindingAsync(name, cancellationToken: token)).Items.Count;
            var secretsBefore = (await admin.CoreV1.ListNamespacedSecretAsync(name, cancellationToken: token)).Items.Count;
            var runtime = await RuntimeAccepted();
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtime.Scenario.Connection, runtime.Scenario.Time))
            {
                var result = await ComposeRuntime(services, runtime.Counter).ExecuteAsync(runtime.Key, token);
                var receipt = RuntimeIdentityCodec.Receipt(result.Receipt!);
                var account = await admin.CoreV1.ReadNamespacedServiceAccountAsync(receipt.Intent.Name, name, cancellationToken: token);
                Check(result.Outcome == InternalActionOutcome.Confirmed && runtime.Counter.Writes == 1 && account.AutomountServiceAccountToken == false &&
                    account.Metadata.Uid == receipt.Uid, "D19-runtime-initial-owned-disabled-automount");
            }
            var originalRuntime = await RuntimeSaved(runtime.Scenario.Connection);
            var runtimeRestored = await Restore(postgres, runtime.Scenario.Connection, token);
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtimeRestored, runtime.Scenario.Time))
            {
                var counter = new Counter(runtimeRestored) { Key = runtime.Key };
                var result = await ComposeRuntime(services, counter).ExecuteAsync(runtime.Key, token);
                var saved = await RuntimeSaved(runtimeRestored);
                Check(result.Outcome == InternalActionOutcome.Confirmed && counter.Writes == 0 && saved == originalRuntime,
                    "D20-runtime-current-restore-retains-binding-audience-uid");
            }
            Check((await admin.CoreV1.ListNamespacedServiceAccountAsync(name, cancellationToken: token)).Items.Count == accountsBefore + 1 &&
                (await admin.RbacAuthorizationV1.ListNamespacedRoleBindingAsync(name, cancellationToken: token)).Items.Count == rolesBefore &&
                (await admin.CoreV1.ListNamespacedSecretAsync(name, cancellationToken: token)).Items.Count == secretsBefore,
                "D30-runtime-no-rbac-or-token-secret-side-effects");
            var runtimeMixed = await RuntimeAccepted(initial: false);
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtimeMixed.Scenario.Connection, runtimeMixed.Scenario.Time))
            {
                var result = await ComposeRuntime(services, runtimeMixed.Counter).ExecuteAsync(runtimeMixed.Key, token);
                await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync(token);
                Check(result.Outcome == InternalActionOutcome.Confirmed && runtimeMixed.Counter.Writes == 1 &&
                    (await db.ApplicationDeployments.AsNoTracking().SingleAsync(token)).AppliedGeneration == 1 && !await db.ConfigurationAccess.AnyAsync(token),
                    "D21-runtime-mixed-exact-preparation-no-access-or-Applied");
            }
            var runtimeLost = await RuntimeAccepted(); runtimeLost.Counter.DropResponse = true;
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtimeLost.Scenario.Connection, runtimeLost.Scenario.Time))
                Check((await ComposeRuntime(services, runtimeLost.Counter).ExecuteAsync(runtimeLost.Key, token)).Outcome == InternalActionOutcome.Uncertain,
                    "D31-runtime-lost-response-preserves-original-dispatch");
            runtimeLost.Counter.DropResponse = false;
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtimeLost.Scenario.Connection, runtimeLost.Scenario.Time))
            {
                var history = (await ComposeRuntime(services, runtimeLost.Counter).HistoryAsync(runtimeLost.Key, token))!;
                var intent = RuntimeIdentityCodec.Decode(history.Intent.Payload);
                var account = await admin.CoreV1.ReadNamespacedServiceAccountAsync(intent.Name, name, cancellationToken: token);
                account.AutomountServiceAccountToken = true;
                await admin.CoreV1.ReplaceNamespacedServiceAccountAsync(account, intent.Name, name, cancellationToken: token);
                Check((await ComposeRuntime(services, runtimeLost.Counter).RecoverByObservationAsync(runtimeLost.Key, token)).Outcome == InternalActionOutcome.NotConfirmed && runtimeLost.Counter.Writes == 1,
                    "D29-runtime-observation-rejects-owned-projection-edit");
                account = await admin.CoreV1.ReadNamespacedServiceAccountAsync(intent.Name, name, cancellationToken: token);
                account.AutomountServiceAccountToken = false;
                await admin.CoreV1.ReplaceNamespacedServiceAccountAsync(account, intent.Name, name, cancellationToken: token);
            }
            var lostRestored = await Restore(postgres, runtimeLost.Scenario.Connection, token);
            await using (var services = DurableDeploymentPlanningTests.InternalServices(lostRestored, runtimeLost.Scenario.Time))
            {
                var counter = new Counter(lostRestored) { Key = runtimeLost.Key };
                var actions = ComposeRuntime(services, counter);
                var result = await actions.RecoverByObservationAsync(runtimeLost.Key, token);
                var history = (await actions.HistoryAsync(runtimeLost.Key, token))!;
                _ = await RuntimeSaved(lostRestored);
                Check(result.Outcome == InternalActionOutcome.Confirmed && counter.Writes == 0 && runtimeLost.Counter.Writes == 1 &&
                    history.Attempts is [var attempt] && attempt.Outcome == "Uncertain" && history.Observations.Last().Confirmed,
                    "D32-runtime-lost-response-restored-observation-one-original-write");
            }
            var runtimeCrash = await RuntimeAccepted();
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtimeCrash.Scenario.Connection, runtimeCrash.Scenario.Time))
            {
                try { await ComposeRuntime(services, runtimeCrash.Counter, p => { if (p == InternalActionFault.BeforeCompletionCommit) throw new InjectedCrash(); }).ExecuteAsync(runtimeCrash.Key, token); }
                catch (InjectedCrash) { }
                await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync(token);
                if (await db.RuntimeIdentityBindings.AnyAsync(token) || await db.OwnedObjects.AnyAsync(token)) throw new InvalidOperationException("Partial runtime confirmation committed.");
            }
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtimeCrash.Scenario.Connection, runtimeCrash.Scenario.Time))
            {
                Check((await ComposeRuntime(services, runtimeCrash.Counter).RecoverByObservationAsync(runtimeCrash.Key, token)).Outcome == InternalActionOutcome.Confirmed &&
                    runtimeCrash.Counter.Writes == 1 && (await RuntimeSaved(runtimeCrash.Scenario.Connection)).Binding != Guid.Empty,
                    "D22-runtime-prebinding-crash-atomic-observation-recovery");
            }
            var runtimeAbsent = await RuntimeAccepted();
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtimeAbsent.Scenario.Connection, runtimeAbsent.Scenario.Time))
            {
                try { await ComposeRuntime(services, runtimeAbsent.Counter, p => { if (p == InternalActionFault.AfterMarkerCommit) throw new InjectedCrash(); }).ExecuteAsync(runtimeAbsent.Key, token); }
                catch (InjectedCrash) { }
            }
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtimeAbsent.Scenario.Connection, runtimeAbsent.Scenario.Time))
                Check((await ComposeRuntime(services, runtimeAbsent.Counter).RecoverByObservationAsync(runtimeAbsent.Key, token)).Outcome == InternalActionOutcome.NotConfirmed && runtimeAbsent.Counter.Writes == 0,
                    "D23-runtime-marker-before-send-absence-never-replays");
            var savedIntent = RuntimeIdentityCodec.Receipt(originalRuntime.Receipt).Intent;
            await admin.CoreV1.DeleteNamespacedServiceAccountAsync(savedIntent.Name, name, new V1DeleteOptions { Preconditions = new() { Uid = runtime.Counter.AcceptedUid } }, cancellationToken: token);
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtime.Scenario.Connection, runtime.Scenario.Time))
                Check((await ComposeRuntime(services, runtime.Counter).ExecuteAsync(runtime.Key, token)).Outcome == InternalActionOutcome.NotConfirmed && runtime.Counter.Writes == 1,
                    "D25-runtime-known-missing-account-no-recreation");
            await admin.CoreV1.CreateNamespacedServiceAccountAsync(new V1ServiceAccount
            {
                Metadata = new()
                {
                    Name = savedIntent.Name,
                    Labels = new Dictionary<string, string> { [DeploymentIntents.OwnerKey] = savedIntent.Owner.ToString("N"), [DeploymentIntents.ManagedKey] = savedIntent.ManagedWorkload.ToString("N") },
                    Annotations = new Dictionary<string, string> { [DeploymentIntents.OperationKey] = savedIntent.Operation.ToString("N"), [DeploymentIntents.GenerationKey] = savedIntent.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                },
                AutomountServiceAccountToken = false
            }, name, cancellationToken: token);
            await using (var services = DurableDeploymentPlanningTests.InternalServices(runtime.Scenario.Connection, runtime.Scenario.Time))
                Check((await ComposeRuntime(services, runtime.Counter).ExecuteAsync(runtime.Key, token)).Outcome != InternalActionOutcome.Confirmed &&
                    runtime.Counter.Writes == 1 && (await RuntimeSaved(runtime.Scenario.Connection)) == originalRuntime,
                    "D24-runtime-foreign-replacement-never-remaps-binding");
            var foreignRuntime = await RuntimeAccepted();
            await using (var services = DurableDeploymentPlanningTests.InternalServices(foreignRuntime.Scenario.Connection, foreignRuntime.Scenario.Time))
            {
                var actions = ComposeRuntime(services, foreignRuntime.Counter); var intent = RuntimeIdentityCodec.Decode((await actions.HistoryAsync(foreignRuntime.Key, token))!.Intent.Payload);
                await admin.CoreV1.CreateNamespacedServiceAccountAsync(new V1ServiceAccount { Metadata = new() { Name = intent.Name }, AutomountServiceAccountToken = false }, name, cancellationToken: token);
                Check((await actions.ExecuteAsync(foreignRuntime.Key, token)).Outcome == InternalActionOutcome.Refused && foreignRuntime.Counter.Writes == 0,
                    "D26-runtime-occupied-foreign-name-no-adoption");
            }
            var missingNamespace = await RuntimeAccepted(targetNamespace: name + "-absent");
            await using (var services = DurableDeploymentPlanningTests.InternalServices(missingNamespace.Scenario.Connection, missingNamespace.Scenario.Time))
                Check((await ComposeRuntime(services, missingNamespace.Counter).ExecuteAsync(missingNamespace.Key, token)).Outcome == InternalActionOutcome.Refused && missingNamespace.Counter.Writes == 0,
                    "D27-runtime-missing-selected-namespace-no-create");
            await admin.CoreV1.CreateNamespacedServiceAccountAsync(new V1ServiceAccount { Metadata = new() { Name = "unprivileged-probe" }, AutomountServiceAccountToken = false }, name, cancellationToken: token);
            var issued = await admin.CoreV1.CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest { Spec = new() { Audiences = [], ExpirationSeconds = 600 } }, "unprivileged-probe", name, cancellationToken: token);
            async Task<KubernetesProviderConfiguration> Restricted(ISecretStore store, EnvironmentRecordId environment)
            {
                var reference = new SecretRef(SecretId.New(), SecretVersionId.New()); var bytes = System.Text.Encoding.UTF8.GetBytes(issued.Status.Token);
                try { await store.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), reference, bytes, token); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                return new(endpoint, await File.ReadAllTextAsync(Path.Combine(directory, "ca.crt"), token), reference, null, null);
            }
            var deniedRuntime = await RuntimeAccepted(configuration: Restricted);
            await using (var services = DurableDeploymentPlanningTests.InternalServices(deniedRuntime.Scenario.Connection, deniedRuntime.Scenario.Time))
                Check((await ComposeRuntime(services, deniedRuntime.Counter).ExecuteAsync(deniedRuntime.Key, token)).Outcome == InternalActionOutcome.Refused && deniedRuntime.Counter.Writes == 0,
                    "D28-runtime-required-permission-denial-no-create");
            Check(lost.Counter.Barriers == 1 && interrupted.Counter.Barriers == 1 && absent.Counter.Barriers == 0 &&
                lost.Counter.TransactionChecks > 0 && interrupted.Counter.TransactionChecks > 0 && absent.Counter.TransactionChecks > 0, "D12-committed-marker-no-network-under-catalog-transaction");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                if (ns is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var current = await admin.CoreV1.ReadNamespaceAsync(name, cancellationToken: cleanup.Token);
                    if (current.Metadata.Uid != ns.Metadata.Uid || current.Metadata.Labels["haby.dev/qualification"] != name) throw new InvalidOperationException("Namespace cleanup ownership mismatch.");
                    await admin.CoreV1.DeleteNamespaceAsync(name, new V1DeleteOptions { Preconditions = new() { Uid = ns.Metadata.Uid } }, cancellationToken: cleanup.Token);
                    var absent = false;
                    for (var n = 0; n < 100; n++)
                    {
                        try { await admin.CoreV1.ReadNamespaceAsync(name, cancellationToken: cleanup.Token); }
                        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound) { absent = true; break; }
                        await Task.Delay(200, cleanup.Token);
                    }
                    Check(absent, "D13-owned-namespace-absent");
                }
            }
            catch (Exception error) { cleanupFailure = error; }
            try { await postgres.DisposeAsync(); Console.WriteLine("PASS durable-owned-catalog-cleanup"); }
            catch (Exception error) { cleanupFailure = cleanupFailure is null ? error : new AggregateException(cleanupFailure, error); }
            if (cleanupFailure is not null) throw primary is null ? cleanupFailure : new AggregateException(primary, cleanupFailure);
        }
    }
    private static InternalDurableActions Compose(ServiceProvider services, Counter counter, Action<InternalActionFault>? fault = null)
    {
        var secrets = services.GetRequiredService<ISecretStore>();
        var handler = new InternalDeploymentAction(secrets, connection => new DeploymentKernel(connection.Binding,
            ct => KubernetesClientFactory.CreateFixture(connection, secrets, [new BoundaryHandler(counter)], ct)));
        return DurableDeploymentPlanningTests.Actions(services, handler, fault);
    }
    private static InternalDurableActions ComposeRuntime(ServiceProvider services, Counter counter, Action<InternalActionFault>? fault = null)
    {
        var secrets = services.GetRequiredService<ISecretStore>();
        var handler = new InternalRuntimeIdentityAction(secrets, (connection, ct) => KubernetesClientFactory.CreateFixture(connection, secrets, [new BoundaryHandler(counter)], ct));
        return DurableDeploymentPlanningTests.IdentityActions(services, handler, fault);
    }
    private static async Task<string> Restore(PostgreSqlFixture fixture, string source, CancellationToken token)
    {
        var target = "haby202_restore_" + Guid.NewGuid().ToString("N");
        await using (var db = new NpgsqlConnection(fixture.ConnectionString))
        {
            await db.OpenAsync(token); await new NpgsqlCommand("CREATE DATABASE \"" + target + "\"", db).ExecuteNonQueryAsync(token);
        }
        var directory = "/tmp/haby202-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(source).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + target, dump);
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
        return new NpgsqlConnectionStringBuilder(source) { Database = target }.ConnectionString;
    }
    private sealed class Counter(string connection)
    {
        internal string Connection = connection;
        internal DurableOperationKey Key = null!;
        internal int Writes, Reads, Barriers, TransactionChecks;
        internal bool DropResponse, Unavailable;
        internal string? AcceptedUid;
    }
    private sealed class BoundaryHandler(Counter counter) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var review = request.RequestUri!.AbsolutePath.EndsWith("/selfsubjectreviews", StringComparison.Ordinal) ||
                request.RequestUri.AbsolutePath.EndsWith("/selfsubjectaccessreviews", StringComparison.Ordinal);
            var write = request.Method != HttpMethod.Get && !review;
            await using (var db = new NpgsqlConnection(counter.Connection))
            {
                await db.OpenAsync(token);
                await using var query = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state = 'idle in transaction'", db);
                if ((long)(await query.ExecuteScalarAsync(token))! != 0) throw new InvalidOperationException("Catalog transaction crossed API work.");
                counter.TransactionChecks++;
                if (write)
                {
                    await using var marker = new NpgsqlCommand("SELECT count(*) FROM durable_create_operations o JOIN durable_create_operation_attempts a ON a.attempt_id = o.current_attempt_id JOIN durable_action_intents i ON i.deployment_id = o.deployment_id AND i.generation = o.generation AND i.operation_id = o.operation_id WHERE o.deployment_id = @deployment AND o.generation = @generation AND o.operation_id = @operation AND o.state = 'Dispatching' AND a.dispatched_at IS NOT NULL AND a.outcome IS NULL", db);
                    marker.Parameters.AddWithValue("deployment", Guid.Parse(counter.Key.DeploymentId.ToString()));
                    marker.Parameters.AddWithValue("generation", counter.Key.Generation.Value); marker.Parameters.AddWithValue("operation", counter.Key.OperationId.Value);
                    if ((long)(await marker.ExecuteScalarAsync(token))! != 1) throw new InvalidOperationException("API write preceded durable barrier.");
                    counter.Barriers++;
                }
            }
            if (write) counter.Writes++; else counter.Reads++;
            if (counter.Unavailable) throw new HttpRequestException("Injected unavailability.");
            var response = await base.SendAsync(request, token);
            if (write && request.Method == HttpMethod.Post && response.StatusCode == HttpStatusCode.Created)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                counter.AcceptedUid = json.RootElement.GetProperty("metadata"u8).GetProperty("uid"u8).GetString();
                if (counter.DropResponse) { response.Dispose(); throw new HttpRequestException("Injected response loss."); }
            }
            return response;
        }
    }
    private static void Check(bool condition, string id)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + "durable-" + id);
        if (!condition) throw new InvalidOperationException("Durable action assertion failed.");
    }
}
