using Haby.Application;
using Haby.Domain;

namespace Haby.Persistence.PostgreSql;

/// <summary>One current durable format across every accepted-history reader.</summary>
internal static class DurableInputStorage
{
    internal const int Version = 4;
    internal static DurablePlanningInputs Inputs(this DurablePlanRow row) => Read(row.SnapshotFormatVersion,
        row.EnvironmentRecordId, row.EnvironmentKey, row.ApplicationRecordId, row.RevisionNumber,
        row.ConfigurationJson, row.ProviderInputsJson, row.GraphJson, row.Fingerprint);
    internal static DurablePlanningInputs Inputs(this DeploymentGenerationRow row) => Read(row.SnapshotFormatVersion,
        row.EnvironmentRecordId, row.EnvironmentKey, row.ApplicationRecordId, row.RevisionNumber,
        row.ConfigurationJson, row.ProviderInputsJson, row.GraphJson, row.Fingerprint);

    private static DurablePlanningInputs Read(int version, Guid environment, string environmentKey, Guid application,
        long revision, string configurationJson, string providerInputsJson, string graphJson, string fingerprint)
    {
        if (version != Version) throw new InvalidDataException("Unsupported durable input format. Explicitly initialize a fresh catalog; external resources remain unchanged.");
        var reference = ManifestRevisionRef.Create(ApplicationRecordId.Parse(application.ToString()), ManifestRevisionNumber.Create(revision));
        var configuration = ConfigurationPlanningSnapshotCodec.Decode(configurationJson, reference);
        var environmentId = EnvironmentRecordId.Parse(environment.ToString());
        var providers = ExactProviderResolutionSetCodec.Decode(providerInputsJson, configuration.Manifest, environmentId);
        var inputs = new DurablePlanningInputs(environmentId, EnvironmentId.Create(environmentKey), reference, providers) { Configuration = configuration };
        if (DurableOperationGraphCodec.Fingerprint(inputs, DurableOperationGraphCodec.Decode(graphJson)) != fingerprint)
            throw new InvalidDataException("Durable input fingerprint mismatch.");
        return inputs;
    }
}
