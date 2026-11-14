using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using k8s;
using k8s.Autorest;
using k8s.Models;
using static Haby.Kubernetes.DeploymentIntents;

namespace Haby.Kubernetes;

internal sealed record WorkloadTarget(string Namespace, string BaseName);
internal sealed record RuntimeAccountIntent(int Format, KubernetesBinding Binding, Guid Installation, Guid Owner,
    Guid ManagedWorkload, long Generation, Guid Operation, string Namespace, string Name);
internal sealed record RuntimeAccountReceipt(RuntimeAccountIntent Intent, string Uid, bool Automount, ConfirmationSource Source);
internal sealed record RuntimeValidationContext(int Format, KubernetesBinding Binding, string Audience, string Namespace,
    string ServiceAccountUid, string Mode);

internal static class RuntimeIdentityCodec
{
    internal const string Role = "runtime-identity";
    internal const string ValidationContract = "haby.kubernetes.runtime-validation/v1";
    internal static InternalManagedTarget Target(EffectiveWorkload workload) => new("haby.kubernetes.workload-target/v1", 1,
        JsonSerializer.Serialize(new WorkloadTarget(workload.Placement.Declaration.Namespace,
            workload.Objects.Single(x => x.Kind == "Deployment").Name), RuntimeIdentityJson.Default.WorkloadTarget));
    internal static string Encode(RuntimeAccountIntent value)
    { Validate(value); return JsonSerializer.Serialize(value, RuntimeIdentityJson.Default.RuntimeAccountIntent); }
    internal static RuntimeAccountIntent Decode(string value)
    {
        Need(value.Length is > 0 and <= 16384, "runtime-intent-size");
        try { var result = JsonSerializer.Deserialize(value, RuntimeIdentityJson.Default.RuntimeAccountIntent); Need(result is not null, "runtime-intent"); Validate(result!); return result!; }
        catch (JsonException) { throw new WorkloadContractException("runtime-intent"); }
    }
    internal static string Encode(RuntimeAccountReceipt value) => JsonSerializer.Serialize(value, RuntimeIdentityJson.Default.RuntimeAccountReceipt);
    internal static RuntimeAccountReceipt Receipt(string value)
    {
        Need(value.Length is > 0 and <= 32768, "runtime-receipt-size");
        try
        {
            var result = JsonSerializer.Deserialize(value, RuntimeIdentityJson.Default.RuntimeAccountReceipt);
            Need(result is not null && !result.Automount && Enum.IsDefined(result.Source), "runtime-receipt");
            Validate(result!.Intent); Text(result.Uid, 128); return result;
        }
        catch (JsonException) { throw new WorkloadContractException("runtime-receipt"); }
    }
    internal static void Validate(RuntimeAccountIntent value)
    {
        Need(value.Format == 1 && value.Installation != Guid.Empty && value.Owner != Guid.Empty && value.ManagedWorkload != Guid.Empty &&
            value.Operation != Guid.Empty && value.Generation > 0, "runtime-intent");
        Binding(value.Binding); Name(value.Namespace); Name(value.Name);
        Need(value.Namespace.Length <= 63 && !value.Namespace.Contains('.') && value.Name.EndsWith("-runtime", StringComparison.Ordinal), "runtime-target");
    }
    internal static string Audience(RuntimeAccountIntent value) => $"urn:haby:{value.Installation:D}:{Guid.Parse(value.Binding.Provider):D}";
    internal static string Validation(RuntimeAccountReceipt receipt) => JsonSerializer.Serialize(new RuntimeValidationContext(1,
        receipt.Intent.Binding, Audience(receipt.Intent), receipt.Intent.Namespace, receipt.Uid, "service-account-bound"),
        RuntimeIdentityJson.Default.RuntimeValidationContext);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(WorkloadTarget))]
[JsonSerializable(typeof(RuntimeAccountIntent))]
[JsonSerializable(typeof(RuntimeAccountReceipt))]
[JsonSerializable(typeof(RuntimeValidationContext))]
internal partial class RuntimeIdentityJson : JsonSerializerContext;

/// <summary>Friend-only preparation. No publication, token, RBAC or Deployment effect.</summary>
internal sealed class InternalRuntimeIdentityAction(ISecretStore secrets,
    Func<KubernetesConnection, CancellationToken, ValueTask<KernelClient>>? fixtureConnection = null) : IInternalActionHandler
{
    internal const string Create = "haby.kubernetes.runtime-identity-create/v1";
    public InternalActionContract Contract { get; } = new("kubernetes.workload/v1alpha1", KubernetesProviderConfigurationCodec.Kind,
        KubernetesProviderConfigurationCodec.Shape, KubernetesProviderConfigurationCodec.Implementation, Create,
        "haby.kubernetes.runtime-account-intent/v1", "haby.kubernetes.runtime-account-receipt/v1");
    public bool ObservationRecovery => true;
    public bool RequiresPreparedConfiguration => true;
    public InternalManagedTarget ManagedTarget(InternalActionContext context) => RuntimeIdentityCodec.Target(InternalDeploymentAction.Resolve(context));
    public string Prepare(InternalActionContext context)
    {
        var workload = InternalDeploymentAction.Resolve(context);
        DeploymentIntents.ValidatePreparation(workload);
        Need(!workload.Declaration.Documents.IsEmpty, "configuration-not-selected");
        var name = workload.Objects.Single(x => x.Kind == "ServiceAccount");
        return RuntimeIdentityCodec.Encode(new RuntimeAccountIntent(1,
            new(context.Provider.Environment.ToString(), context.Provider.Instance.ToString(), context.Provider.Configuration.Value, context.Provider.Implementation.Value),
            context.InstallationId, Guid.Parse(context.Key.DeploymentId.ToString()), context.ManagedIdentity,
            context.Key.Generation.Value, context.Marker, name.Namespace, name.Name));
    }
    public bool Validate(InternalActionContext context, string payload)
    {
        try { return RuntimeIdentityCodec.Encode(RuntimeIdentityCodec.Decode(payload)) == payload && Prepare(context) == payload; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException) { return false; }
    }
    public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation)
    {
        try
        {
            var result = RuntimeIdentityCodec.Receipt(receipt);
            return Validate(context, payload) && RuntimeIdentityCodec.Encode(result.Intent) == payload &&
                result.Source == (observation ? ConfirmationSource.Observation : ConfirmationSource.Response);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException) { return false; }
    }
    public InternalIdentityConfirmation IdentityConfirmation(InternalActionContext context, string payload, string receipt)
    {
        var result = RuntimeIdentityCodec.Receipt(receipt);
        Need(ValidateReceipt(context, payload, receipt, result.Source == ConfirmationSource.Observation), "runtime-confirmation");
        return new(RuntimeIdentityCodec.Role, Contract.OutputContract, 1, receipt,
            "haby.kubernetes.token-review/v1", RuntimeIdentityCodec.Audience(result.Intent), RuntimeIdentityCodec.ValidationContract, RuntimeIdentityCodec.Validation(result));
    }
    private ValueTask<KernelClient> Connect(InternalActionContext context, CancellationToken token)
    {
        var config = new KubernetesProviderConfigurationCodec().DecodeTyped(context.Configuration.PayloadJson);
        var binding = new KubernetesBinding(context.Provider.Environment.ToString(), context.Provider.Instance.ToString(),
            context.Provider.Configuration.Value, context.Provider.Implementation.Value);
        var connection = new KubernetesConnection(binding, config.Endpoint, config.ServerCaPem, config.BearerToken, config.ClientCertificate, config.ClientPrivateKey);
        return fixtureConnection?.Invoke(connection, token) ?? KubernetesClientFactory.Create(connection, secrets, token);
    }
    public ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token) => Run(context, payload, false, token);
    public ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token) => Run(context, payload, true, token);
    private async ValueTask<InternalActionResult> Run(InternalActionContext context, string payload, bool observe, CancellationToken token)
    {
        var dispatched = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Need(Validate(context, payload), "runtime-intent"); var intent = RuntimeIdentityCodec.Decode(payload);
            deadline.Token.ThrowIfCancellationRequested(); using var lease = await Connect(context, deadline.Token); var client = lease.Client;
            if (!observe)
            {
                var user = (await client.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: deadline.Token)).Status?.UserInfo;
                Need(!string.IsNullOrWhiteSpace(user?.Username) && user.Username != "system:anonymous" && user.Groups?.Contains("system:unauthenticated") != true, "authentication-required");
                // Only the actual cluster-scoped namespace GET and selected namespace account GET/CREATE.
                foreach (var capability in new[] { ("get", "namespaces", "", intent.Namespace), ("get", "serviceaccounts", intent.Namespace, intent.Name), ("create", "serviceaccounts", intent.Namespace, "") })
                {
                    var review = await client.AuthorizationV1.CreateSelfSubjectAccessReviewAsync(new V1SelfSubjectAccessReview
                    {
                        Spec = new V1SelfSubjectAccessReviewSpec
                        {
                            ResourceAttributes = new V1ResourceAttributes
                            { Verb = capability.Item1, Group = "", Resource = capability.Item2, NamespaceProperty = capability.Item3, Name = capability.Item4 }
                        }
                    }, cancellationToken: deadline.Token);
                    Need(review.Status?.Allowed == true && review.Status.Denied != true && string.IsNullOrEmpty(review.Status.EvaluationError), "capability-denied");
                }
                using var ns = await client.CoreV1.ReadNamespaceWithHttpMessagesAsync(intent.Namespace, cancellationToken: deadline.Token);
                Need(ns.Body.Metadata?.Name == intent.Namespace && ns.Body.Metadata.DeletionTimestamp is null && ns.Body.Status?.Phase == "Active", "namespace-unavailable");
            }
            V1ServiceAccount? account;
            try { using var read = await client.CoreV1.ReadNamespacedServiceAccountWithHttpMessagesAsync(intent.Name, intent.Namespace, cancellationToken: deadline.Token); account = read.Body; }
            catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound) { account = null; }
            if (observe)
            {
                if (account is null) return new(InternalActionOutcome.NotConfirmed, "object-absent");
                return Confirm(account, intent, ConfirmationSource.Observation);
            }
            if (account is not null) return new(InternalActionOutcome.Refused, "occupied-name");
            var body = new V1ServiceAccount
            {
                ApiVersion = "v1",
                Kind = "ServiceAccount",
                AutomountServiceAccountToken = false,
                Metadata = new V1ObjectMeta
                {
                    Name = intent.Name,
                    NamespaceProperty = intent.Namespace,
                    Labels = new Dictionary<string, string> { [OwnerKey] = intent.Owner.ToString("N"), [ManagedKey] = intent.ManagedWorkload.ToString("N") },
                    Annotations = new Dictionary<string, string> { [OperationKey] = intent.Operation.ToString("N"), [GenerationKey] = intent.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                }
            };
            deadline.Token.ThrowIfCancellationRequested(); dispatched = true;
            using var response = await client.CoreV1.CreateNamespacedServiceAccountWithHttpMessagesAsync(body, intent.Namespace, cancellationToken: deadline.Token);
            return Confirm(response.Body, intent, ConfirmationSource.Response);
        }
        catch (WorkloadContractException) { return new(dispatched ? InternalActionOutcome.Uncertain : observe ? InternalActionOutcome.NotConfirmed : InternalActionOutcome.Refused, dispatched ? "response-unconfirmed" : "identity-evidence-refused"); }
        catch (Exception) { return new(dispatched ? InternalActionOutcome.Uncertain : observe ? InternalActionOutcome.Unavailable : InternalActionOutcome.Refused, dispatched ? "dispatch-uncertain" : observe ? "observation-unavailable" : "identity-preflight-refused"); }
    }
    private static string? Value(IDictionary<string, string>? values, string key) => values is not null && values.TryGetValue(key, out var value) ? value : null;
    private static InternalActionResult Confirm(V1ServiceAccount account, RuntimeAccountIntent intent, ConfirmationSource source)
    {
        var meta = account.Metadata;
        Need(account.ApiVersion == "v1" && account.Kind == "ServiceAccount" && meta is not null && meta.Name == intent.Name && meta.NamespaceProperty == intent.Namespace && meta.DeletionTimestamp is null &&
            Value(meta.Labels, OwnerKey) == intent.Owner.ToString("N") &&
            Value(meta.Labels, ManagedKey) == intent.ManagedWorkload.ToString("N") &&
            Value(meta.Annotations, OperationKey) == intent.Operation.ToString("N") &&
            Value(meta.Annotations, GenerationKey) == intent.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            account.AutomountServiceAccountToken == false, "account-projection");
        Text(meta!.Uid, 128);
        return new(InternalActionOutcome.Confirmed, source == ConfirmationSource.Response ? "response-confirmed" : "observation-confirmed",
            RuntimeIdentityCodec.Encode(new RuntimeAccountReceipt(intent, meta.Uid, false, source)));
    }
}
