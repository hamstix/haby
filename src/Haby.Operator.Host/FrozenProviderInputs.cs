using Haby.Application;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    private static IEnumerable<FrozenProviderInput> ProviderInputSummaries(DurablePlanningInputs inputs) =>
        inputs.Providers.Entries.Values.Select(x => new FrozenProviderInput
        {
            Kind = x.Input is ProviderInput.Resource ? "resource" : "workload",
            ResourceId = x.Input is ProviderInput.Resource r ? r.Id.Value : "",
            ComponentId = x.Input is ProviderInput.Workload w ? w.Component.Value : "",
            WorkloadId = x.Input is ProviderInput.Workload workload ? workload.Id.Value : "",
            EnvironmentRecordId = x.Environment.ToString(),
            ProviderAlias = x.Alias.Value,
            ProviderInstanceId = x.Instance.ToString(),
            ConfigurationRevision = x.Configuration.Value,
            ImplementationId = x.Implementation.Value,
            NamingContract = x.Naming?.Contract.Value ?? "",
            NamingJson = x.Naming is { } naming ? ConfigurationJson.Serialize(naming.Value).Json ?? throw new InvalidDataException() : "",
            PlacementContract = x.Placement?.Contract.Value ?? "",
            PlacementJson = x.Placement is { } placement ? ConfigurationJson.Serialize(placement.Value).Json ?? throw new InvalidDataException() : ""
        }).OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.ResourceId, StringComparer.Ordinal)
            .ThenBy(x => x.ComponentId, StringComparer.Ordinal).ThenBy(x => x.WorkloadId, StringComparer.Ordinal);
}
