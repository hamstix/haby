using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Private durable input in the explicitly selected plaintext catalog backend.
/// No public endpoint, password digest, normalization, implicit replacement or lost-response cleanup.</summary>
public sealed class PostgreSqlProtectedApplicationCredentialIntake(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IProtectedApplicationCredentialIntake, IProtectedApplicationCredentialCandidateIntake
{
    public ValueTask<ProtectedCredentialStageResult> StageAsync(Guid requestId, ManagedResourceKey owner,
        SecretMaterial material, CancellationToken cancellationToken) => StageCoreAsync(requestId, owner, material, null, cancellationToken);

    public ValueTask<ProtectedCredentialStageResult> StageCandidateAsync(OperatorImportRequestBinding binding,
        SecretMaterial material, CancellationToken token)
    {
        try { binding.Validate(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return ValueTask.FromResult(new ProtectedCredentialStageResult(ProtectedCredentialStageStatus.InvalidInput)); }
        return binding.Correction is null ? ValueTask.FromResult(new ProtectedCredentialStageResult(ProtectedCredentialStageStatus.InvalidInput))
            : StageCoreAsync(binding.RequestId, binding.CredentialOwner, material, binding, token);
    }

    private async ValueTask<ProtectedCredentialStageResult> StageCoreAsync(Guid requestId, ManagedResourceKey owner,
        SecretMaterial material, OperatorImportRequestBinding? candidate, CancellationToken cancellationToken)
    {
        var bytes = material.CopyBytes();
        var environment = Guid.Parse(owner.EnvironmentRecordId.ToString());
        var application = Guid.Parse(owner.ApplicationRecordId.ToString());
        var committing = false;
        try
        {
            if (requestId == Guid.Empty || !Valid(bytes)) return new(ProtectedCredentialStageStatus.InvalidInput);
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ImportMutationGate.LockAsync(db, environment, application, cancellationToken);
            if (!await db.Environments.AnyAsync(x => x.Id == environment, cancellationToken) ||
                !await db.Applications.AnyAsync(x => x.Id == application, cancellationToken))
                return new(ProtectedCredentialStageStatus.InvalidInput);
            var binding = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken);
            if (await db.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == requestId, cancellationToken) ||
                candidate is not null && (binding is null || !PostgreSqlDurableDeploymentPlanner.SameImportRequest(binding, candidate)))
                return new(ProtectedCredentialStageStatus.Conflict);
            var previous = await ReadAsync(db, environment, requestId, owner, bytes, candidate, cancellationToken);
            if (previous is not null) return previous;
            if (candidate is not null)
            {
                if (!await ImportCorrectionRules.EligibleAsync(db, candidate, cancellationToken)) return new(ProtectedCredentialStageStatus.Conflict);
            }
            else if (binding?.PreviousRequest is not null || await db.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x =>
                x.Environment == environment && x.Application == application && x.Resource == owner.ResourceLocalId.Value, cancellationToken))
                return new(ProtectedCredentialStageStatus.Conflict);
            var row = new ProtectedApplicationCredentialRow
            {
                Id = Guid.CreateVersion7(),
                Request = requestId,
                Environment = environment,
                Application = application,
                Resource = owner.ResourceLocalId.Value,
                Secret = Guid.CreateVersion7(),
                Version = Guid.CreateVersion7(),
                Material = bytes
            };
            db.ProtectedApplicationCredentials.Add(row);
            if (candidate is null) db.ProtectedCredentialOwners.Add(new()
            { Environment = environment, Application = application, Resource = owner.ResourceLocalId.Value, Stage = row.Id, Request = requestId });
            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            committing = true;
            await transaction.CommitAsync(CancellationToken.None);
            return new(ProtectedCredentialStageStatus.Staged, Reference(row));
        }
        catch (Exception error) when (error is DbUpdateException or NpgsqlException or IOException || committing && error is OperationCanceledException)
        {
            try
            {
                await using var reread = await contexts.CreateDbContextAsync(CancellationToken.None);
                return await ReadAsync(reread, environment, requestId, owner, bytes, candidate, CancellationToken.None) ??
                    new(ProtectedCredentialStageStatus.StorageUnavailable);
            }
            catch (Exception unavailable) when (unavailable is DbUpdateException or NpgsqlException or IOException or TimeoutException)
            { return new(ProtectedCredentialStageStatus.StorageUnavailable); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool Valid(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 65536) return false;
        try
        {
            var chars = new UTF8Encoding(false, true).GetChars(bytes);
            try { return !chars.Contains('\0'); }
            finally { Array.Clear(chars); }
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static async Task<ProtectedCredentialStageResult?> ReadAsync(ManifestCatalogDbContext db, Guid environment,
        Guid request, ManagedResourceKey owner, byte[] bytes, OperatorImportRequestBinding? candidate, CancellationToken token)
    {
        var application = Guid.Parse(owner.ApplicationRecordId.ToString());
        var row = await db.ProtectedApplicationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Request == request, token);
        if (row is null) return null;
        try
        {
            if (await db.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == request, token)) return new(ProtectedCredentialStageStatus.Conflict);
            if (candidate is null)
            {
                if (!await db.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x => x.Stage == row.Id && x.Request == request, token))
                    return new(ProtectedCredentialStageStatus.Conflict);
            }
            else
            {
                var binding = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request, token);
                if (binding is null || !PostgreSqlDurableDeploymentPlanner.SameImportRequest(binding, candidate)) return new(ProtectedCredentialStageStatus.Conflict);
            }
            return row.Request == request && row.Environment == environment && row.Application == application && row.Resource == owner.ResourceLocalId.Value &&
                CryptographicOperations.FixedTimeEquals(row.Material, bytes)
                ? new(ProtectedCredentialStageStatus.Existing, Reference(row)) : new(ProtectedCredentialStageStatus.Conflict);
        }
        finally { CryptographicOperations.ZeroMemory(row.Material); }
    }

    public async ValueTask<SecretMaterial?> ResolveStagedAsync(ProtectedCredentialReference reference,
        ManagedResourceKey owner, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var environment = Guid.Parse(owner.EnvironmentRecordId.ToString());
        var application = Guid.Parse(owner.ApplicationRecordId.ToString());
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ImportMutationGate.LockAsync(db, environment, application, cancellationToken);
        if (!await db.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x => x.Stage == reference.Id &&
            x.Environment == environment && x.Application == application && x.Resource == owner.ResourceLocalId.Value, cancellationToken)) return null;
        var row = await db.ProtectedApplicationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reference.Id &&
            x.Environment == environment && x.Application == application && x.Resource == owner.ResourceLocalId.Value, cancellationToken);
        if (row is null) return null;
        try { return Reference(row) == reference && Valid(row.Material) ? new(row.Material) : null; }
        finally { CryptographicOperations.ZeroMemory(row.Material); }
    }

    internal static ProtectedCredentialReference Reference(ProtectedApplicationCredentialRow row) =>
        new(row.Id, new(SecretId.Parse(row.Secret.ToString()), SecretVersionId.Parse(row.Version.ToString())));
}
