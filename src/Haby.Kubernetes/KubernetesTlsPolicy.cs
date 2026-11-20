using System.Security.Cryptography.X509Certificates;

namespace Haby.Kubernetes;

public enum KubernetesTlsMode { System, CustomCa, Insecure }
public enum KubernetesRevocationMode { Online, Offline, NoCheck }

/// <summary>Immutable normalized server trust policy; credentials and HTTPS are independent.</summary>
public sealed record KubernetesTlsPolicy(KubernetesTlsMode Mode, KubernetesRevocationMode? Revocation)
{
    internal static KubernetesTlsPolicy Normalize(KubernetesTlsPolicy? policy, string? serverCaPem)
    {
        policy ??= new(serverCaPem is null ? KubernetesTlsMode.System : KubernetesTlsMode.CustomCa, KubernetesRevocationMode.Online);
        if (!Enum.IsDefined(policy.Mode) || policy.Revocation is { } revocation && !Enum.IsDefined(revocation) ||
            policy.Mode == KubernetesTlsMode.CustomCa && serverCaPem is null ||
            policy.Mode != KubernetesTlsMode.CustomCa && serverCaPem is not null ||
            policy.Mode == KubernetesTlsMode.Insecure && policy.Revocation is not null)
            throw new ArgumentException("Invalid Kubernetes TLS policy.");
        return policy.Mode == KubernetesTlsMode.Insecure ? policy : policy with { Revocation = policy.Revocation ?? KubernetesRevocationMode.Online };
    }

    internal X509RevocationMode CertificateRevocation => Revocation switch
    {
        KubernetesRevocationMode.Online => X509RevocationMode.Online,
        KubernetesRevocationMode.Offline => X509RevocationMode.Offline,
        _ => X509RevocationMode.NoCheck,
    };

    internal static KubernetesTlsPolicy Parse(string mode, string? revocation, string? serverCaPem) => Normalize(new(mode switch
    {
        "system" => KubernetesTlsMode.System,
        "customCa" => KubernetesTlsMode.CustomCa,
        "insecure" => KubernetesTlsMode.Insecure,
        _ => throw new ArgumentException("Invalid Kubernetes TLS mode."),
    }, revocation switch
    {
        null => null,
        "online" => KubernetesRevocationMode.Online,
        "offline" => KubernetesRevocationMode.Offline,
        "noCheck" => KubernetesRevocationMode.NoCheck,
        _ => throw new ArgumentException("Invalid Kubernetes revocation policy."),
    }), serverCaPem);

    internal string ModeName => Mode switch { KubernetesTlsMode.System => "system", KubernetesTlsMode.CustomCa => "customCa", _ => "insecure" };
    internal string? RevocationName => Revocation switch
    {
        KubernetesRevocationMode.Online => "online",
        KubernetesRevocationMode.Offline => "offline",
        KubernetesRevocationMode.NoCheck => "noCheck",
        _ => null,
    };
}
