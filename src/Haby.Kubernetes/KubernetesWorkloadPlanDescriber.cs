using Haby.Application;
using Haby.Domain;

namespace Haby.Kubernetes;

/// <summary>Pure projection from frozen inputs; never resolves current selectors or credentials.</summary>
public sealed class KubernetesWorkloadPlanDescriber : IWorkloadPlanDescriber
{
    public string ImplementationId => KubernetesProviderConfigurationCodec.Implementation;
    public WorkloadPlanItem Describe(ManifestRevision revision, DurablePlanningInputs inputs, ProviderInput.Workload subject)
    {
        var effective = InternalDeploymentAction.Resolve(inputs, revision.ApplicationVersion.Value, subject);
        return new(subject.Component.Value, subject.Id.Value, inputs.Providers.Declaration.Spec.Components[subject.Component].Workloads[subject.Id].Type.Value,
            effective.Objects.Single(x => x.Kind == "Deployment").Name, effective.ImageReference,
            effective.Declaration.Documents.Select(x => x.Value).Order(StringComparer.Ordinal).ToArray());
    }
}
