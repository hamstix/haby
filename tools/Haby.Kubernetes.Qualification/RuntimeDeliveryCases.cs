using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
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
        ServiceProvider services, InternalRuntimePublication publication, RuntimePublicationRequest request, bool login, CancellationToken token)
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
                                [.. handlers, new ReviewBoundary(async () => { reviews++; if (afterReview is { } hook) { afterReview = null; await hook(); } })], ct)));
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
                            expected.Port == confirmed.Port && expected.Database == confirmed.DatabaseName && expected.Timeout == 200,
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
                    await host.StopAsync(token);
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
    private sealed class ReviewBoundary(Func<Task> after) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var response = await base.SendAsync(request, token); await after(); return response; }
    }
    private sealed class ReadBoundary(IRuntimeConfigurationStore inner, Func<Task> after) : IRuntimeConfigurationStore
    {
        public ValueTask<RuntimeConfigurationAuthority?> AuthorizeAsync(RuntimeAuthenticatedContext identity, ConfigurationDocumentRevisionId revision, CancellationToken token) => inner.AuthorizeAsync(identity, revision, token);
        public async ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(RuntimeConfigurationAuthority authority, CancellationToken token)
        { await after(); return await inner.ReadAsync(authority, token); }
    }
}
