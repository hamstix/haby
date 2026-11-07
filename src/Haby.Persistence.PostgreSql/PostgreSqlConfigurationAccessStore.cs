using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlConfigurationAccessStore(IDbContextFactory<ManifestCatalogDbContext> contexts,
    IConfigurationDocumentStore documents, TimeProvider clock) : IConfigurationAccessStore, IConfigurationDeliveryDocumentStore
{
    public async ValueTask<ConfigurationAccessCreated?> CreateAsync(ConfigurationDocumentSelection selection, string actor, DateTimeOffset? expiresAt, CancellationToken token)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        var now = UtcPrecision(clock.GetUtcNow());
        if (expiresAt is { } expiry && (expiry.Offset != TimeSpan.Zero || expiry <= now)) throw new ArgumentException("Expiry must be a future UTC instant.");
        if (expiresAt is { } exactExpiry) expiresAt = UtcPrecision(exactExpiry);
        if (expiresAt <= now) throw new ArgumentException("Expiry must be a future UTC instant.");
        var selected = await documents.SelectAsync(selection, token);
        if (selected.Status != ConfigurationDocumentStatus.Found || selected.Target is not { } target) return null;
        var artifact = (await documents.ReadAsync(target, token)).Revision;
        if (artifact is null) return null;
        var id = Guid.CreateVersion7();
        var credential = ConfigurationDeliveryCredential.Create(id);
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = new ConfigurationAccessRow
        {
            Id = id,
            RevisionId = Guid.Parse(artifact.Metadata.Id.ToString()),
            Scope = ScopeCodec.Encode(artifact.Metadata),
            CredentialHash = ConfigurationDeliveryCredential.Hash(credential),
            Actor = actor,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };
        db.ConfigurationAccess.Add(row);
        await db.SaveChangesAsync(token);
        return new(Metadata(row), credential);
    }

    public async ValueTask<ConfigurationAccessGrant?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.ConfigurationAccess.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return row is null ? null : Metadata(row);
    }

    public async ValueTask<ConfigurationAccessGrant?> RevokeAsync(Guid id, string actor, CancellationToken token)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = UtcPrecision(clock.GetUtcNow());
        await db.ConfigurationAccess.Where(x => x.Id == id && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedBy, actor), token);
        return await GetAsync(id, token);
    }

    public async ValueTask<ConfigurationAccessGrant?> AuthorizeAsync(string credential, ConfigurationDocumentRevisionId revision, CancellationToken token)
    {
        if (!ConfigurationDeliveryCredential.TryIdentify(credential, out var id)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        // This query touches only the grant table; do not join the document before authorization.
        var row = await db.ConfigurationAccess.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (row is null || !ConfigurationDeliveryCredential.Verify(credential, row.CredentialHash) || row.RevokedAt is not null ||
            row.ExpiresAt <= clock.GetUtcNow() || row.RevisionId.ToString() != revision.ToString()) return null;
        return Metadata(row);
    }

    public async ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(ConfigurationAccessGrant grant, CancellationToken token)
    {
        try { return await ReadDeliveryAsync(grant, token); }
        catch (System.Data.Common.DbException) { throw new ConfigurationDeliveryStorageException(); }
    }

    private async ValueTask<ConfigurationDeliveryArtifact?> ReadDeliveryAsync(ConfigurationAccessGrant grant, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var id = Guid.Parse(grant.Document.Id.ToString());
        var row = await db.ConfigurationDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (row is null) return null;
        var revision = ConfigurationDocumentCodec.Decode(row.Payload, row.Fingerprint);
        if (revision.Metadata != grant.Document || row.DeploymentId.ToString() != revision.Metadata.Target.Deployment.ToString() ||
            row.Generation != revision.Metadata.Target.Generation.Value || row.Component != revision.Metadata.Target.Component.Value ||
            row.Document != revision.Metadata.Target.Document.Value) throw new InvalidDataException();
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == row.DeploymentId, token);
        if (owner.ApplicationRecordId.ToString() != revision.Metadata.Revision.ApplicationRecordId.ToString()) throw new InvalidDataException();
        return new(revision, EnvironmentRecordId.Parse(owner.EnvironmentRecordId.ToString()));
    }

    private static ConfigurationAccessGrant Metadata(ConfigurationAccessRow row)
    {
        var scope = ScopeCodec.Decode(row.Scope);
        if (scope.Id.ToString() != row.RevisionId.ToString()) throw new InvalidDataException();
        return new(row.Id, scope, row.Actor, row.CreatedAt, row.ExpiresAt, row.RevokedBy, row.RevokedAt);
    }
    private static DateTimeOffset UtcPrecision(DateTimeOffset instant) => new(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
}

// A closed metadata-only snapshot, deliberately independent from the protected artifact payload.
internal sealed record ConfigurationAccessScope(string Id, string Deployment, long Generation, string Component, string Document,
    string Environment, string Application, string ApplicationRecord, long Revision, string Plan, string FileName, string Format,
    string Serializer, string Fingerprint, string FrozenFingerprint, string Actor, DateTimeOffset CreatedAt);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ConfigurationAccessScope))]
internal partial class ConfigurationAccessJsonContext : JsonSerializerContext;

internal static class ScopeCodec
{
    public static string Encode(ConfigurationDocumentMetadata m) => JsonSerializer.Serialize(new ConfigurationAccessScope(m.Id.ToString(), m.Target.Deployment.ToString(),
        m.Target.Generation.Value, m.Target.Component.Value, m.Target.Document.Value, m.Environment.Value, m.Application.Value,
        m.Revision.ApplicationRecordId.ToString(), m.Revision.RevisionNumber.Value, m.Plan.ToString(), m.FileName, m.Format, m.Serializer,
        m.Fingerprint, m.FrozenInputFingerprint, m.Actor, m.CreatedAt), ConfigurationAccessJsonContext.Default.ConfigurationAccessScope);
    public static ConfigurationDocumentMetadata Decode(string json)
    {
        var m = JsonSerializer.Deserialize(json, ConfigurationAccessJsonContext.Default.ConfigurationAccessScope) ?? throw new InvalidDataException();
        return new(ConfigurationDocumentRevisionId.Parse(m.Id), new(ApplicationDeploymentId.Parse(m.Deployment), DeploymentGeneration.Create(m.Generation),
            ManifestLocalId.Create(m.Component), ManifestLocalId.Create(m.Document)), EnvironmentId.Create(m.Environment), Haby.Domain.ApplicationId.Create(m.Application),
            ManifestRevisionRef.Create(ApplicationRecordId.Parse(m.ApplicationRecord), ManifestRevisionNumber.Create(m.Revision)), DeploymentPlanId.Parse(m.Plan),
            m.FileName, m.Format, m.Serializer, m.Fingerprint, m.FrozenFingerprint, m.Actor, m.CreatedAt);
    }
}
