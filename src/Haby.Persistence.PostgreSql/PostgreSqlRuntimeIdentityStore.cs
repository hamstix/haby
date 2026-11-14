using System.Data;
using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Read-only identity metadata. Does not resolve documents, permissions or secret material.</summary>
internal sealed class PostgreSqlRuntimeIdentityStore(IDbContextFactory<ManifestCatalogDbContext> contexts,
    InternalDurableActions origins) : IRuntimeIdentityStore
{
    public async ValueTask<RuntimeTrust?> ReadAsync(Guid binding, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var row = await db.RuntimeIdentityBindings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding, token);
        if (row is null) return null;
        InternalRuntimePublication.ValidateBinding(row);
        await origins.RequireAuthenticationIdentityOriginAsync(db, row, token);
        var owner = await db.ManagedWorkloads.AsNoTracking().SingleAsync(x => x.Id == row.ManagedWorkloadId, token);
        var inventory = await db.OwnedObjects.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == owner.Id && x.Role == row.Role, token);
        var provider = await db.ProviderInstances.AsNoTracking().SingleAsync(x => x.Id == row.ProviderId, token);
        var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleAsync(x => x.ProviderInstanceId == row.ProviderId && x.RevisionNumber == row.ConfigurationRevision, token);
        if (owner.ProviderId != row.ProviderId || owner.EnvironmentId != row.EnvironmentId || provider.EnvironmentRecordId != row.EnvironmentId ||
            revision.EnvironmentRecordId != row.EnvironmentId || !revision.IsSealed)
            throw new InvalidDataException("Runtime identity owner mismatch.");
        return new(row.Id, row.InstallationId, row.EnvironmentId, row.ManagedWorkloadId, row.ProviderId, row.ConfigurationRevision,
            revision.ImplementationId, provider.ProviderKindId, revision.ConfigurationShapeId, revision.PayloadFormatVersion,
            revision.PayloadJson, row.Authenticator, row.Audience, row.Contract, row.Format, row.ValidationPayload,
            inventory.Contract, inventory.Format, inventory.Payload, row.Status == "Revoked");
    }
    public async ValueTask<bool> IsCurrentAsync(RuntimeTrust expected, CancellationToken token)
    {
        var current = await ReadAsync(expected.Binding, token);
        return current is not null && !current.Revoked && current == expected;
    }
}
