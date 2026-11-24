using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Haby.Operator.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    private static async Task ReferenceWorkloadPreparation(ManifestOperator.ManifestOperatorClient client, string connection,
        DurableCommitSummary baseline, PublicReferenceFailureDiagnostics diagnostic)
    {
        // Real stock Kubernetes transport against an explicitly synthetic HTTP backend.
        // No internal constructor, registry replacement or additional Kubernetes hook is used.
        using var issuedCertificate = Certificate();
        var pfx = issuedCertificate.Export(X509ContentType.Pkcs12);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        await diagnostic.RunAsync(async cleanup =>
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            var backend = builder.Build();
            cleanup(() => backend.DisposeAsync());
            cleanup(async () => await backend.StopAsync());
            var accounts = new ConcurrentDictionary<string, string>();
            var externalWrites = 0;
            var calls = new ConcurrentQueue<string>();
            backend.Run(async context =>
            {
                context.Response.ContentType = "application/json";
                var path = context.Request.Path.Value!;
                calls.Enqueue(context.Request.Method + " " + path);
                string? reply = null;
                if (path.EndsWith("/selfsubjectreviews", StringComparison.Ordinal))
                    reply = """{"apiVersion":"authentication.k8s.io/v1","kind":"SelfSubjectReview","status":{"userInfo":{"username":"sample-operator","groups":["system:authenticated"]}}}""";
                else if (path.EndsWith("/selfsubjectaccessreviews", StringComparison.Ordinal))
                    reply = """{"apiVersion":"authorization.k8s.io/v1","kind":"SelfSubjectAccessReview","status":{"allowed":true}}""";
                else if (path == "/api/v1/namespaces/test")
                    reply = """{"apiVersion":"v1","kind":"Namespace","metadata":{"name":"test","uid":"namespace-test"},"status":{"phase":"Active"}}""";
                else if (path.StartsWith("/api/v1/namespaces/test/serviceaccounts", StringComparison.Ordinal))
                {
                    if (context.Request.Method == "POST")
                    {
                        var account = (await JsonNode.ParseAsync(context.Request.Body))!;
                        account["metadata"]!["uid"] = Guid.NewGuid().ToString();
                        account["metadata"]!["resourceVersion"] = "1";
                        reply = account.ToJsonString();
                        accounts["/api/v1/namespaces/test/serviceaccounts/" + account["metadata"]!["name"]!.GetValue<string>()] = reply;
                        Interlocked.Increment(ref externalWrites);
                    }
                    else if (!accounts.TryGetValue(path, out reply))
                    { context.Response.StatusCode = 404; reply = """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}"""; }
                }
                // Refuse the Deployment phase: this case qualifies preparation, never rollout.
                if (reply is null) { context.Response.StatusCode = 403; reply = """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"Forbidden","code":403}"""; }
                await context.Response.WriteAsync(reply);
            });
            await backend.StartAsync();
            Assert.Equal(SecretStatus.Created, (await client.CreateSecretAsync(new()
            {
                Environment = "pilot",
                Name = "runtime-admin",
                RequestId = Guid.NewGuid().ToString(),
                Material = ByteString.CopyFromUtf8("sample-kubernetes-test-token")
            })).Status);
            diagnostic.At(PublicReferenceCheckpoint.ProviderPreflight);
            var declaration = JsonNode.Parse("""
                {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"runtime","environment":"pilot"},"spec":{
                "revision":1,"providerKind":"kubernetes/v1","configurationContract":"haby.kubernetes.connection/v1","implementationId":"haby.kubernetes/v1",
                "properties":{"endpoint":"https://127.0.0.1/","tls":{"mode":"insecure"}},"secretRefs":{"bearer-token":{"secretName":"runtime-admin","version":1}}}}
                """u8)!;
            declaration["spec"]!["properties"]!["endpoint"] = backend.Urls.Single();
            Assert.Equal(ProviderStatus.Created, (await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFromUtf8(declaration.ToJsonString()) })).Status);
            var aliasResult = await client.SetProviderAliasAsync(new()
            { Environment = "pilot", Alias = "kubernetes.primary", ExpectedAbsent = true, Target = new() { Name = "runtime", Revision = 1 } });
            Assert.True(aliasResult.Status == ProviderAccessStatus.Changed,
                "Synthetic Kubernetes alias: " + aliasResult.Status + "/" + aliasResult.Preflight + "/" + aliasResult.ConnectionDiagnostic + "; calls=" + string.Join(';', calls));
            var placement = """
                {"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard","revision":1},"spec":{
                "namespace":"test","runtimeConfiguration":{"endpoint":"https://configuration.example.invalid/","tls":{"mode":"system"}}}}
                """;
            Assert.Equal(PlacementStatus.Created, (await client.ImportPlacementAsync(new() { Environment = "pilot", ProviderAlias = "kubernetes.primary", Payload = ByteString.CopyFromUtf8(placement) })).Status);
            Assert.Equal(PlacementSelectionStatus.Created, (await client.ApplyPlacementSelectionAsync(new()
            {
                Environment = "pilot",
                ProviderAlias = "kubernetes.primary",
                PlacementName = "standard",
                Revision = 1,
                Target = new() { Kind = "environment" },
                ExpectedAbsence = true
            })).Status);
            var envelope = ReferenceEnvelope("reference-consumer");
            envelope["applicationVersion"] = "2";
            foreach (var component in envelope["manifest"]!["spec"]!["components"]!.AsObject())
                component.Value!["workloads"] = JsonNode.Parse("""
                    {"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/app:v1"},"configuration":{"documents":["settings"]}}}}
                    """u8);
            var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(envelope.ToJsonString()) });
            Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase);
            var request = ReferenceChange(imported.Created.Revision.Reference);
            request.Precondition = PlanningPrecondition.ExpectedGeneration;
            request.ExpectedGeneration = baseline.Generation;
            var before = await ReferenceSnapshot(connection);
            var changed = request.Clone(); changed.Acquisitions.Resources[0].Input.Json = "{\"Identity\":\"another-service\"}";
            Assert.Equal(PlanningStatus.ReferenceNotSupported, (await client.CreateChangeSetAsync(changed)).Status);
            Assert.Equal(before, await ReferenceSnapshot(connection));
            var initial = request.Clone(); initial.Precondition = PlanningPrecondition.ExpectedAbsence; initial.ClearExpectedGeneration();
            Assert.Equal(PlanningStatus.ReferenceNotSupported, (await client.CreateChangeSetAsync(initial)).Status);
            var fresh = envelope.DeepClone();
            fresh["manifest"]!["metadata"]!["id"] = "initial-reference-workload";
            var freshRevision = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(fresh.ToJsonString()) });
            Assert.Equal(ImportReply.OutcomeOneofCase.Created, freshRevision.OutcomeCase);
            Assert.Equal(PlanningStatus.ReferenceNotSupported, (await client.CreateChangeSetAsync(ReferenceChange(freshRevision.Created.Revision.Reference))).Status);
            foreach (var fault in new[] { "removal", "provider" })
            {
                var refusedEnvelope = envelope.DeepClone();
                refusedEnvelope["applicationVersion"] = fault == "removal" ? "3" : "4";
                if (fault == "removal")
                {
                    refusedEnvelope["manifest"]!["spec"]!["resources"]!.AsObject().Remove("right");
                    refusedEnvelope["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]!["service"]!["target"]!["resourceRef"] = "left";
                }
                else refusedEnvelope["manifest"]!["spec"]!["resources"]!["left"]!["providerRef"] = "kubernetes.primary";
                var refusedRevision = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFromUtf8(refusedEnvelope.ToJsonString()) });
                Assert.Equal(ImportReply.OutcomeOneofCase.Created, refusedRevision.OutcomeCase);
                var refusal = ReferenceChange(refusedRevision.Created.Revision.Reference);
                if (fault == "removal") refusal.Acquisitions.Resources.RemoveAt(1);
                refusal.Precondition = PlanningPrecondition.ExpectedGeneration;
                refusal.ExpectedGeneration = baseline.Generation;
                Assert.Equal(PlanningStatus.ReferenceNotSupported, (await client.CreateChangeSetAsync(refusal)).Status);
            }
            Assert.Equal(before, await ReferenceSnapshot(connection));
            diagnostic.At(PublicReferenceCheckpoint.PlanCommit);
            var change = await client.CreateChangeSetAsync(request);
            Assert.Equal(PlanningStatus.Created, change.Status);
            var planned = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
            Assert.Equal(PlanningStatus.Planned, planned.Status);
            Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = planned.Plan.PlanId })).Status);
            var committed = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId, PlanId = planned.Plan.PlanId });
            Assert.Equal(CommitStatus.Committed, committed.Status);
            long attempts, outputs;
            await using (var read = ReferenceReadContext(connection))
            { attempts = await read.DurableOperationAttempts.CountAsync(x => x.OperationId == "left-reference" || x.OperationId == "right-reference"); outputs = await read.OperationOutputs.CountAsync(x => x.Contract == "sample.access/v1"); }
            var exact = new GenerationRequest { DeploymentId = committed.Commit.DeploymentId, Generation = committed.Commit.Generation };
            var submissions = 0;
            var replies = await ReferencePreparationContinuation.RunAsync(exact, committed.Commit, imported.Created.Revision.Reference, baseline.Generation,
                input =>
                {
                    diagnostic.At(submissions++ == 0 ? PublicReferenceCheckpoint.Submit : PublicReferenceCheckpoint.PreparationContinuation);
                    return client.SubmitGenerationAsync(input).ResponseAsync;
                }, async input =>
                {
                    diagnostic.At(PublicReferenceCheckpoint.PreparationInspection);
                    diagnostic.Predicate(ReferencePreparationPredicate.InspectionWriteBound, actualCount: externalWrites, expectedCount: 2, flag: externalWrites is >= 0 and <= 2);
                    Assert.InRange(externalWrites, 0, 2);
                    diagnostic.Predicate(ReferencePreparationPredicate.InspectionRpc);
                    var inspected = await client.GetGenerationAsync(input);
                    diagnostic.Predicate(ReferencePreparationPredicate.InspectionReplyReceived, flag: true);
                    return inspected;
                }, (reply, attempt) =>
                {
                    diagnostic.At(attempt == 1 ? PublicReferenceCheckpoint.PreparationFirstResult : PublicReferenceCheckpoint.PreparationContinuationResult);
                    diagnostic.Observe(reply, externalWrites);
                }, diagnostic, input => ReferencePreparationCatalog.ReadAsync(connection, input, committed.Commit, imported.Created.Revision.Reference, diagnostic));
            // First is an independent snapshot; its safe observation is never overwritten by a continuation.
            var firstSubmission = replies.First;
            var prepared = replies.Final;
            Assert.NotSame(firstSubmission, prepared);
            diagnostic.At(PublicReferenceCheckpoint.PreparationResult);
            Assert.True(prepared.Stage == "Deployment", "Preparation: " + prepared.Outcome + "/" + prepared.Stage + "/" + prepared.Reason + "; writes=" + externalWrites + "; calls=" + string.Join(';', calls));
            // Preparation must be complete. The deliberately unsupported Deployment backend may
            // refuse immediately or close through the stock bounded request budget; neither proves rollout.
            Assert.Contains(prepared.Outcome, new[] { "stopped", "cancelled" });
            Assert.Equal(2, externalWrites); // Only synthetic ServiceAccount preparation, no Deployment write.
            Assert.All(prepared.Progress.Members, x => Assert.Equal("prepared", x.Documents));
            diagnostic.At(PublicReferenceCheckpoint.PreparationPostconditions);
            await using (var verify = ReferenceReadContext(connection))
            {
                Assert.Equal(attempts, await verify.DurableOperationAttempts.CountAsync(x => x.OperationId == "left-reference" || x.OperationId == "right-reference"));
                Assert.Equal(outputs, await verify.OperationOutputs.CountAsync(x => x.Contract == "sample.access/v1"));
                Assert.Equal(4, await verify.ResourceManagement.CountAsync());
                Assert.Equal(2, await verify.SecretPayloads.CountAsync());
                Assert.Empty(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
                Assert.Equal(6, await verify.ConfigurationSharedUses.CountAsync());
                Assert.Equal(2, await verify.WorkloadConfigurationRecipients.CountAsync());
                Assert.Equal(2, await verify.RuntimeIdentityBindings.CountAsync(x => x.Status == "Prepared"));
                Assert.Equal(2, await verify.RuntimeConfigurationPermissions.CountAsync(x => x.Status == "Published"));
            }
        });
    }
}
