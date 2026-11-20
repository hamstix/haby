using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.PostgreSql;

/// <summary>Reobserves the original pinned pair without rewriting its historical receipt.</summary>
public sealed class PostgreSqlImportReuseVerifier(ResourceTypeId type, PostgreSqlImportVerifier verifier,
    IPostgreSqlImportFaultHooks hooks) : IDurableResourceReuseVerifier
{
    public DurableActionAdmissionKey Key => (type == PostgreSqlDatabaseContract.Id
        ? PostgreSqlImportContracts.Database : PostgreSqlImportContracts.Login).Admission;

    public async ValueTask<ResourceReuseValidationStatus> VerifyAsync(AcceptedOperation original, ResourceImportReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (original.Node.Action.Admission != Key || PostgreSqlImportObservation.Decode(receipt.Evidence) is not { } previous)
            return ResourceReuseValidationStatus.InputUnavailable;
        var result = await verifier.VerifyAsync(original, cancellationToken);
        if (result.Status != PostgreSqlImportVerificationStatus.Confirmed || result.Observation is not { } current)
            return result.Status switch
            {
                PostgreSqlImportVerificationStatus.Conflict => ResourceReuseValidationStatus.Conflict,
                PostgreSqlImportVerificationStatus.Connectivity => ResourceReuseValidationStatus.Connectivity,
                _ => ResourceReuseValidationStatus.InputUnavailable
            };
        // VerifyAsync checks identities, safe role attributes, exact credential and effective privileges.
        // Ownership is accepted evidence too. Irrelevant ACL representation/grant changes are allowed.
        if (previous.SchemaOwner != current.SchemaOwner || previous.SchemaOwnerOid != current.SchemaOwnerOid ||
            previous.PublicSchemaOwner != current.PublicSchemaOwner || previous.PublicSchemaOwnerOid != current.PublicSchemaOwnerOid)
            return ResourceReuseValidationStatus.Conflict;
        await hooks.AfterReuseVerificationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ResourceReuseValidationStatus.Confirmed;
    }
}
