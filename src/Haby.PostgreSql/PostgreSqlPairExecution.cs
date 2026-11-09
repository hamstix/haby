using Haby.Application;

namespace Haby.PostgreSql;

internal static class PostgreSqlPairExecution
{
    // Retain the four-step effect fixture and admit the exact complete policy graph.
    internal static bool IsSupported(AcceptedOperation accepted, PreparedProviderConfiguration configuration)
    {
        if (accepted.Graph.Nodes.Count != 4 || accepted.Graph.Baseline is not null)
        {
            var admission = new DurableActionAdmission(new(new IDurableActionAdmissionHandler[]
                { new PostgreSqlDatabaseCreateAdmission(), new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id) }, []));
            try
            {
                return admission.ValidateCreationGraph(accepted.Spec,
                    new("postgresql/v1", "haby.postgresql.connection/v1", PostgreSqlDatabaseCheckpointCapability.ImplementationId),
                    accepted.Inputs, configuration, accepted.Graph);
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException) { return false; }
        }
        try
        {
            var expected = new List<DurableOperationNode>();
            foreach (var resource in accepted.Spec.Resources)
            {
                var admission = new PostgreSqlPairResourceAdmission(resource.Value.Type);
                if (!admission.Validate(accepted.Spec, resource.Key, accepted.Inputs, configuration)) return false;
                expected.AddRange(admission.Plan(accepted.Spec, resource.Key, accepted.Inputs, configuration)
                    .Where(n => n.Action != PostgreSqlApplicationAccessAction.RegistrationKey));
            }
            return new DurableOperationGraph(expected).Equals(accepted.Graph);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException) { return false; }
    }
}
