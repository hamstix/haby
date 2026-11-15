using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Configuration.Client;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Haby.PostgreSql.IntegrationTests;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

internal static class RuntimeDeliveryCases
{
    internal static async Task Run(Kubernetes admin, DurableDeploymentPlanningTests.InternalScenario scenario,
        ServiceProvider services, InternalRuntimePublication publication, RuntimePublicationRequest request, bool login, CancellationToken token,
        IReadOnlyList<RuntimePublicationRequest>? mandatory = null)
    {
        var label = login ? "login" : "initial";
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        RuntimeIdentityBindingRow binding;
        string inventory;
        string fingerprint;
        PostgreSqlApplicationConnection? expectedConnection = null;
        await using (var read = await contexts.CreateDbContextAsync(token))
        {
            binding = await read.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.Id == request.Binding, token);
            inventory = (await read.OwnedObjects.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == binding.ManagedWorkloadId && x.Role == binding.Role, token)).Payload;
            fingerprint = await read.ConfigurationDocuments.AsNoTracking().Where(x => x.Id == request.Revision).Select(x => x.Fingerprint).SingleAsync(token);
            if (login)
            {
                var output = await read.OperationOutputs.AsNoTracking().SingleAsync(x => x.Contract == PostgreSqlApplicationConnectionContract.Contract && x.Key == PostgreSqlApplicationConnectionContract.Key, token);
                expectedConnection = PostgreSqlApplicationConnectionContract.Decode(new(output.Contract, output.Key, output.Payload))
                    ?? throw new InvalidOperationException("Confirmed login output unavailable.");
            }
        }
        var receipt = RuntimeIdentityCodec.Receipt(inventory);
        var ns = receipt.Intent.Namespace;
        var podName = "delivery-" + Guid.NewGuid().ToString("N")[..12];
        V1Pod? pod = null;
        Func<Task>? afterReview = null, afterAuthority = null;
        var reviews = 0;
        byte[]? expectedReviewedCredential = null;
        var startupReviews = 0;
        var startupCredentialsMatch = true;
        void ObserveReview(byte[] payload)
        {
            if (expectedReviewedCredential is null) return;
            using var json = JsonDocument.Parse(payload);
            startupCredentialsMatch &= json.RootElement.GetProperty("spec"u8).GetProperty("token"u8).ValueEquals(expectedReviewedCredential);
            startupReviews++;
        }
        async Task Revoke()
        {
            var current = (await publication.ReadAsync(request.Key, token))!;
            await publication.ApplyAsync(request with
            {
                Key = "delivery-revoke-" + Guid.NewGuid().ToString("N"),
                Operation = RuntimePublicationOperation.RevokePermission,
                Origin = null,
                ExpectedToken = current.CurrentPermission!.Token
            }, token);
        }
        async Task Republish()
        {
            var current = (await publication.ReadAsync(request.Key, token))!;
            await publication.ApplyAsync(request with { Key = "delivery-republish-" + Guid.NewGuid().ToString("N"), ExpectedToken = current.CurrentPermission!.Token }, token);
        }
        void Check(bool value, string id)
        { if (!value) throw new InvalidOperationException("Runtime delivery qualification failed: " + id); Console.WriteLine("PASS delivery-" + label + "-" + id); }
        Exception? primary = null;
        try
        {
            pod = await admin.CoreV1.CreateNamespacedPodAsync(new V1Pod
            {
                Metadata = new() { Name = podName },
                Spec = new()
                { ServiceAccountName = receipt.Intent.Name, AutomountServiceAccountToken = false, Containers = [new() { Name = "app", Image = "registry.k8s.io/pause:3.10" }] }
            }, ns, cancellationToken: token);
            var issued = await admin.CoreV1.CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest
            {
                Spec = new()
                { Audiences = [binding.Audience], ExpirationSeconds = 600, BoundObjectRef = new() { ApiVersion = "v1", Kind = "Pod", Name = podName, Uid = pod.Metadata.Uid } }
            },
                receipt.Intent.Name, ns, cancellationToken: token);
            var bytes = Encoding.ASCII.GetBytes(issued.Status.Token);
            try
            {
                async Task DeliveryHost(bool first)
                {
                    await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Port=0", "--Haby:Tls:Enabled=false"], collection =>
                    {
                        // Only the existing private-CA transport seam differs; real TokenReview and runtime policy execute.
                        collection.RemoveAll<IRuntimeAuthenticator>();
                        collection.AddSingleton<IRuntimeAuthenticator>(p => new KubernetesRuntimeAuthenticator(p.GetRequiredService<ISecretStore>(),
                            (connection, handlers, ct) => KubernetesClientFactory.CreateFixture(connection, p.GetRequiredService<ISecretStore>(),
                                [.. handlers, new ReviewBoundary(async () => { reviews++; if (afterReview is { } hook) { afterReview = null; await hook(); } }, ObserveReview)], ct)));
                        collection.AddSingleton<IRuntimeConfigurationStore>(p => new ReadBoundary(new PostgreSqlRuntimeConfigurationStore(
                            p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>()), async () => { if (afterAuthority is { } hook) { afterAuthority = null; await hook(); } }));
                    });
                    await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                    using var channel = GrpcChannel.ForAddress(host.Urls.Single(), new GrpcChannelOptions { MaxReceiveMessageSize = 8 * 1024 * 1024 });
                    var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
                    var headers = new Metadata { { "authorization", "Bearer " + Encoding.ASCII.GetString(bytes) } };
                    var call = new DeliverRuntimeConfigurationRequest { BindingId = request.Binding.ToString(), RevisionId = request.Revision!.Value.ToString() };
                    var before = reviews;
                    var beforeState = await State(contexts, token);
                    var reply = await client.DeliverRuntimeAsync(call, headers, deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: token);
                    var afterState = await State(contexts, token);
                    Check(reply.RevisionId == call.RevisionId && reply.SourceFingerprint == fingerprint && reviews == before + 1 && beforeState.SequenceEqual(afterState),
                        first ? "T01-generated-Host-real-Pod-TokenReview-exact-revision" : "T06-restarted-Host-fresh-TokenReview-exact-authority");
                    using var json = JsonDocument.Parse(reply.Json.Memory);
                    if (login)
                    {
                        var connectionString = json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString()!;
                        var expected = new NpgsqlConnectionStringBuilder(connectionString);
                        var confirmed = expectedConnection ?? throw new InvalidOperationException("Confirmed login output unavailable.");
                        Check(!string.IsNullOrEmpty(expected.Password) && expected.Username == confirmed.UserName && expected.Host == confirmed.Host &&
                            // PrepareMixedAsync uses UpdateEnvelope's independently declared default timeout of 210.
                            expected.Port == confirmed.Port && expected.Database == confirmed.DatabaseName && expected.Timeout == 210,
                            first ? "T02-exact-application-connection-fields" : "T08-restarted-exact-application-connection-fields");
                        await using var application = new NpgsqlConnection(connectionString);
                        await application.OpenAsync(token);
                        await using var transaction = await application.BeginTransactionAsync(token);
                        await new NpgsqlCommand("CREATE TEMP TABLE haby_runtime_delivery_probe (value integer); INSERT INTO haby_runtime_delivery_probe VALUES (219)", application, transaction).ExecuteNonQueryAsync(token);
                        Check((int)(await new NpgsqlCommand("SELECT value FROM haby_runtime_delivery_probe", application, transaction).ExecuteScalarAsync(token))! == 219,
                            first ? "T03-real-application-credential-DDL-DML" : "T09-restarted-real-application-credential-DDL-DML");
                        await transaction.RollbackAsync(token);
                    }
                    else Check(Encoding.UTF8.GetString(reply.Json.Span) == "{\"Mode\":\"frozen\"}",
                        first ? "T02-exact-complete-static-JSON" : "T08-restarted-exact-complete-static-JSON");
                    if (first)
                    {
                        afterReview = Revoke;
                        var refusal = await Refused(client, call, headers, token);
                        Check(refusal == StatusCode.PermissionDenied, "T04-permission-revoked-during-real-TokenReview-denies");
                        await Republish();
                        afterAuthority = Revoke;
                        var inflight = await client.DeliverRuntimeAsync(call, headers, cancellationToken: token);
                        Check(inflight.RevisionId == call.RevisionId && await Refused(client, call, headers, token) == StatusCode.PermissionDenied,
                            "T05-revoke-after-authority-inflight-only-next-request-denied");
                        await Republish();
                    }
                    if (first && mandatory is not null) await Startup(client, mandatory);
                    await host.StopAsync(token);
                }

                async Task Startup(ConfigurationDelivery.ConfigurationDeliveryClient client, IReadOnlyList<RuntimePublicationRequest> selection)
                {
                    Check(selection.Count == 2 && selection.All(x => x.Binding == request.Binding), "S01-two-explicit-published-documents-same-binding");
                    var secondPodName = "startup-" + Guid.NewGuid().ToString("N")[..12];
                    V1Pod? secondPod = null;
                    var privatePath = Path.Combine(Path.GetTempPath(), "haby221-" + Guid.NewGuid().ToString("N"));
                    if (OperatingSystem.IsWindows()) Directory.CreateDirectory(privatePath);
                    else Directory.CreateDirectory(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    byte[]? replacementBytes = null;
                    Exception? startupFailure = null;
                    try
                    {
                        secondPod = await admin.CoreV1.CreateNamespacedPodAsync(new V1Pod
                        {
                            Metadata = new() { Name = secondPodName },
                            Spec = new()
                            {
                                ServiceAccountName = receipt.Intent.Name,
                                AutomountServiceAccountToken = false,
                                Containers = [new() { Name = "app", Image = "registry.k8s.io/pause:3.10" }]
                            }
                        }, ns, cancellationToken: token);
                        var replacementToken = await admin.CoreV1.CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest
                        {
                            Spec = new()
                            {
                                Audiences = [binding.Audience],
                                ExpirationSeconds = 600,
                                BoundObjectRef = new() { ApiVersion = "v1", Kind = "Pod", Name = secondPodName, Uid = secondPod.Metadata.Uid }
                            }
                        }, receipt.Intent.Name, ns, cancellationToken: token);
                        replacementBytes = Encoding.ASCII.GetBytes(replacementToken.Status.Token);
                        Check(!bytes.SequenceEqual(replacementBytes), "S02-two-distinct-real-Pod-bound-issued-tokens");
                        var credentialPath = Path.Combine(privatePath, "projected");
                        await File.WriteAllBytesAsync(credentialPath, bytes, token);
                        var revisions = selection.Select(x => x.Revision!.Value.ToString()).ToArray();
                        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
                        await using (var read = await contexts.CreateDbContextAsync(token))
                            foreach (var selected in selection)
                                fingerprints.Add(selected.Revision!.Value.ToString(), await read.ConfigurationDocuments.AsNoTracking()
                                    .Where(x => x.Id == selected.Revision).Select(x => x.Fingerprint).SingleAsync(token));
                        expectedReviewedCredential = bytes;
                        var beforeReviews = reviews;
                        var firstReviewProven = false;
                        afterAuthority = async () =>
                        {
                            // Runs only after the first real TokenReview and exact publication authorization.
                            firstReviewProven = reviews == beforeReviews + 1 && startupReviews == 1 && startupCredentialsMatch;
                            var replacementPath = Path.Combine(privatePath, "replacement");
                            await File.WriteAllBytesAsync(replacementPath, replacementBytes, token);
                            File.Move(replacementPath, credentialPath, true);
                            expectedReviewedCredential = replacementBytes;
                            throw new ConfigurationDeliveryStorageException();
                        };
                        var startup = new RuntimeStartupClient(client);
                        var beforeState = await State(contexts, token);
                        using (var complete = await startup.FetchAsync(request.Binding.ToString(), revisions, credentialPath, cancellationToken: token))
                        {
                            Check(firstReviewProven, "S03-first-real-review-before-controlled-temporary-boundary");
                            Check(complete.Documents.Select(x => x.RevisionId).SequenceEqual(revisions) && complete.Documents.All(x =>
                                x.SourceFingerprint == fingerprints[x.RevisionId]) && Encoding.UTF8.GetString(complete.Documents[0].Json.Span) == "{\"Mode\":\"frozen\"}" &&
                                Encoding.UTF8.GetString(complete.Documents[1].Json.Span) == "{}",
                                "S04-complete-two-exact-documents-and-original-content");
                            Check(reviews == beforeReviews + 3 && startupReviews == 3 && startupCredentialsMatch,
                                "S05-retry-rereads-atomic-file-replacement-and-real-second-Pod-token");
                        }
                        var completedState = await State(contexts, token);
                        Check(beforeState.SequenceEqual(completedState), "S06-helper-created-no-authority-or-state");
                        var beforeRestart = reviews;
                        using (var restarted = await new RuntimeStartupClient(client).FetchAsync(request.Binding.ToString(), revisions, credentialPath, cancellationToken: token))
                            Check(restarted.Documents.Count == 2 && reviews == beforeRestart + 2 && startupCredentialsMatch,
                                "S07-restarted-consumer-fresh-file-reads-and-real-delivery");
                        var second = selection[1];
                        var current = (await publication.ReadAsync(second.Key, token))!;
                        await publication.ApplyAsync(second with
                        {
                            Key = "startup-revoke-" + Guid.NewGuid().ToString("N"),
                            Operation = RuntimePublicationOperation.RevokePermission,
                            Origin = null,
                            ExpectedToken = current.CurrentPermission!.Token
                        }, token);
                        var primaryEntered = false;
                        try
                        {
                            using var partial = await startup.FetchAsync(request.Binding.ToString(), revisions, credentialPath, cancellationToken: token);
                            primaryEntered = true;
                        }
                        catch (RuntimeStartupException failure) when (failure.Failure == RuntimeStartupFailure.AccessDenied) { }
                        Check(!primaryEntered, "S08-later-independent-revocation-prevents-primary-start");
                        current = (await publication.ReadAsync(second.Key, token))!;
                        await publication.ApplyAsync(second with { Key = "startup-republish-" + Guid.NewGuid().ToString("N"), ExpectedToken = current.CurrentPermission!.Token }, token);
                    }
                    catch (Exception error) { startupFailure = error; throw; }
                    finally
                    {
                        afterAuthority = null; expectedReviewedCredential = null;
                        if (replacementBytes is not null) CryptographicOperations.ZeroMemory(replacementBytes);
                        Exception? cleanupFailure = null;
                        try
                        {
                            if (secondPod is not null) await admin.CoreV1.DeleteNamespacedPodAsync(secondPodName, ns,
                                new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = secondPod.Metadata.Uid }, GracePeriodSeconds = 0 }, cancellationToken: CancellationToken.None);
                        }
                        catch (Exception error) { cleanupFailure = error; }
                        try { Directory.Delete(privatePath, true); }
                        catch (Exception error) { cleanupFailure ??= error; }
                        if (cleanupFailure is not null)
                        {
                            if (startupFailure is null) throw new InvalidOperationException("Runtime startup owned cleanup failed.");
                            Console.Error.WriteLine("Runtime startup owned cleanup failed; primary failure retained.");
                        }
                    }
                }
                await DeliveryHost(true);
                await DeliveryHost(false);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            await using var verify = await contexts.CreateDbContextAsync(token);
            var applied = (await verify.ApplicationDeployments.AsNoTracking().SingleAsync(token)).AppliedGeneration;
            Check(applied == (login ? (long?)1 : null) && !await verify.ConfigurationAccess.AnyAsync(token), "T07-no-Applied-or-static-grant-mutation");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            if (pod is not null)
            {
                try { await admin.CoreV1.DeleteNamespacedPodAsync(podName, ns, new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = pod.Metadata.Uid }, GracePeriodSeconds = 0 }, cancellationToken: CancellationToken.None); }
                catch when (primary is not null) { Console.Error.WriteLine("Runtime delivery owned Pod cleanup failed; primary failure retained."); }
            }
        }
    }
    private static async Task<long[]> State(IDbContextFactory<ManifestCatalogDbContext> contexts, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return [await db.DurableOperations.LongCountAsync(token), await db.DurableOperationAttempts.LongCountAsync(token),
            await db.OperationOutputs.LongCountAsync(token), await db.GeneratedCredentialSlots.LongCountAsync(token),
            await db.ConfigurationDocuments.LongCountAsync(token), await db.ConfigurationAccess.LongCountAsync(token),
            await db.RuntimeIdentityBindings.LongCountAsync(token), await db.RuntimeConfigurationPermissions.LongCountAsync(token),
            await db.RuntimePublicationReceipts.LongCountAsync(token),
            (await db.ApplicationDeployments.AsNoTracking().SingleAsync(token)).AppliedGeneration ?? 0];
    }
    private static async Task<StatusCode> Refused(ConfigurationDelivery.ConfigurationDeliveryClient client, DeliverRuntimeConfigurationRequest request, Metadata headers, CancellationToken token)
    {
        try { await client.DeliverRuntimeAsync(request, headers, cancellationToken: token); throw new InvalidOperationException("Expected runtime refusal."); }
        catch (RpcException error) { return error.StatusCode; }
    }
    private sealed class ReviewBoundary(Func<Task> after, Action<byte[]> observe) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(token);
            try
            {
                if (content is not null) observe(content);
                var response = await base.SendAsync(request, token); await after(); return response;
            }
            finally { if (content is not null) CryptographicOperations.ZeroMemory(content); }
        }
    }
    private sealed class ReadBoundary(IRuntimeConfigurationStore inner, Func<Task> after) : IRuntimeConfigurationStore
    {
        public ValueTask<RuntimeConfigurationAuthority?> AuthorizeAsync(RuntimeAuthenticatedContext identity, ConfigurationDocumentRevisionId revision, CancellationToken token) => inner.AuthorizeAsync(identity, revision, token);
        public async ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(RuntimeConfigurationAuthority authority, CancellationToken token)
        { await after(); return await inner.ReadAsync(authority, token); }
    }
}
