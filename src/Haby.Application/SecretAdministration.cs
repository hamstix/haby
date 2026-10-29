using Haby.Domain;

namespace Haby.Application;

public sealed record SecretVersionMetadata(SecretVersionId VersionId, int? Number, DateTimeOffset? CreatedAt);
public sealed record SecretMetadata(EnvironmentRecordId Environment, SecretId SecretId, string? Name,
    string Purpose, DateTimeOffset? CreatedAt, IReadOnlyList<SecretVersionMetadata> Versions);
public enum SecretOutcome
{
    Created, ExistingEquivalent, InvalidRequest, EnvironmentNotFound, RequestContentConflict,
    SecretStorageUnavailable, Found, NotFound, Listed, InvalidCursor, InvalidPageSize
}
public sealed record SecretResult(SecretOutcome Outcome, SecretMetadata? Secret = null, Guid? RequestId = null);
public sealed record SecretListResult(SecretOutcome Outcome, IReadOnlyList<SecretMetadata> Items, string? NextCursor = null);

/// <summary>Owns the atomic Secret/version/payload/receipt transaction, including equivalent requests.</summary>
public interface IAdministrativeSecretCatalog
{
    ValueTask<SecretResult> CreateAsync(EnvironmentRecordId environment, string name, Guid requestId,
        SecretMaterial material, string actor, CancellationToken cancellationToken);
    ValueTask<SecretMetadata?> GetAsync(EnvironmentRecordId environment, string? name, SecretId? id, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<SecretMetadata>> ListAsync(EnvironmentRecordId environment, string? after, int limit, CancellationToken cancellationToken);
}

/// <summary>Environment-scoped administrative creation and material-free discovery.</summary>
public sealed class SecretAdministration(IEnvironmentQuery environments, IAdministrativeSecretCatalog catalog)
{
    public const int MaxBytes = 65_536;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async ValueTask<SecretResult> CreateAsync(string? environment, string? name, Guid requestId,
        SecretMaterial material, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var env = Name(environment);
        var secretName = Name(name);
        var bytes = material.CopyBytes();
        try
        {
            if (env is null || secretName is null || requestId == Guid.Empty || bytes.Length is < 1 or > MaxBytes ||
                string.IsNullOrWhiteSpace(actor) || actor.Length > 256)
                return new(SecretOutcome.InvalidRequest, RequestId: requestId);
        }
        finally { Array.Clear(bytes); }
        var record = await environments.FindAsync(env, cancellationToken);
        return record is null ? new(SecretOutcome.EnvironmentNotFound, RequestId: requestId) :
            await catalog.CreateAsync(record, secretName.Value, requestId, material, actor, cancellationToken);
    }

    public async ValueTask<SecretResult> GetAsync(string? environment, string? name, string? id, CancellationToken cancellationToken)
    {
        var env = Name(environment);
        SecretId? identity = null;
        if (env is null || (string.IsNullOrEmpty(name) == string.IsNullOrEmpty(id))) return new(SecretOutcome.InvalidRequest);
        if (!string.IsNullOrEmpty(id))
        {
            try { identity = SecretId.Parse(id); }
            catch (ArgumentException) { return new(SecretOutcome.InvalidRequest); }
            catch (FormatException) { return new(SecretOutcome.InvalidRequest); }
        }
        else if (Name(name) is null) return new(SecretOutcome.InvalidRequest);
        var record = await environments.FindAsync(env, cancellationToken);
        if (record is null) return new(SecretOutcome.EnvironmentNotFound);
        var result = await catalog.GetAsync(record, identity is null ? name : null, identity, cancellationToken);
        return result is null ? new(SecretOutcome.NotFound) : new(SecretOutcome.Found, result);
    }

    public async ValueTask<SecretListResult> ListAsync(string? environment, int size, string? cursor, CancellationToken cancellationToken)
    {
        var env = Name(environment);
        if (env is null) return new(SecretOutcome.InvalidRequest, []);
        if (size == 0) size = DefaultPageSize;
        if (size is < 1 or > MaxPageSize) return new(SecretOutcome.InvalidPageSize, []);
        var record = await environments.FindAsync(env, cancellationToken);
        if (record is null) return new(SecretOutcome.EnvironmentNotFound, []);
        // Secret-specific, versioned, Environment-identity-bound ordinal name keyset; live traversal.
        var prefix = "secret1:" + record + ":";
        string? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (cursor.Length > prefix.Length + 64 || !cursor.StartsWith(prefix, StringComparison.Ordinal) ||
                !ValidPosition(cursor[prefix.Length..])) return new(SecretOutcome.InvalidCursor, []);
            after = cursor[prefix.Length..];
        }
        var rows = await catalog.ListAsync(record, after, size + 1, cancellationToken);
        var items = rows.Take(size).ToArray();
        return new(SecretOutcome.Listed, items, rows.Count > size ? prefix + (items[^1].Name ?? "~" + items[^1].SecretId) : null);
    }

    private static bool ValidPosition(string value) => Name(value) is not null ||
        (value.Length == 37 && value[0] == '~' && Guid.TryParseExact(value[1..], "D", out var id) &&
         id != Guid.Empty && id.Version == 7 && value[20] is ('8' or '9' or 'a' or 'b') && value[1..] == id.ToString());

    private static EnvironmentId? Name(string? name)
    {
        if (name is null) return null;
        try { return EnvironmentId.Create(name); }
        catch (ArgumentException) { return null; }
    }
}
