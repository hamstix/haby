using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlApplicationCredentialReader(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IApplicationCredentialReader
{
    public async ValueTask<ConfirmedApplicationCredential?> FindAsync(ManagedResourceKey owner, SecretRef reference,
        CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var generated = await PostgreSqlGeneratedCredentialSlots.ReadAsync(db,
            new(owner, GeneratedCredentialSlot.ApplicationPassword), GeneratedCredentialSlot.RandomPasswordV1, cancellationToken, validateMaterial: false);
        if (generated?.Outcome == GeneratedCredentialOutcome.Existing && generated.Reference == reference)
            return new(owner, reference, ApplicationCredentialProvenance.Generated);
        return await ConfirmImportedAsync(db, owner, reference, cancellationToken)
            ? new(owner, reference, ApplicationCredentialProvenance.Imported) : null;
    }

    internal static async Task<bool> ConfirmImportedAsync(ManifestCatalogDbContext db, ManagedResourceKey owner,
        SecretRef reference, CancellationToken token)
    {
        var environment = Guid.Parse(owner.EnvironmentRecordId.ToString());
        var application = Guid.Parse(owner.ApplicationRecordId.ToString());
        var secret = Guid.Parse(reference.SecretId.ToString());
        var version = Guid.Parse(reference.SecretVersionId.ToString());
        var receiptId = await db.ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Environment == environment &&
            x.Application == application && x.Resource == owner.ResourceLocalId.Value && x.Secret == secret && x.Version == version)
            .Select(x => x.ConfirmedReceipt).SingleOrDefaultAsync(token);
        if (receiptId is null) return false;
        var receipt = await db.ResourceImportReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receiptId, token);
        if (receipt is null) return false;
        var document = ResourceImportReceiptIntegrity.Decode(receipt.Document);
        return await ResourceImportReceiptIntegrity.ReadAsync(db, new(ApplicationDeploymentId.Parse(receipt.Deployment.ToString()),
            DeploymentGeneration.Create(receipt.Generation), OperationId.Create(document.Operations[0].Operation)), token) is not null &&
            await db.SecretPayloads.AsNoTracking().AnyAsync(x => x.SecretVersionId == version && x.Bytes.Length > 0 && x.Bytes.Length <= 65536, token);
    }
}
