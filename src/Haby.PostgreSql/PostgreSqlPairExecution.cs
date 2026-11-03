using Haby.Application;

namespace Haby.PostgreSql;

internal static class PostgreSqlPairExecution
{
    // Only the exact accepted policy's four-step internal prefix. This never admits
    // a different manifest policy or makes a five-step generation executable.
    internal static bool IsPrefix(AcceptedOperation accepted, PreparedProviderConfiguration configuration)
    {
        if (accepted.Graph.Nodes.Count != 4) return false;
        try
        {
            var expected = new List<DurableOperationNode>();
            foreach (var resource in accepted.Spec.Resources)
            {
                var admission = new PostgreSqlPairResourceAdmission(resource.Value.Type);
                if (!admission.Validate(accepted.Spec, resource.Key, accepted.Inputs, configuration)) return false;
                expected.AddRange(admission.Plan(accepted.Spec, resource.Key, accepted.Inputs, configuration)
                    .Where(n => n.Id.Value != "app-login-access"));
            }
            return new DurableOperationGraph(expected).Equals(accepted.Graph);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException) { return false; }
    }
}
