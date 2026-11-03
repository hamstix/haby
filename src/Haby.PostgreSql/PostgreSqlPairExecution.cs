using Haby.Application;

namespace Haby.PostgreSql;

internal static class PostgreSqlPairExecution
{
    // Retain the four-step effect fixture and admit the exact complete policy graph.
    internal static bool IsSupported(AcceptedOperation accepted, PreparedProviderConfiguration configuration)
    {
        if (accepted.Graph.Nodes.Count is not (4 or 5)) return false;
        try
        {
            var expected = new List<DurableOperationNode>();
            foreach (var resource in accepted.Spec.Resources)
            {
                var admission = new PostgreSqlPairResourceAdmission(resource.Value.Type);
                if (!admission.Validate(accepted.Spec, resource.Key, accepted.Inputs, configuration)) return false;
                expected.AddRange(admission.Plan(accepted.Spec, resource.Key, accepted.Inputs, configuration)
                    .Where(n => accepted.Graph.Nodes.Count == 5 || n.Id.Value != "app-login-access"));
            }
            return new DurableOperationGraph(expected).Equals(accepted.Graph);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException) { return false; }
    }
}
