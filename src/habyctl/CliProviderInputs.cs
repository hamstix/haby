using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliProviderInput(string Kind, string ResourceId, string ComponentId, string WorkloadId,
    string EnvironmentRecordId, string ProviderAlias, string ProviderInstanceId, long ConfigurationRevision,
    string ImplementationId, string NamingContract, string NamingJson, string PlacementContract, string PlacementJson);

internal static partial class PlanningPresentation
{
    internal static CliProviderInput[] ProviderInputs(IEnumerable<FrozenProviderInput> inputs) => inputs.Select(x =>
        new CliProviderInput(x.Kind, x.ResourceId, x.ComponentId, x.WorkloadId, x.EnvironmentRecordId, x.ProviderAlias,
            x.ProviderInstanceId, x.ConfigurationRevision, x.ImplementationId, x.NamingContract, x.NamingJson, x.PlacementContract, x.PlacementJson)).ToArray();
}

internal sealed partial class CliTextPresenter
{
    private void PrintProviderInput(CliProviderInput input)
    {
        var key = input.Kind == "resource" ? input.ResourceId : input.ComponentId + "/" + input.WorkloadId;
        _output.WriteLine($"Provider input: {input.Kind} {key}; {input.ProviderAlias} -> {input.ProviderInstanceId} revision {input.ConfigurationRevision}; implementation: {input.ImplementationId}");
        if (input.NamingContract.Length > 0) _output.WriteLine($"Naming: {input.NamingContract}; {input.NamingJson}");
        if (input.PlacementContract.Length > 0) _output.WriteLine($"Placement: {input.PlacementContract}; {input.PlacementJson}");
    }
}
