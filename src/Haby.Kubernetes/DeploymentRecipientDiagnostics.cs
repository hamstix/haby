using Haby.Kubernetes.Contracts;
namespace Haby.Kubernetes;

internal enum RecipientProjectionFailure { None, Container, Resources, Ports, Probes, RuntimeAccount, RuntimeBootstrap, RuntimeVolume, RuntimeMount, RuntimeInjection, Shape, Unknown }
internal sealed record DeploymentRecipientDiagnostic(int Parents, int Recipients, int Previous,
    RecipientProjectionFailure ParentFailure, RecipientProjectionFailure PodFailure);
internal static class DeploymentRecipientDiagnostics
{
    internal static RecipientProjectionFailure Failure(Exception error) => error is WorkloadContractException contract ? contract.Code switch
    {
        "container-drift" or "container-identity" => RecipientProjectionFailure.Container,
        "resource-drift" or "resource-shape" => RecipientProjectionFailure.Resources,
        "ports-shape" or "port-shape" or "port-identity" or "port-drift" => RecipientProjectionFailure.Ports,
        "probe-drift" => RecipientProjectionFailure.Probes,
        "runtime-account-drift" => RecipientProjectionFailure.RuntimeAccount,
        "runtime-bootstrap-drift" => RecipientProjectionFailure.RuntimeBootstrap,
        "runtime-volume-drift" => RecipientProjectionFailure.RuntimeVolume,
        "runtime-mount-drift" or "runtime-mount-collision" => RecipientProjectionFailure.RuntimeMount,
        "runtime-token-injection" => RecipientProjectionFailure.RuntimeInjection,
        "runtime-list-shape" or "runtime-list-identity" => RecipientProjectionFailure.Shape,
        _ => RecipientProjectionFailure.Unknown
    } : RecipientProjectionFailure.Unknown;
    internal static void Emit(Action<DeploymentRecipientDiagnostic>? observer, DeploymentRecipientDiagnostic value)
    { try { observer?.Invoke(value); } catch { } }
}
