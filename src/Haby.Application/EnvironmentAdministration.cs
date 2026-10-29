using Haby.Domain;

namespace Haby.Application;

/// <summary>Secret-free registry metadata; the logical name is distinct from persisted identity.</summary>
public sealed record EnvironmentMetadata(EnvironmentRecordId RecordId, EnvironmentId Name);
public sealed record EnvironmentCreation(EnvironmentMetadata Environment, bool Created);

/// <summary>Shared persistence boundary for explicit administration and legacy internal callers.</summary>
public interface IEnvironmentCatalog
{
    ValueTask<EnvironmentCreation> CreateOrGetAsync(EnvironmentId name, CancellationToken cancellationToken);
    ValueTask<EnvironmentMetadata?> GetAsync(EnvironmentId name, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<EnvironmentMetadata>> ListAsync(EnvironmentId? after, int limit, CancellationToken cancellationToken);
}

public enum EnvironmentOutcome { Created, Existing, Found, NotFound, InvalidName, Listed, InvalidCursor, InvalidPageSize }
public sealed record EnvironmentResult(EnvironmentOutcome Outcome, EnvironmentMetadata? Environment = null);
public sealed record EnvironmentListResult(EnvironmentOutcome Outcome, IReadOnlyList<EnvironmentMetadata> Items, string? NextCursor = null);

/// <summary>Explicit Environment administration. Reads never create state.</summary>
public sealed class EnvironmentAdministration(IEnvironmentCatalog catalog)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    private const string CursorPrefix = "env1:";

    public async ValueTask<EnvironmentResult> CreateAsync(string? name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ParseName(name);
        if (id is null) return new(EnvironmentOutcome.InvalidName);
        var result = await catalog.CreateOrGetAsync(id, cancellationToken);
        return new(result.Created ? EnvironmentOutcome.Created : EnvironmentOutcome.Existing, result.Environment);
    }

    public async ValueTask<EnvironmentResult> GetAsync(string? name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ParseName(name);
        if (id is null) return new(EnvironmentOutcome.InvalidName);
        var result = await catalog.GetAsync(id, cancellationToken);
        return result is null ? new(EnvironmentOutcome.NotFound) : new(EnvironmentOutcome.Found, result);
    }

    public async ValueTask<EnvironmentListResult> ListAsync(int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageSize == 0) pageSize = DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize) return new(EnvironmentOutcome.InvalidPageSize, []);
        EnvironmentId? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (cursor.Length > 69 || !cursor.StartsWith(CursorPrefix, StringComparison.Ordinal) ||
                (after = ParseName(cursor[CursorPrefix.Length..])) is null)
                return new(EnvironmentOutcome.InvalidCursor, []);
        }
        var rows = await catalog.ListAsync(after, pageSize + 1, cancellationToken);
        var items = rows.Take(pageSize).ToArray();
        return new(EnvironmentOutcome.Listed, items,
            rows.Count > pageSize ? CursorPrefix + items[^1].Name.Value : null);
    }

    private static EnvironmentId? ParseName(string? name)
    {
        if (name is null) return null;
        try { return EnvironmentId.Create(name); }
        catch (ArgumentException) { return null; }
    }
}
