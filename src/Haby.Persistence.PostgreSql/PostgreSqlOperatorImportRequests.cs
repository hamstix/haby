using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

public sealed partial class PostgreSqlDurableDeploymentPlanner
{
    async ValueTask<OperatorImportRequestResult> IOperatorImportRequestStore.BindAsync(OperatorImportRequestBinding binding,
        string actor, CancellationToken token)
    {
        try { binding.Validate(); ValidateActor(actor); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return new(OperatorImportRequestStatus.InvalidInput); }
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var previous = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.RequestId, token);
            if (previous is not null) return await ReadBoundRequestAsync(db, previous, binding, token);
            var validation = await ValidateChangeSetInputAsync(db, binding.Environment, binding.Revision.ApplicationRecordId,
                binding.Revision, binding.Input, true, token);
            if (validation is { } refused) return new(ImportRequestStatus(refused));
            var application = Guid.Parse(binding.Revision.ApplicationRecordId.ToString());
            var revision = await db.Revisions.AsNoTracking().Where(x => x.ApplicationRecordId == application &&
                x.LocalRevisionNumber == binding.Revision.RevisionNumber.Value).Select(x => x.Id).SingleAsync(token);
            var row = new OperatorImportRequestRow
            {
                Id = binding.RequestId,
                Environment = Guid.Parse(binding.Environment.ToString()),
                Application = application,
                ManifestRevision = revision,
                Revision = binding.Revision.RevisionNumber.Value,
                CredentialResource = binding.CredentialResource.Value,
                Input = ResourceImportIntentCodec.Encode(binding.Input)!,
                CreatedAt = clock.GetUtcNow(),
                CreatedBy = actor
            };
            db.OperatorImportRequestRecords.Add(row);
            await db.SaveChangesAsync(token);
            var persisted = await db.OperatorImportRequestRecords.AsNoTracking().SingleAsync(x => x.Id == row.Id, token);
            return new(OperatorImportRequestStatus.Pending, ImportRequestView(persisted));
        }
        catch (Exception error) when (ImportRequestStorageFailure(error))
        {
            // Includes a concurrent insert and a committed binding whose response was lost.
            return await RecoverImportRequestAsync(binding, null, false);
        }
    }

    async ValueTask<OperatorImportRequestResult> IOperatorImportRequestStore.CompleteAsync(OperatorImportRequestBinding binding,
        ProtectedCredentialReference credential, string actor, CancellationToken token)
    {
        try { binding.Validate(); ValidateActor(actor); ArgumentNullException.ThrowIfNull(credential); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return new(OperatorImportRequestStatus.InvalidInput); }
        var committing = false;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT request_id FROM operator_resource_import_requests WHERE request_id = {binding.RequestId} FOR UPDATE", token);
            var row = await db.OperatorImportRequestRecords.SingleOrDefaultAsync(x => x.Id == binding.RequestId, token);
            if (row is null) return new(OperatorImportRequestStatus.NotFound);
            if (!SameImportRequest(row, binding)) return new(OperatorImportRequestStatus.Conflict);
            var staged = await ReadRequestCredentialAsync(db, credential.Id, token);
            if (!MatchesRequestCredential(staged, row) || staged!.Secret != Guid.Parse(credential.Reference.SecretId.ToString()) ||
                staged.Version != Guid.Parse(credential.Reference.SecretVersionId.ToString())) return new(OperatorImportRequestStatus.Conflict);
            if (row.ChangeSet is not null)
            {
                var previous = await ReadBoundRequestAsync(db, row, binding, token, credential);
                return previous with
                {
                    Status = previous.Status == OperatorImportRequestStatus.Created
                    ? OperatorImportRequestStatus.Existing : previous.Status
                };
            }
            if (staged.ConfirmedReceipt is not null) return new(OperatorImportRequestStatus.Conflict);
            var intent = binding.Attach(credential);
            var validation = await ValidateChangeSetInputAsync(db, binding.Environment, binding.Revision.ApplicationRecordId,
                binding.Revision, intent, true, token);
            if (validation is { } refused) return new(ImportRequestStatus(refused));
            if (!await db.Revisions.AsNoTracking().AnyAsync(x => x.Id == row.ManifestRevision &&
                x.ApplicationRecordId == row.Application && x.LocalRevisionNumber == row.Revision, token))
                return new(OperatorImportRequestStatus.StorageUnavailable);
            var change = AddChangeSet(db, binding.Environment, binding.Revision.ApplicationRecordId, binding.Revision,
                new DeploymentPrecondition.ExpectedAbsence(), intent, actor);
            row.ChangeSet = Guid.Parse(change.Id.ToString());
            row.StagedCredential = credential.Id;
            row.CompletedAt = change.CreatedAt;
            row.CompletedBy = actor;
            await db.SaveChangesAsync(token);
            var persisted = await db.OperatorImportRequestRecords.AsNoTracking().SingleAsync(x => x.Id == row.Id, token);
            var completed = await ReadBoundRequestAsync(db, persisted, binding, token, credential);
            if (completed.Status != OperatorImportRequestStatus.Created)
                throw new InvalidDataException("Import request completion is unavailable.");
            token.ThrowIfCancellationRequested();
            committing = true;
            await transaction.CommitAsync(CancellationToken.None);
            return completed;
        }
        catch (Exception error) when (ImportRequestStorageFailure(error) || committing && error is OperationCanceledException)
        {
            if (!committing) token.ThrowIfCancellationRequested();
            return await RecoverImportRequestAsync(binding, credential, true);
        }
    }

    async ValueTask<OperatorImportRequestResult> IOperatorImportRequestStore.GetAsync(Guid requestId, CancellationToken token)
    {
        if (requestId == Guid.Empty) return new(OperatorImportRequestStatus.InvalidInput);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var row = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, token);
            return row is null ? new(OperatorImportRequestStatus.NotFound)
                : await ReadBoundRequestAsync(db, row, StoredImportBinding(row), token);
        }
        catch (Exception error) when (ImportRequestStorageFailure(error))
        { return new(OperatorImportRequestStatus.StorageUnavailable); }
    }

    private async ValueTask<OperatorImportRequestResult> RecoverImportRequestAsync(OperatorImportRequestBinding binding,
        ProtectedCredentialReference? credential, bool completion)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
            var row = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.RequestId);
            if (row is null) return new(OperatorImportRequestStatus.StorageUnavailable);
            var result = await ReadBoundRequestAsync(db, row, binding, CancellationToken.None, credential);
            if (!completion || result.Status == OperatorImportRequestStatus.Conflict) return result;
            return result.Status == OperatorImportRequestStatus.Created
                ? result with { Status = OperatorImportRequestStatus.Existing }
                : new(OperatorImportRequestStatus.StorageUnavailable, result.Request);
        }
        catch (Exception error) when (ImportRequestStorageFailure(error))
        { return new(OperatorImportRequestStatus.StorageUnavailable); }
    }

    private static async Task<OperatorImportRequestResult> ReadBoundRequestAsync(ManifestCatalogDbContext db,
        OperatorImportRequestRow row, OperatorImportRequestBinding binding, CancellationToken token,
        ProtectedCredentialReference? expectedCredential = null)
    {
        if (!SameImportRequest(row, binding)) return new(OperatorImportRequestStatus.Conflict);
        StoredImportBinding(row).Validate();
        ValidateActor(row.CreatedBy);
        if (!await db.Revisions.AsNoTracking().AnyAsync(x => x.Id == row.ManifestRevision &&
            x.ApplicationRecordId == row.Application && x.LocalRevisionNumber == row.Revision, token))
            throw new InvalidDataException("Import request revision is unavailable.");
        if (row.ChangeSet is null)
        {
            if (row.StagedCredential is not null || row.CompletedAt is not null || row.CompletedBy is not null)
                throw new InvalidDataException("Import request linkage is incomplete.");
            return new(OperatorImportRequestStatus.Pending, ImportRequestView(row));
        }
        if (row.StagedCredential is not { } stagedId || row.CompletedAt is null || row.CompletedBy is null)
            throw new InvalidDataException("Import request linkage is incomplete.");
        ValidateActor(row.CompletedBy);
        var staged = await ReadRequestCredentialAsync(db, stagedId, token);
        if (!MatchesRequestCredential(staged, row)) throw new InvalidDataException("Import request credential is unavailable.");
        var reference = new ProtectedCredentialReference(stagedId, new(SecretId.Parse(staged!.Secret.ToString()), SecretVersionId.Parse(staged.Version.ToString())));
        if (expectedCredential is not null && expectedCredential != reference) return new(OperatorImportRequestStatus.Conflict);
        var change = await db.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ChangeSet, token);
        if (change is null || change.EnvironmentRecordId != row.Environment || change.ApplicationRecordId != row.Application ||
            change.RevisionNumber != row.Revision || !change.ExpectedAbsence || change.ExpectedGeneration is not null ||
            change.ActorId != row.CompletedBy || change.CreatedAt != row.CompletedAt ||
            change.ImportIntentJson != ResourceImportIntentCodec.Encode(binding.Attach(reference)))
            throw new InvalidDataException("Import request ChangeSet is unavailable.");
        return new(OperatorImportRequestStatus.Created, ImportRequestView(row));
    }

    internal static OperatorImportRequestBinding StoredImportBinding(OperatorImportRequestRow row) => new(row.Id,
        EnvironmentRecordId.Parse(row.Environment.ToString()), ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.Application.ToString()),
            ManifestRevisionNumber.Create(row.Revision)), ManifestLocalId.Create(row.CredentialResource),
        ResourceImportIntentCodec.Decode(row.Input) ?? throw new InvalidDataException("Import request input is unavailable."));

    internal static bool SameImportRequest(OperatorImportRequestRow row, OperatorImportRequestBinding binding) =>
        row.Id == binding.RequestId && row.Environment == Guid.Parse(binding.Environment.ToString()) &&
        row.Application == Guid.Parse(binding.Revision.ApplicationRecordId.ToString()) && row.Revision == binding.Revision.RevisionNumber.Value &&
        row.CredentialResource == binding.CredentialResource.Value && row.Input == ResourceImportIntentCodec.Encode(binding.Input);

    private static OperatorImportRequestView ImportRequestView(OperatorImportRequestRow row) => new(row.Id,
        EnvironmentRecordId.Parse(row.Environment.ToString()), ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.Application.ToString()),
            ManifestRevisionNumber.Create(row.Revision)), ManifestLocalId.Create(row.CredentialResource), row.CreatedAt, row.CreatedBy,
        row.ChangeSet is { } change ? DeploymentChangeSetId.Parse(change.ToString()) : null, row.CompletedAt, row.CompletedBy);

    private static OperatorImportRequestStatus ImportRequestStatus(DurableChangeSetCreateStatus status) => status switch
    {
        DurableChangeSetCreateStatus.EnvironmentNotFound => OperatorImportRequestStatus.EnvironmentNotFound,
        DurableChangeSetCreateStatus.RevisionNotFound => OperatorImportRequestStatus.RevisionNotFound,
        DurableChangeSetCreateStatus.ImportNotSupported => OperatorImportRequestStatus.ImportNotSupported,
        _ => OperatorImportRequestStatus.InvalidInput
    };

    private static bool ImportRequestStorageFailure(Exception error) => error is DbUpdateException or NpgsqlException or IOException or TimeoutException or ArgumentException;

    internal sealed record RequestCredential(Guid Id, Guid Request, Guid Environment, Guid Application, string Resource,
        Guid Secret, Guid Version, Guid? ConfirmedReceipt);
    internal static Task<RequestCredential?> ReadRequestCredentialAsync(ManifestCatalogDbContext db, Guid id, CancellationToken token) =>
        db.ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Id == id).Select(x => new RequestCredential(x.Id, x.Request,
            x.Environment, x.Application, x.Resource, x.Secret, x.Version, x.ConfirmedReceipt)).SingleOrDefaultAsync(token);
    internal static bool MatchesRequestCredential(RequestCredential? credential, OperatorImportRequestRow row) =>
        credential is not null && credential.Request == row.Id && credential.Environment == row.Environment &&
        credential.Application == row.Application && credential.Resource == row.CredentialResource;
}

public static class OperatorImportRequestServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlOperatorImportRequests(this IServiceCollection services)
    {
        services.AddPostgreSqlDurableDeploymentPlanner();
        services.TryAddSingleton<IOperatorImportRequestStore>(provider => provider.GetRequiredService<PostgreSqlDurableDeploymentPlanner>());
        services.TryAddSingleton<IProtectedApplicationCredentialIntake, PostgreSqlProtectedApplicationCredentialIntake>();
        services.TryAddSingleton<OperatorImportRequests>();
        return services;
    }
}
