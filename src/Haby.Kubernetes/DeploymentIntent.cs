using System.Collections.Immutable;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;
using Haby.Kubernetes.Contracts;

namespace Haby.Kubernetes;

/// <summary>Exact non-secret authority selected by the trusted caller, never inferred from an endpoint.</summary>
public sealed record KubernetesBinding(string Environment, string Provider, long Configuration, string Implementation);
public sealed record DeploymentProjection(string Container, string Image, int Replicas, string Strategy,
    string? MaxSurge, string? MaxUnavailable, int MinReadySeconds, int ProgressDeadlineSeconds,
    ImmutableSortedDictionary<string, string> NodeSelector, ImmutableArray<string> ImagePullSecrets);
public sealed record DeploymentIntent(int Format, KubernetesBinding Binding, string Component, string Workload,
    string Placement, int PlacementRevision, string Namespace, string Name, Guid Owner, Guid ManagedWorkload,
    long TargetGeneration, Guid Operation, DeploymentProjection Target, string? ExpectedUid,
    DeploymentProjection? LastConfirmed);
public enum ConfirmationSource { Response = 1, Observation = 2 }
public sealed record DeploymentReceipt(int Format, KubernetesBinding Binding, string Namespace, string Name,
    Guid Owner, Guid ManagedWorkload, long TargetGeneration, Guid Operation, string Uid, long KubernetesGeneration,
    DeploymentProjection Projection, ConfirmationSource Source);
public enum KernelOutcome { Confirmed, Refused, Conflict, Uncertain, CanceledBeforeDispatch, ObservationUnavailable, NotConfirmed }
/// <summary>Finite value-free diagnostics; never includes SDK exceptions or raw API responses.</summary>
public sealed record KernelResult(KernelOutcome Outcome, string Code, int WriteDispatches, DeploymentReceipt? Receipt = null);

public static class DeploymentIntents
{
    public const string OwnerKey = "haby.dev/owner";
    public const string ManagedKey = "haby.dev/managed-workload";
    public const string OperationKey = "haby.dev/write-operation";
    public const string GenerationKey = "haby.dev/target-generation";
    internal static readonly string[] Reserved = [OwnerKey, ManagedKey, OperationKey, GenerationKey];

    /// <summary>Pure preparation; refuses unsupported effective defaults as well as explicit requests.</summary>
    public static DeploymentIntent Prepare(EffectiveWorkload workload, Guid owner, Guid managedWorkload,
        long generation, Guid operation, DeploymentReceipt? previous = null)
    {
        Need(workload.Declaration.Documents.IsEmpty, "unsupported-fields");
        ValidatePreparation(workload);
        var d = workload.Declaration; var p = workload.Placement;
        var obj = workload.Objects[0]; var provider = workload.Provider;
        var binding = new KubernetesBinding(provider.Environment.ToString()!, provider.Instance.ToString(),
            provider.Configuration.Value, provider.Implementation.Value);
        var target = ProjectionFor(workload);
        if (previous is not null)
        {
            Validate(previous);
            Need(previous.Binding == binding && previous.Namespace == obj.Namespace && previous.Name == obj.Name &&
                previous.Owner == owner && previous.ManagedWorkload == managedWorkload && previous.Projection.Container == target.Container,
                "immutable-identity");
        }
        var intent = new DeploymentIntent(1, binding, workload.Input.Component.Value, workload.Input.Id.Value,
            p.Declaration.Reference.Name.Value, p.Declaration.Reference.Revision, obj.Namespace, obj.Name, owner,
            managedWorkload, generation, operation, target, previous?.Uid, previous?.Projection);
        Validate(intent); return intent;
    }

    private static DeploymentProjection ProjectionFor(EffectiveWorkload workload)
    {
        var d = workload.Declaration; var s = workload.Settings; var p = workload.Placement;
        var strategy = s.Strategy ?? new RolloutStrategy("RollingUpdate", new(25, true), new(25, true));
        string? Bound(RolloutBound? b) => b is null ? null : b.Value.ToString(CultureInfo.InvariantCulture) + (b.Percent ? "%" : "");
        return new DeploymentProjection(d.ContainerName, workload.ImageReference, s.Replicas ?? 1, strategy.Type,
            Bound(strategy.MaxSurge), Bound(strategy.MaxUnavailable), s.MinReadySeconds ?? 0, s.RolloutSeconds ?? 600,
            p.Declaration.NodeSelector.ToImmutableSortedDictionary(StringComparer.Ordinal), p.Declaration.ImagePullSecrets.Order(StringComparer.Ordinal).ToImmutableArray());
    }

    // Validates every supported effective field while deliberately granting no write intent.
    internal static void ValidatePreparation(EffectiveWorkload workload)
    {
        var d = workload.Declaration; var s = workload.Settings; var p = workload.Placement;
        Need(d.Environment.Count == 0 && d.Ports.Count == 0 && d.Volumes.Count == 0 && d.Mounts.IsEmpty &&
            d.Routes.IsEmpty && d.Probes == new Probes(null, null, null) &&
            s.Resources is null && d.Overrides.Resources is null && p.Declaration.Settings.Resources is null &&
            s.Service != true && s.HeadlessService != true && s.Ingress?.Enabled != true,
            "unsupported-fields");
        Need(workload.Objects.Length == (d.Documents.IsEmpty ? 1 : 2), "unsupported-objects");
        if (!d.Documents.IsEmpty)
        {
            var account = workload.Objects[1];
            Need(account.Group == "" && account.Kind == "ServiceAccount" && account.Provider == workload.Provider.Instance &&
                account.Namespace == p.Declaration.Namespace, "unsupported-objects");
        }
        var obj = workload.Objects[0]; var provider = workload.Provider;
        Need(obj.Group == "apps" && obj.Kind == "Deployment" && obj.Provider == provider.Instance &&
            obj.Namespace == p.Declaration.Namespace && p.Provider == provider.Instance && p.Configuration == provider.Configuration &&
            p.Implementation == provider.Implementation && p.Environment == provider.Environment && p.Alias == provider.Alias &&
            provider.Input == workload.Input, "exact-input");
        Projection(ProjectionFor(workload));
    }

    public static string Encode(DeploymentIntent intent) { Validate(intent); return JsonSerializer.Serialize(intent, KernelJson.Default.DeploymentIntent); }
    public static DeploymentIntent Decode(string json)
    {
        Need(json is { Length: > 0 and <= 1_048_576 }, "intent-format");
        try { var value = JsonSerializer.Deserialize(json, KernelJson.Default.DeploymentIntent); Need(value is not null, "intent-format"); Validate(value!); return value!; }
        catch (JsonException) { throw new WorkloadContractException("intent-format"); }
        catch (ArgumentException) { throw new WorkloadContractException("intent-format"); }
    }
    public static string Encode(DeploymentReceipt receipt) { Validate(receipt); return JsonSerializer.Serialize(receipt, KernelJson.Default.DeploymentReceipt); }
    public static DeploymentReceipt DecodeReceipt(string json)
    {
        Need(json is { Length: > 0 and <= 1_048_576 }, "receipt-format");
        try { var value = JsonSerializer.Deserialize(json, KernelJson.Default.DeploymentReceipt); Need(value is not null, "receipt-format"); Validate(value!); return value!; }
        catch (JsonException) { throw new WorkloadContractException("receipt-format"); }
        catch (ArgumentException) { throw new WorkloadContractException("receipt-format"); }
    }
    internal static void Validate(DeploymentIntent i)
    {
        Need(i is { Format: 1, Binding: not null, Target: not null } && i.Owner != Guid.Empty && i.ManagedWorkload != Guid.Empty &&
            i.Operation != Guid.Empty && i.TargetGeneration > 0 && i.PlacementRevision > 0, "intent-format");
        Binding(i.Binding); Name(i.Namespace); Name(i.Name); Name(i.Component); Name(i.Workload); Name(i.Placement);
        Need(i.Namespace.Length <= 63 && !i.Namespace.Contains('.'), "namespace");
        Projection(i.Target); Need((i.ExpectedUid is null) == (i.LastConfirmed is null), "intent-update-pair");
        if (i.LastConfirmed is not null) { Text(i.ExpectedUid!, 128); Projection(i.LastConfirmed); Need(i.LastConfirmed.Container == i.Target.Container, "immutable-container"); }
    }
    internal static void Validate(DeploymentReceipt r)
    {
        Need(r is { Format: 1, Binding: not null, Projection: not null } && r.Owner != Guid.Empty && r.ManagedWorkload != Guid.Empty &&
            r.Operation != Guid.Empty && r.TargetGeneration > 0 && r.KubernetesGeneration > 0 && Enum.IsDefined(r.Source), "receipt-format");
        Binding(r.Binding); Name(r.Namespace); Name(r.Name); Text(r.Uid, 128); Projection(r.Projection);
    }
    internal static void Binding(KubernetesBinding b)
    {
        Need(b is not null && Guid.TryParse(b.Environment, out var e) && e != Guid.Empty && Guid.TryParse(b.Provider, out var p) && p != Guid.Empty && b.Configuration > 0, "binding");
        _ = Haby.Domain.EnvironmentRecordId.Parse(b.Environment); _ = Haby.Domain.ProviderInstanceId.Parse(b.Provider);
        Text(b.Implementation, 128);
    }
    internal static void Projection(DeploymentProjection p)
    {
        Need(p is not null && p.NodeSelector is not null && !p.ImagePullSecrets.IsDefault, "projection");
        Need(p.NodeSelector.Count <= 256 && p.ImagePullSecrets.Length <= 256, "projection-size");
        Name(p.Container); Text(p.Image, 512); Need(!p.Image.Any(char.IsWhiteSpace), "image");
        // Reuse the current portable image/container grammar; a reloaded intent cannot bypass it.
        KubernetesContract.DecodeWorkload(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["containerName"] = p.Container,
            ["image"] = new JsonObject { ["reference"] = p.Image }
        }.ToJsonString()));
        Need(p.Replicas is >= 1 and <= 10000 && p.MinReadySeconds is >= 0 and <= 3600 && p.ProgressDeadlineSeconds <= 86400 && p.ProgressDeadlineSeconds > p.MinReadySeconds &&
            (p.Strategy == "Recreate" && p.MaxSurge is null && p.MaxUnavailable is null ||
             p.Strategy == "RollingUpdate" && ValidBound(p.MaxSurge) && ValidBound(p.MaxUnavailable) && (p.MaxSurge != "0" && p.MaxSurge != "0%" || p.MaxUnavailable != "0" && p.MaxUnavailable != "0%")), "projection-settings");
        foreach (var (k, v) in p.NodeSelector) { Need(!Reserved.Contains(k), "reserved-key"); Text(k, 253); Need(v is not null && v.Length <= 63 && !v.Any(char.IsControl), "node-selector"); }
        foreach (var n in p.ImagePullSecrets) Name(n);
        Need(p.ImagePullSecrets.Distinct(StringComparer.Ordinal).Count() == p.ImagePullSecrets.Length, "duplicate-key");
        var selector = new JsonObject(); foreach (var (key, value) in p.NodeSelector) selector[key] = value;
        var pulls = new JsonArray(); foreach (var name in p.ImagePullSecrets) pulls.Add(name);
        KubernetesContract.DecodePlacement(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["apiVersion"] = "haby.dev/v1alpha1",
            ["kind"] = "KubernetesPlacement",
            ["metadata"] = new JsonObject { ["name"] = "kernel", ["revision"] = 1 },
            ["spec"] = new JsonObject { ["namespace"] = "kernel", ["nodeSelector"] = selector, ["imagePullSecrets"] = pulls }
        }.ToJsonString()));
    }
    private static bool ValidBound(string? s) => s is not null && Regex.IsMatch(s, "\\A(0|[1-9][0-9]*)(%)?\\z", RegexOptions.CultureInvariant) &&
        int.TryParse(s.TrimEnd('%'), out var value) && value >= 0 && (!s.EndsWith('%') || value <= 100);
    internal static void Name(string s) => Need(s is not null && s.Length <= 253 && s.Split('.').All(part =>
        Regex.IsMatch(part, "\\A[a-z0-9]([a-z0-9-]*[a-z0-9])?\\z", RegexOptions.CultureInvariant)), "name");
    internal static void Text(string s, int max) => Need(!string.IsNullOrWhiteSpace(s) && s.Length <= max && !s.Any(char.IsControl), "value");
    internal static void Need([DoesNotReturnIf(false)] bool condition, string code) { if (!condition) throw new WorkloadContractException(code); }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(DeploymentIntent))]
[JsonSerializable(typeof(DeploymentReceipt))]
internal partial class KernelJson : JsonSerializerContext;
