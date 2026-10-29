using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class EnvironmentAdministrationTests
{
    private sealed class Catalog : IEnvironmentCatalog
    {
        internal readonly SortedDictionary<string, EnvironmentMetadata> Rows = new(StringComparer.Ordinal);
        internal int Calls;
        internal int LastLimit;
        public ValueTask<EnvironmentCreation> CreateOrGetAsync(EnvironmentId name, CancellationToken cancellationToken)
        {
            Calls++;
            var created = !Rows.ContainsKey(name.Value);
            if (created) Rows.Add(name.Value, new(EnvironmentRecordId.New(), name));
            return ValueTask.FromResult(new EnvironmentCreation(Rows[name.Value], created));
        }
        public ValueTask<EnvironmentMetadata?> GetAsync(EnvironmentId name, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(Rows.GetValueOrDefault(name.Value));
        }
        public ValueTask<IReadOnlyList<EnvironmentMetadata>> ListAsync(EnvironmentId? after, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            LastLimit = limit;
            return ValueTask.FromResult<IReadOnlyList<EnvironmentMetadata>>(Rows.Values
                .Where(x => after is null || string.CompareOrdinal(x.Name.Value, after.Value) > 0).Take(limit).ToArray());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Production")]
    [InlineData("1dev")]
    [InlineData("dev--one")]
    [InlineData("dev-")]
    [InlineData("dev_one")]
    [InlineData(" dev")]
    [InlineData("dev\n")]
    [InlineData("dév")]
    public async Task Invalid_names_are_typed_and_never_reach_storage(string? name)
    {
        var catalog = new Catalog();
        var useCases = new EnvironmentAdministration(catalog);
        Assert.Equal(EnvironmentOutcome.InvalidName, (await useCases.CreateAsync(name, default)).Outcome);
        Assert.Equal(EnvironmentOutcome.InvalidName, (await useCases.GetAsync(name, default)).Outcome);
        Assert.Equal(0, catalog.Calls);
    }

    [Fact]
    public async Task Name_length_boundary_and_retry_preserve_separate_identity()
    {
        var catalog = new Catalog();
        var useCases = new EnvironmentAdministration(catalog);
        var name = new string('a', 64);
        var created = await useCases.CreateAsync(name, default);
        Assert.Equal(EnvironmentOutcome.Created, created.Outcome);
        Assert.Equal(name, created.Environment!.Name.Value);
        Assert.NotEqual(name, created.Environment.RecordId.ToString());
        Assert.Equal(7, Guid.Parse(created.Environment.RecordId.ToString()).Version);
        var retry = await useCases.CreateAsync(name, default);
        Assert.Equal(EnvironmentOutcome.Existing, retry.Outcome);
        Assert.Equal(created.Environment, retry.Environment);
        Assert.Equal(created.Environment, (await useCases.GetAsync(name, default)).Environment);
        Assert.Equal(EnvironmentOutcome.InvalidName, (await useCases.CreateAsync(name + "a", default)).Outcome);
        Assert.Equal(EnvironmentOutcome.InvalidName, (await useCases.GetAsync(name + "a", default)).Outcome);
        Assert.Equal(EnvironmentOutcome.NotFound, (await useCases.GetAsync("missing", default)).Outcome);
        Assert.Single(catalog.Rows);
    }

    [Theory]
    [InlineData("env2:dev")]
    [InlineData("env1:")]
    [InlineData("env1:DEV")]
    [InlineData("env1:dev\n")]
    [InlineData(" env1:dev")]
    [InlineData("dev")]
    public async Task Invalid_cursors_fail_before_storage(string cursor)
    {
        var catalog = new Catalog();
        Assert.Equal(EnvironmentOutcome.InvalidCursor, (await new EnvironmentAdministration(catalog).ListAsync(1, cursor, default)).Outcome);
        Assert.Equal(0, catalog.Calls);
    }

    [Fact]
    public async Task Pages_are_bounded_ordered_and_never_hide_continuation()
    {
        var catalog = new Catalog();
        var useCases = new EnvironmentAdministration(catalog);
        foreach (var name in new[] { "b", "a0", "a-1", "a", "z" }) await useCases.CreateAsync(name, default);
        var first = await useCases.ListAsync(2, null, default);
        Assert.Equal(["a", "a-1"], first.Items.Select(x => x.Name.Value));
        Assert.Equal("env1:a-1", first.NextCursor);
        Assert.Equal(3, catalog.LastLimit);
        var second = await useCases.ListAsync(2, first.NextCursor, default);
        Assert.Equal(["a0", "b"], second.Items.Select(x => x.Name.Value));
        Assert.Equal("env1:b", second.NextCursor);
        var last = await useCases.ListAsync(1, second.NextCursor, default);
        Assert.Equal("z", Assert.Single(last.Items).Name.Value);
        Assert.Null(last.NextCursor);
        var empty = await useCases.ListAsync(100, "env1:z", default);
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);
        Assert.Equal(101, catalog.LastLimit);
        var all = await useCases.ListAsync(0, "", default);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(51, catalog.LastLimit);
        Assert.Null(all.NextCursor);
        Assert.Equal("b", Assert.Single((await useCases.ListAsync(1, "env1:az", default)).Items).Name.Value);
    }

    [Fact]
    public async Task Invalid_page_sizes_and_oversized_cursor_never_reach_storage()
    {
        var catalog = new Catalog();
        var useCases = new EnvironmentAdministration(catalog);
        foreach (var size in new[] { -1, 101, int.MaxValue })
            Assert.Equal(EnvironmentOutcome.InvalidPageSize, (await useCases.ListAsync(size, null, default)).Outcome);
        Assert.Equal(EnvironmentOutcome.InvalidCursor, (await useCases.ListAsync(1, "env1:" + new string('a', 65), default)).Outcome);
        Assert.Equal(0, catalog.Calls);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await useCases.CreateAsync("valid", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await useCases.GetAsync("valid", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await useCases.ListAsync(0, null, canceled.Token));
        Assert.Equal(0, catalog.Calls);
    }
}
