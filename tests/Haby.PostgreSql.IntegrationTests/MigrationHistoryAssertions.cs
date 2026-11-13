using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

internal static class MigrationHistoryAssertions
{
    internal static async Task<string[]> CurrentAsync(ManifestCatalogDbContext db, params string[] removedNames)
    {
        var expected = db.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
        await EqualAsync(db, expected);
        var removed = removedNames.Select(name => Assert.Single(expected, id => id.EndsWith("_" + name, StringComparison.Ordinal))).ToArray();
        Assert.Equal(removedNames.Length, removed.Distinct(StringComparer.Ordinal).Count());
        return expected.Except(removed, StringComparer.Ordinal).ToArray();
    }

    internal static async Task EqualAsync(ManifestCatalogDbContext db, string[] expected)
        => Assert.Equal(expected.Order(StringComparer.Ordinal), (await db.Database.GetAppliedMigrationsAsync()).Order(StringComparer.Ordinal));
}
