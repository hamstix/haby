using System.Text.Json.Nodes;
using Haby.Application;
using k8s;
using k8s.Models;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class DeploymentRecipientTests
{
    private static (DeploymentIntent Intent, DeploymentReceipt Receipt, V1ReplicaSetList Sets, V1PodList Pods) Fixture(string strategy = "RollingUpdate")
    {
        var original = ConfiguredDeploymentTests.Intent();
        var prior = original with
        {
            Target = original.Target with
            {
                Strategy = strategy,
                MaxSurge = strategy == "Recreate" ? null : "25%",
                MaxUnavailable = strategy == "Recreate" ? null : "25%"
            }
        };
        var intent = prior with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = prior.Target,
            TargetGeneration = 2,
            Target = prior.Target with { Image = "registry.example/app:v2" }
        };
        var obj = DeploymentReadinessTests.ReadyObject(intent);
        var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        var template = KubernetesJson.Deserialize<V1PodTemplateSpec>(obj["spec"]!["template"]!.ToJsonString());
        var set = new V1ReplicaSet
        {
            Metadata = new()
            {
                Name = "new",
                NamespaceProperty = intent.Namespace,
                Uid = "new-rs",
                OwnerReferences = [new() { ApiVersion = "apps/v1", Kind = "Deployment", Name = intent.Name, Uid = receipt.Uid, Controller = true }]
            },
            Spec = new() { Template = template }
        };
        var pod = new V1Pod
        {
            Metadata = new()
            {
                Name = "new-pod",
                NamespaceProperty = intent.Namespace,
                Uid = "new-pod",
                OwnerReferences = [new() { ApiVersion = "apps/v1", Kind = "ReplicaSet", Name = "new", Uid = "new-rs", Controller = true }]
            },
            Spec = template.Spec
        };
        return (intent, receipt, new() { Metadata = new() { ResourceVersion = "20" }, Items = [set] },
            new() { Metadata = new() { ResourceVersion = "21" }, Items = [pod] });
    }

    [Theory]
    [InlineData("Recreate")]
    [InlineData("RollingUpdate")]
    public void Exact_target_chain_proves_sampled_absence_and_evidence_cannot_cross_receipts(string strategy)
    {
        var f = Fixture(strategy);
        var result = DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, new());
        Assert.True(result.Absent, result.Code);
        Assert.True(DeploymentRecipients.Validate(DeploymentIntents.Encode(f.Intent), DeploymentIntents.Encode(f.Receipt), result));
        Assert.False(DeploymentRecipients.Validate(DeploymentIntents.Encode(f.Intent), DeploymentIntents.Encode(f.Receipt with { Uid = "replacement" }), result));
        Assert.DoesNotContain(f.Intent.Target.Runtime!.Bootstrap, result.Evidence!.Json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Old_Pod_blocks_retirement_even_when_terminating_and_all_target_replicas_are_ready(bool terminating)
    {
        var f = Fixture();
        var old = KubernetesJson.Deserialize<V1ReplicaSet>(KubernetesJson.Serialize(f.Sets.Items[0]));
        old.Metadata.Name = "old"; old.Metadata.Uid = "old-rs";
        old.Spec.Template.Spec.Containers.Single(x => x.Name == f.Intent.Target.Container).Image = "registry.example/app:v1";
        f.Sets.Items.Add(old);
        var pod = KubernetesJson.Deserialize<V1Pod>(KubernetesJson.Serialize(f.Pods.Items[0]));
        pod.Metadata.Name = "old-pod"; pod.Metadata.Uid = "old-pod";
        pod.Metadata.OwnerReferences[0].Name = "old"; pod.Metadata.OwnerReferences[0].Uid = "old-rs";
        pod.Metadata.DeletionTimestamp = terminating ? DateTime.UtcNow : null;
        pod.Spec = old.Spec.Template.Spec;
        f.Pods.Items.Add(pod);
        var result = DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, new());
        Assert.False(result.Absent); Assert.Equal("old-recipients-present", result.Code);
        f.Pods.Items.Remove(pod);
        Assert.True(DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, new()).Absent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fixture_wait_requires_every_previous_UID_chain_absent_after_held_Pod_disappears(bool terminating)
    {
        var f = Fixture();
        foreach (var name in new[] { "held", "other" })
        {
            var set = KubernetesJson.Deserialize<V1ReplicaSet>(KubernetesJson.Serialize(f.Sets.Items[0]));
            set.Metadata.Name = name; set.Metadata.Uid = name + "-rs";
            set.Spec.Template.Spec.Containers.Single(x => x.Name == f.Intent.Target.Container).Image = "registry.example/app:v1";
            f.Sets.Items.Add(set);
            var pod = KubernetesJson.Deserialize<V1Pod>(KubernetesJson.Serialize(f.Pods.Items[0]));
            pod.Metadata.Name = name; pod.Metadata.Uid = name + "-pod";
            pod.Metadata.OwnerReferences[0].Name = name; pod.Metadata.OwnerReferences[0].Uid = set.Metadata.Uid;
            pod.Metadata.DeletionTimestamp = terminating ? DateTime.UtcNow : null;
            pod.Spec = set.Spec.Template.Spec;
            f.Pods.Items.Add(pod);
        }
        var reads = 0;
        await ConfiguredRecipientWait.UntilAbsentAsync(_ =>
        {
            if (++reads == 2) f.Pods.Items.RemoveAt(1); // Held gone, other UID chain remains.
            if (reads == 3) f.Pods.Items.RemoveAt(1);
            var sample = DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, new());
            Assert.Equal(reads == 3, sample.Absent);
            return Task.FromResult(sample);
        }, sample => DeploymentRecipients.Validate(DeploymentIntents.Encode(f.Intent), DeploymentIntents.Encode(f.Receipt), sample),
            3, 1, () => Assert.Fail("Unexpected expiry."), default);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task Fixture_recipient_wait_never_renews_cancellation_or_accepts_unvalidated_absence()
    {
        using var budget = new CancellationTokenSource();
        var reads = 0; var expiries = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfiguredRecipientWait.UntilAbsentAsync(ct =>
        {
            Assert.Equal(budget.Token, ct);
            reads++; budget.Cancel();
            return Task.FromResult(new RecipientObservation(true, "old-recipients-absent"));
        }, _ => true, 128, 1, () => expiries++, budget.Token));
        Assert.Equal(1, reads); Assert.Equal(1, expiries);
        await Assert.ThrowsAsync<TimeoutException>(() => ConfiguredRecipientWait.UntilAbsentAsync(_ =>
            Task.FromResult(new RecipientObservation(true, "old-recipients-absent")), _ => false,
            2, 1, () => expiries++, default));
        Assert.Equal(2, expiries);
    }

    [Theory]
    [InlineData("set-page")]
    [InlineData("pod-page")]
    [InlineData("remaining")]
    [InlineData("missing-version")]
    [InlineData("wrong-set-owner")]
    [InlineData("wrong-controller-uid")]
    [InlineData("wrong-pod-owner")]
    [InlineData("missing-set")]
    [InlineData("duplicate-pod")]
    [InlineData("duplicate-set")]
    [InlineData("missing-controller")]
    [InlineData("foreign-namespace")]
    [InlineData("bound")]
    public void Incomplete_or_ambiguous_snapshots_never_authorize_absence(string defect)
    {
        var f = Fixture(); var options = new RecipientObservationOptions();
        switch (defect)
        {
            case "set-page": f.Sets.Metadata.ContinueProperty = "next"; break;
            case "pod-page": f.Pods.Metadata.ContinueProperty = "next"; break;
            case "remaining": f.Pods.Metadata.RemainingItemCount = 1; break;
            case "missing-version": f.Sets.Metadata.ResourceVersion = null; break;
            case "wrong-controller-uid": f.Sets.Items[0].Metadata.OwnerReferences[0].Uid = "replacement"; break;
            case "wrong-set-owner": f.Sets.Items[0].Metadata.OwnerReferences[0].Name = "wrong"; break;
            case "wrong-pod-owner": f.Pods.Items[0].Metadata.OwnerReferences[0].Uid = "other"; break;
            case "missing-set": f.Sets.Items.Clear(); break;
            case "duplicate-pod": f.Pods.Items.Add(f.Pods.Items[0]); break;
            case "duplicate-set": f.Sets.Items.Add(f.Sets.Items[0]); break;
            case "missing-controller": f.Pods.Items[0].Metadata.OwnerReferences.Clear(); break;
            case "foreign-namespace": f.Pods.Items[0].Metadata.NamespaceProperty = "other"; break;
            case "bound": f.Pods.Items.Add(f.Pods.Items[0]); options = new(MaximumObjects: 1); break;
        }
        Assert.False(DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, options).Absent);
    }

    [Fact]
    public void Forged_ready_labels_do_not_hide_a_Pod_with_the_previous_bootstrap()
    {
        var f = Fixture();
        f.Pods.Items[0].Spec.Containers.Single(x => x.Name == f.Intent.Target.Container).Env.Single(x => x.Name == "HABY_RUNTIME_BOOTSTRAP").Value = "previous";
        Assert.False(DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, new()).Absent);
    }
    [Fact]
    public async Task Oversized_HTTP_response_is_refused_before_SDK_deserialization()
    {
        using var bounded = new BoundedRecipientResponse(1024) { InnerHandler = new RecipientResponse() };
        using var client = new HttpClient(bounded);
        await Assert.ThrowsAsync<IOException>(() => client.GetAsync("https://fixture.invalid/api/v1/namespaces/test/pods"));
    }
    [Fact]
    public void Pod_API_defaults_and_foreign_fields_keep_the_existing_projection_predicate()
    {
        var f = Fixture();
        f.Pods.Items[0] = KubernetesJson.Deserialize<V1Pod>(KubernetesJson.Serialize(f.Pods.Items[0]));
        var pod = f.Pods.Items[0];
        pod.Spec.ServiceAccount = f.Intent.Target.Runtime!.ServiceAccount;
        pod.Spec.DnsPolicy = "ClusterFirst"; pod.Spec.RestartPolicy = "Always";
        pod.Spec.Containers.Single(x => x.Name == f.Intent.Target.Container).ImagePullPolicy = "IfNotPresent";
        pod.Metadata.Annotations = new Dictionary<string, string> { ["fixture.example/foreign"] = "retained" };
        DeploymentRecipientDiagnostic? seen = null;
        var result = DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, new(), value => seen = value);
        Assert.True(result.Absent);
        Assert.Equal(result, DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, new(), _ => throw new IOException("protected")));
        Assert.Equal(new(1, 1, 0, RecipientProjectionFailure.None, RecipientProjectionFailure.None), seen);
    }
    [Fact]
    public void Owned_Pod_mount_admission_difference_reports_the_actual_predicate_without_relaxing_it()
    {
        var f = Fixture();
        f.Pods.Items[0] = KubernetesJson.Deserialize<V1Pod>(KubernetesJson.Serialize(f.Pods.Items[0]));
        f.Pods.Items[0].Spec.Containers.Single(x => x.Name == f.Intent.Target.Container).VolumeMounts.Single(x => x.Name == "haby-runtime-token").MountPropagation = "None";
        DeploymentRecipientDiagnostic? seen = null;
        Assert.False(DeploymentRecipients.Evaluate(f.Intent, f.Receipt, f.Sets, f.Pods, new(), value => seen = value).Absent);
        Assert.Equal(new(1, 1, 1, RecipientProjectionFailure.None, RecipientProjectionFailure.RuntimeMount), seen);
    }
    [Fact]
    public void Confirmed_CREATE_recipient_proof_uses_actual_receipt_UID_without_relaxing_UPDATE_identity()
    {
        var f = Fixture();
        var create = f.Intent with { ExpectedUid = null, LastConfirmed = null };
        var observed = DeploymentRecipients.Evaluate(create, f.Receipt, f.Sets, f.Pods, new());
        Assert.True(observed.Absent, observed.Code);
        Assert.True(DeploymentRecipients.Validate(DeploymentIntents.Encode(create), DeploymentIntents.Encode(f.Receipt), observed));
        Assert.False(DeploymentRecipients.Validate(DeploymentIntents.Encode(create), DeploymentIntents.Encode(f.Receipt with { Uid = "replacement" }), observed));
        var wrongUpdate = f.Intent with { ExpectedUid = "foreign-confirmed-uid" };
        Assert.False(DeploymentRecipients.Evaluate(wrongUpdate, f.Receipt, f.Sets, f.Pods, new()).Absent);
        Assert.False(DeploymentRecipients.Validate(DeploymentIntents.Encode(wrongUpdate), DeploymentIntents.Encode(f.Receipt), observed));
    }

    private sealed class RecipientResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1025]) });
    }

}
