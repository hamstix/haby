using System.Globalization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.PostgreSql;

/// <summary>Equivalent ordinary output contracts for exact import producers. No CREATE evidence.</summary>
public sealed class PostgreSqlImportOutputContract(ResourceTypeId type, PostgreSqlProviderConfigurationCodec codec,
    IApplicationCredentialReader credentials) : IResourceObservationOutputContract
{
    public DurableActionExecutionKey Producer => type == PostgreSqlDatabaseContract.Id ? PostgreSqlImportContracts.Database : PostgreSqlImportContracts.Login;
    public ExternalOperationOutputIdentity Identity => type == PostgreSqlDatabaseContract.Id
        ? new(PostgreSqlDatabaseOutputContract.Contract, PostgreSqlDatabaseOutputContract.Key)
        : new(PostgreSqlApplicationConnectionContract.Contract, PostgreSqlApplicationConnectionContract.Key);

    public bool ValidateObservationEvidence(AcceptedOperation accepted, DurableActionPayload evidence) =>
        PostgreSqlImportContracts.Decode(accepted.Node) is { } input &&
        PostgreSqlImportAdmission.Prepare(accepted.Spec, accepted.Inputs) == input &&
        PostgreSqlImportObservation.Decode(evidence) is { } observed &&
        PostgreSqlImportObservation.Verify(input.Pair, observed) == PostgreSqlImportVerificationStatus.Confirmed;

    public bool Validate(DurableOperationNode node, ExternalOperationOutput output)
    {
        if (node.Action != Producer || PostgreSqlImportContracts.Decode(node) is not { } input ||
            new ExternalOperationOutputIdentity(output.Contract, output.Key) != Identity) return false;
        return type == PostgreSqlDatabaseContract.Id
            ? PostgreSqlDatabaseOutputContract.Decode(output) == new PostgreSqlDatabaseOutput(input.Pair.DatabaseName, input.Pair.DatabaseOid, input.Pair.PublicSchemaOid)
            : PostgreSqlApplicationConnectionContract.Decode(output) is { } connection && connection.DatabaseName == input.Pair.DatabaseName &&
                connection.Schema == input.Pair.Schema && connection.UserName == input.Pair.LoginName && connection.Password == PostgreSqlImportContracts.Reference(input);
    }

    public bool ValidateReuse(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
        IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration configuration,
        IReadOnlyCollection<ProviderResourceReservation> reservations)
    {
        if (!Validate(accepted.Node, output) || PostgreSqlImportContracts.Decode(accepted.Node) is not { } input ||
            PostgreSqlImportAdmission.Prepare(accepted.Spec, accepted.Inputs) != input ||
            configuration.ProviderKindId != PostgreSqlProviderConfigurationCodec.Kind || configuration.ShapeId != PostgreSqlProviderConfigurationCodec.Shape ||
            configuration.ImplementationId != Producer.Admission.ImplementationId.Value) return false;
        var configured = codec.DecodeTyped(configuration.PayloadJson);
        if (type == PostgreSqlApplicationLoginContract.Id && PostgreSqlApplicationConnectionContract.Decode(output) !=
            new PostgreSqlApplicationConnection(configured.Host, configured.Port, input.Pair.DatabaseName, input.Pair.Schema, input.Pair.LoginName, PostgreSqlImportContracts.Reference(input))) return false;
        return Reservations(accepted.Inputs, input.Pair).All(reservations.Contains);
    }

    public async ValueTask<bool> ValidateAcceptedAsync(DurableOperationKey key, AcceptedOperation accepted,
        ExternalOperationOutput output, CancellationToken cancellationToken)
    {
        if (!Validate(accepted.Node, output) || PostgreSqlImportContracts.Decode(accepted.Node) is not { } input ||
            PostgreSqlImportAdmission.Prepare(accepted.Spec, accepted.Inputs) != input) return false;
        return await credentials.FindAsync(new(accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId,
            ManifestLocalId.Create(input.Pair.LoginResource)), PostgreSqlImportContracts.Reference(input), cancellationToken) is
        { Provenance: ApplicationCredentialProvenance.Imported };
    }

    internal static IReadOnlyList<ProviderResourceReservation> Reservations(DurablePlanningInputs inputs, PostgreSqlExistingPairInput pair)
    {
        var db = ManifestLocalId.Create(pair.DatabaseResource); var role = ManifestLocalId.Create(pair.LoginResource);
        ProviderResourceReservation R(ManifestLocalId resource, string ns, string value) =>
            new(inputs.Providers.Resource(resource).Instance, inputs.Environment, inputs.Revision.ApplicationRecordId, resource, ns, value);
        return [R(db, "postgresql.database", pair.DatabaseName), R(db, "postgresql.database.identity", pair.DatabaseOid.ToString(CultureInfo.InvariantCulture)),
            R(role, PostgreSqlApplicationLoginAction.RoleReservationNamespace, pair.LoginName), R(role, "postgresql.role.identity", pair.LoginOid.ToString(CultureInfo.InvariantCulture))];
    }
}
