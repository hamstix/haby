using System.Data;
using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Friend-only metadata authorization. This is neither authentication nor delivery.</summary>
internal sealed class InternalRuntimePublication(IDbContextFactory<ManifestCatalogDbContext> contexts,
    InternalWorkloadConfiguration configuration, InternalDurableActions identities, TimeProvider clock,
    Action<RuntimePublicationReceipt>? afterCommit = null)
{
    internal static void ValidateBinding(RuntimeIdentityBindingRow row)
    {
        if (row.ConcurrencyToken == Guid.Empty || (row.Status switch
        {
            "Prepared" => row.RevokedAt is not null || row.RevokedBy is not null,
            "Revoked" => row.RevokedAt is null || string.IsNullOrWhiteSpace(row.RevokedBy) || row.RevokedBy.Length > 256 || row.RevokedAt.Value.Offset != TimeSpan.Zero,
            _ => true
        })) throw new InvalidDataException("Invalid runtime binding lifecycle.");
    }
    internal static RuntimeBindingState BindingState(RuntimeIdentityBindingRow row)
    {
        ValidateBinding(row);
        return new(row.Id, row.Status, row.ConcurrencyToken, row.RevokedBy, row.RevokedAt);
    }
    private static RuntimePermissionState? PermissionState(RuntimeConfigurationPermissionRow? row)
    {
        if (row is null) return null;
        if (row.Status is not ("Published" or "Revoked") || row.ConcurrencyToken == Guid.Empty || row.Id == Guid.Empty)
            throw new InvalidDataException("Invalid runtime permission lifecycle.");
        return new(row.Id, row.BindingId, row.RevisionId, row.Status, row.ConcurrencyToken, row.CreatedBy, row.CreatedAt,
            row.ChangedBy, row.ChangedAt, row.LastRequest);
    }

    internal async Task<RuntimePublicationRequest[]> PreparationRequestsAsync(DurableChangeSetCommit accepted, string actor, CancellationToken token)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var source = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
        var recipients = await configuration.RequireRecipientsAsync(db, source, token);
        var result = new List<RuntimePublicationRequest>();
        foreach (var recipient in recipients.OrderBy(x => x.Workload.Component.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Workload.Id.Value, StringComparer.Ordinal).ThenBy(x => x.Document.Value, StringComparer.Ordinal))
        {
            var managed = await db.ManagedWorkloads.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
                x.Component == recipient.Workload.Component.Value && x.Workload == recipient.Workload.Id.Value, token)
                ?? throw new InvalidDataException("Confirmed managed identity unavailable.");
            var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == managed.Id, token)
                ?? throw new InvalidDataException("Confirmed runtime binding unavailable.");
            ValidateBinding(binding);
            await identities.RequireIdentityOriginAsync(db, binding, token);
            var origin = await OriginAsync(db, source, managed, recipient, binding, token);
            // The request is stable even after a revoke: never choose a fresh token on resume.
            var request = new RuntimePublicationRequest(InternalRuntimePublicationPreparation.Key(origin, Guid.Parse(recipient.Revision.ToString())),
                RuntimePublicationOperation.PublishPermission, binding.Id, Guid.Parse(recipient.Revision.ToString()), null, actor, origin);
            if (binding.Status == "Revoked") throw new RuntimePublicationBlockedException(request);
            result.Add(request);
        }
        return result.ToArray();
    }

    private static async Task<RuntimePublicationOrigin> OriginAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        ManagedWorkloadRow managed, WorkloadConfigurationRecipient recipient, RuntimeIdentityBindingRow binding, CancellationToken token)
    {
        if (binding.EnvironmentId != source.EnvironmentRecordId || managed.EnvironmentId != source.EnvironmentRecordId ||
            managed.ApplicationId != source.ApplicationRecordId || managed.ProviderId != binding.ProviderId)
            throw new InvalidDataException("Runtime publication owner mismatch.");
        var inventory = await db.OwnedObjects.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == managed.Id && x.Role == binding.Role, token);
        var proof = await DurableAggregateCompletion.ReadAsync(db, source, token, true)
            ?? throw new InvalidDataException("Runtime publication checkpoint unavailable.");
        return new(source.DeploymentId, source.Generation, source.PlanId, source.ChangeSetId, source.AcceptedAt, source.ActorId,
            source.EnvironmentRecordId, source.ApplicationRecordId, managed.Id, recipient.Workload.Component.Value,
            recipient.Workload.Id.Value, recipient.Document.Value, source.Fingerprint, proof.Id, inventory.Generation, inventory.OperationId);
    }

    private async Task RequireNewPublicationAsync(ManifestCatalogDbContext db, RuntimePublicationRequest request,
        RuntimeIdentityBindingRow binding, ManagedWorkloadRow managed, CancellationToken token)
    {
        var origin = request.Origin!;
        var source = await InternalGenerationAuthority.RequireAsync(db, origin.Accepted, token);
        var recipients = await configuration.RequireRecipientsAsync(db, source, token); // ALL subjects, inside the acceptance transaction.
        foreach (var subject in recipients.Select(x => x.Workload).Distinct())
        {
            var owner = await db.ManagedWorkloads.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
                x.Component == subject.Component.Value && x.Workload == subject.Id.Value, token)
                ?? throw new InvalidDataException("Complete managed identity preparation required.");
            var identity = await db.RuntimeIdentityBindings.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == owner.Id, token)
                ?? throw new InvalidDataException("Complete runtime identity preparation required.");
            ValidateBinding(identity);
            await identities.RequireIdentityOriginAsync(db, identity, token);
        }
        var exact = recipients.SingleOrDefault(x => x.Workload.Component.Value == origin.Component && x.Workload.Id.Value == origin.Workload &&
            x.Document.Value == origin.Document && x.Revision.ToString() == request.Revision!.Value.ToString())
            ?? throw new InvalidDataException("Exact runtime publication recipient unavailable.");
        if (managed.DeploymentId != source.DeploymentId || managed.Component != origin.Component || managed.Workload != origin.Workload ||
            origin != await OriginAsync(db, source, managed, exact, binding, token))
            throw new InvalidDataException("Exact runtime publication origin mismatch.");
    }

    internal async Task<RuntimePublicationResult> ApplyAsync(RuntimePublicationRequest request, CancellationToken token)
    {
        RuntimePublicationCodec.Validate(request);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        // An absent request has no row to lock. This localized PostgreSQL lock serializes
        // request identity first, including same-key/different-target races.
        var gate = "haby-runtime-publication/v1/" + request.Key;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({gate}, 0))", token);
        var saved = await db.RuntimePublicationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == request.Key, token);
        RuntimePublicationReceipt? original = saved is null ? null : ReadReceipt(saved);
        if (original is not null && original.Request != request) throw new InvalidDataException("Conflicting runtime publication request.");
        var deployment = await (from b in db.RuntimeIdentityBindings.AsNoTracking()
                                join w in db.ManagedWorkloads.AsNoTracking() on b.ManagedWorkloadId equals w.Id
                                where b.Id == request.Binding
                                select w.DeploymentId).SingleOrDefaultAsync(token);
        if (deployment == Guid.Empty) throw new InvalidDataException("Runtime binding unavailable.");
        await DurableAggregateCompletion.LockAsync(db, deployment, token);
        var binding = await db.RuntimeIdentityBindings.SingleAsync(x => x.Id == request.Binding, token);
        var managed = await db.ManagedWorkloads.AsNoTracking().SingleAsync(x => x.Id == binding.ManagedWorkloadId, token);
        if (managed.DeploymentId != deployment) throw new InvalidDataException("Runtime binding owner mismatch.");
        var bindingState = BindingState(binding);
        var permission = request.Revision is { } revision ? await db.RuntimeConfigurationPermissions.SingleOrDefaultAsync(x =>
            x.BindingId == binding.Id && x.RevisionId == revision, token) : null;
        var before = PermissionState(permission);
        if (original is not null) return new(original, bindingState, before); // No fresh acceptance, precondition or mutation.

        if (request.Operation == RuntimePublicationOperation.PublishPermission)
            await RequireNewPublicationAsync(db, request, binding, managed, token);
        else if (request.Revision is { } revokedRevision && !await db.ConfigurationDocuments.AsNoTracking().AnyAsync(x =>
            x.Id == revokedRevision && x.DeploymentId == deployment && x.Component == managed.Component, token))
            throw new InvalidDataException("Runtime permission target unavailable.");

        var at = clock.GetUtcNow(); at = new(at.UtcTicks - at.UtcTicks % 10, TimeSpan.Zero);
        var expected = request.Operation == RuntimePublicationOperation.RevokeBinding ? binding.ConcurrencyToken : permission?.ConcurrencyToken;
        RuntimePublicationOutcome outcome;
        if (request.ExpectedToken != expected) outcome = RuntimePublicationOutcome.PreconditionFailed;
        else if (request.Operation == RuntimePublicationOperation.PublishPermission && binding.Status == "Revoked") outcome = RuntimePublicationOutcome.BindingRevoked;
        else if (request.Operation == RuntimePublicationOperation.RevokeBinding)
        {
            outcome = binding.Status == "Revoked" ? RuntimePublicationOutcome.Unchanged : RuntimePublicationOutcome.Revoked;
            if (outcome == RuntimePublicationOutcome.Revoked)
            {
                binding.Status = "Revoked"; binding.ConcurrencyToken = Guid.CreateVersion7(); binding.RevokedAt = at; binding.RevokedBy = request.Actor;
            }
        }
        else
        {
            var desired = request.Operation == RuntimePublicationOperation.PublishPermission ? "Published" : "Revoked";
            if (permission?.Status == desired || permission is null && desired == "Revoked") outcome = RuntimePublicationOutcome.Unchanged;
            else
            {
                outcome = desired == "Published" ? RuntimePublicationOutcome.Published : RuntimePublicationOutcome.Revoked;
                if (permission is null)
                {
                    permission = new()
                    {
                        Id = Guid.CreateVersion7(),
                        BindingId = binding.Id,
                        RevisionId = request.Revision!.Value,
                        Status = desired,
                        OriginPayload = RuntimePublicationCodec.Encode(request.Origin!),
                        CreatedBy = request.Actor,
                        CreatedAt = at,
                        ChangedBy = request.Actor,
                        ChangedAt = at,
                        LastRequest = request.Key
                    };
                    db.RuntimeConfigurationPermissions.Add(permission);
                }
                permission.Status = desired; permission.ConcurrencyToken = Guid.CreateVersion7();
                permission.ChangedBy = request.Actor; permission.ChangedAt = at; permission.LastRequest = request.Key;
            }
        }
        var receipt = new RuntimePublicationReceipt(Guid.CreateVersion7(), request, outcome, at, before, PermissionState(permission), BindingState(binding));
        db.RuntimePublicationReceipts.Add(new()
        {
            RequestKey = request.Key,
            Id = receipt.Id,
            BindingId = request.Binding,
            RevisionId = request.Revision,
            Payload = RuntimePublicationCodec.Encode(receipt)
        });
        await db.SaveChangesAsync(token);
        token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None);
        afterCommit?.Invoke(receipt);
        return new(receipt, BindingState(binding), PermissionState(permission));
    }

    internal async Task<RuntimePublicationResult?> ReadAsync(string requestKey, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var row = await db.RuntimePublicationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == requestKey, token);
        if (row is null) return null;
        var receipt = ReadReceipt(row);
        var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.Id == row.BindingId, token);
        var permission = await db.RuntimeConfigurationPermissions.AsNoTracking().SingleOrDefaultAsync(x => x.BindingId == row.BindingId && x.RevisionId == row.RevisionId, token);
        return new(receipt, BindingState(binding), PermissionState(permission));
    }

    private static RuntimePublicationReceipt ReadReceipt(RuntimePublicationReceiptRow row)
    {
        var receipt = RuntimePublicationCodec.Receipt(row.Payload);
        if (receipt.Id != row.Id || receipt.Request.Key != row.RequestKey || receipt.Request.Binding != row.BindingId || receipt.Request.Revision != row.RevisionId)
            throw new InvalidDataException("Runtime publication receipt integrity mismatch.");
        return receipt;
    }

    internal async Task<RuntimeMetadataAuthority?> AuthorizeMetadataAsync(Guid bindingId, Guid revisionId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == bindingId, token);
        if (binding is null) return null;
        ValidateBinding(binding);
        if (binding.Status != "Prepared") return null;
        var permission = await db.RuntimeConfigurationPermissions.AsNoTracking().SingleOrDefaultAsync(x => x.BindingId == bindingId && x.RevisionId == revisionId, token);
        if (PermissionState(permission)?.Status != "Published") return null;
        var origin = RuntimePublicationCodec.Origin(permission!.OriginPayload);
        var managed = await db.ManagedWorkloads.AsNoTracking().SingleAsync(x => x.Id == binding.ManagedWorkloadId, token);
        var document = await db.ConfigurationDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId, token);
        var original = await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == permission.LastRequest, token);
        var receipt = ReadReceipt(original);
        if (document is null || managed.Id != origin.ManagedWorkload || binding.EnvironmentId != origin.Environment || managed.EnvironmentId != origin.Environment ||
            managed.ApplicationId != origin.Application || managed.DeploymentId != origin.Deployment || managed.Component != origin.Component || managed.Workload != origin.Workload ||
            document.DeploymentId != origin.Deployment || document.Generation != origin.Generation || document.Component != origin.Component || document.Document != origin.Document ||
            receipt.After != PermissionState(permission) || receipt.Request.Binding != bindingId || receipt.Request.Revision != revisionId ||
            receipt.Request.Origin != origin || receipt.Outcome != RuntimePublicationOutcome.Published)
            throw new InvalidDataException("Runtime permission authority mismatch.");
        return new(bindingId, permission.Id, revisionId, origin.Environment, origin.Application, origin.Deployment, origin.Generation, origin.Component, origin.Workload, origin.Document);
    }
}
