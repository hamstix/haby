using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Kubernetes.Contracts;

public abstract record WorkloadImage
{
    private WorkloadImage() { }
    public sealed record Composed(string Name, string? Tag, bool FromApplicationVersion) : WorkloadImage;
    public sealed record Complete(string Reference) : WorkloadImage;
}
public sealed record ContainerPort(int Number, string Protocol);
public sealed record ObjectKey(string Name, string Key, bool Optional);
public abstract record EnvironmentValue
{
    private EnvironmentValue() { }
    public sealed record Literal(string Value) : EnvironmentValue;
    public sealed record ConfigMap(ObjectKey Reference) : EnvironmentValue;
    public sealed record Secret(ObjectKey Reference) : EnvironmentValue;
}
public sealed record Probe(string Port, string? Path, string? Scheme, int InitialDelaySeconds,
    int PeriodSeconds, int TimeoutSeconds, int SuccessThreshold, int FailureThreshold);
public sealed record Probes(Probe? Startup, Probe? Liveness, Probe? Readiness);
public sealed record ExistingVolume(string Kind, string Name, bool Optional, bool ReadOnly);
public sealed record VolumeMount(string Volume, string MountPath, bool ReadOnly, string? SubPath);
public sealed record Route(string HostLabel, string Path, string Port, string PathType);
public sealed record ResourceQuantities(string? Cpu, string? Memory);
public sealed record ResourceRequirements(ResourceQuantities? Requests, ResourceQuantities? Limits);
public sealed record RolloutBound(int Value, bool Percent);
public sealed record RolloutStrategy(string Type, RolloutBound? MaxSurge, RolloutBound? MaxUnavailable);
public sealed record IngressPolicy(bool Enabled, string? ClassName, string? Domain, string? TlsSecret);
public sealed record OperationalSettings(int? Replicas, RolloutStrategy? Strategy, int? MinReadySeconds,
    int? RolloutSeconds, ResourceRequirements? Resources, bool? Service, bool? HeadlessService, IngressPolicy? Ingress);
public sealed record KubernetesWorkload(WorkloadImage Image, string ContainerName, string? NameOverride,
    ImmutableDictionary<string, ContainerPort> Ports, ImmutableDictionary<string, EnvironmentValue> Environment,
    Probes Probes, ImmutableDictionary<string, ExistingVolume> Volumes, ImmutableArray<VolumeMount> Mounts,
    ImmutableArray<Route> Routes, ImmutableArray<ManifestLocalId> Documents, OperationalSettings Overrides);
public sealed record PlacementReference(ManifestLocalId Name, int Revision);
public sealed class KubernetesPlacement
{
    internal KubernetesPlacement(PlacementReference reference, string @namespace, string? registry,
        string? repositoryPrefix, ImmutableDictionary<string, string> nodeSelector,
        ImmutableArray<string> imagePullSecrets, OperationalSettings settings)
    {
        Reference = reference; Namespace = @namespace; Registry = registry; RepositoryPrefix = repositoryPrefix;
        NodeSelector = nodeSelector; ImagePullSecrets = imagePullSecrets; Settings = settings;
    }
    public PlacementReference Reference { get; }
    public string Namespace { get; }
    public string? Registry { get; }
    public string? RepositoryPrefix { get; }
    public ImmutableDictionary<string, string> NodeSelector { get; }
    public ImmutableArray<string> ImagePullSecrets { get; }
    public OperationalSettings Settings { get; }
}

/// <summary>A safe, value-free refusal from portable decoding or pure resolution.</summary>
public sealed class WorkloadContractException(string code) : ArgumentException(code)
{
    public string Code { get; } = code;
}
