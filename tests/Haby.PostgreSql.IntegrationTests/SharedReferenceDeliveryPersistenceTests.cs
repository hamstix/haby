using System.Data.Common;
using Haby.Application;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class SharedReferenceDeliveryTests
{
    private sealed class ProvenanceWrites : DbCommandInterceptor
    {
        internal int Writes;
        private void Observe(DbCommand command)
        {
            if (command.CommandText.Contains("configuration_shared_credential_uses", StringComparison.Ordinal) &&
                !command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Writes++;
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result)
        { Observe(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Observe(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData data, InterceptionResult<int> result)
        { Observe(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Observe(command); return ValueTask.FromResult(result); }
    }
    private static ManifestCatalogDbContext ProvenanceContext(Scenario scenario, ProvenanceWrites? writes = null)
    {
        var options = new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection);
        if (writes is not null) options.AddInterceptors(writes);
        return new(options.Options);
    }

    [Fact]
    public async Task Created_document_shared_provenance_refuses_tracked_and_projected_bulk_history_mutations_before_SQL()
    {
        var scenario = await Database();
        await Materialize(scenario);
        foreach (var asynchronous in new[] { false, true })
            foreach (var delete in new[] { false, true })
                foreach (var bulk in new[] { false, true })
                {
                    var writes = new ProvenanceWrites();
                    await using var act = ProvenanceContext(scenario, writes);
                    if (bulk)
                    {
                        if (asynchronous)
                            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                            {
                                if (delete) await act.ConfigurationSharedUses.ExecuteDeleteAsync();
                                else await act.ConfigurationSharedUses.Select(x => new { History = x }).ExecuteUpdateAsync(x => x.SetProperty(r => r.History.Formatter, "changed"));
                            });
                        else Assert.Throws<InvalidOperationException>(() =>
                        {
                            if (delete) act.ConfigurationSharedUses.ExecuteDelete();
                            else act.ConfigurationSharedUses.Select(x => new { History = x }).ExecuteUpdate(x => x.SetProperty(r => r.History.Formatter, "changed"));
                        });
                    }
                    else
                    {
                        var row = await act.ConfigurationSharedUses.OrderBy(x => x.Document).FirstAsync();
                        if (delete) act.ConfigurationSharedUses.Remove(row); else row.Formatter = "changed";
                        if (asynchronous) await Assert.ThrowsAsync<InvalidOperationException>(async () => await act.SaveChangesAsync());
                        else Assert.Throws<InvalidOperationException>(() => act.SaveChanges());
                    }
                    Assert.Equal(0, writes.Writes);
                }
        await using var verify = ProvenanceContext(scenario);
        Assert.Equal(2, await verify.ConfigurationSharedUses.CountAsync());
        Assert.All(await verify.ConfigurationSharedUses.AsNoTracking().ToArrayAsync(), x => Assert.Equal(FormatterId.Value, x.Formatter));
    }

    [Fact]
    public async Task Shared_provenance_cannot_be_backfilled_or_inserted_with_a_missing_or_expired_acceptance_transaction()
    {
        var scenario = await Database();
        await Materialize(scenario);
        foreach (var mode in new[] { "unscoped", "backfill", "expired", "changed" })
        {
            var writes = new ProvenanceWrites();
            await using var act = ProvenanceContext(scenario, writes);
            var row = await act.ConfigurationSharedUses.AsNoTracking().OrderBy(x => x.Document).FirstAsync();
            if (mode != "unscoped")
            {
                await using (var transaction = await act.Database.BeginTransactionAsync())
                {
                    Assert.True(await act.AcquireDocumentUseGatesAsync(row.Environment, [row.Provider], default));
                    act.AuthorizeDocumentSharedUse(row);
                    if (mode == "expired") await transaction.CommitAsync();
                    else
                    {
                        if (mode == "changed") row.Projection = "/other";
                        act.ConfigurationSharedUses.Add(row);
                        await Assert.ThrowsAsync<InvalidOperationException>(async () => await act.SaveChangesAsync());
                    }
                }
                if (mode != "expired") { Assert.Equal(0, writes.Writes); continue; }
                await act.Database.BeginTransactionAsync();
            }
            act.ConfigurationSharedUses.Add(row);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await act.SaveChangesAsync());
            Assert.Equal(0, writes.Writes);
        }
        await using var verify = ProvenanceContext(scenario);
        Assert.Equal(2, await verify.ConfigurationSharedUses.CountAsync());
    }

    [Fact]
    public async Task D_additive_migration_matches_the_model_and_Up_Down_add_no_stored_server_code()
    {
        var scenario = await Database();
        await using var db = ProvenanceContext(scenario);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261123201454_SharedCredentialAuthorityTransactions";
        const string current = "20261123233005_SharedReferenceConfigurationDelivery";
        foreach (var sql in new[] { migrator.GenerateScript(previous, current), migrator.GenerateScript(current, previous) })
            foreach (var forbidden in new[] { "CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", "CREATE TRIGGER", "CREATE PROCEDURE", "DO $" })
                Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        await migrator.MigrateAsync(previous);
        Assert.DoesNotContain(current, await db.Database.GetAppliedMigrationsAsync());
        await migrator.MigrateAsync(current);
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.ConfigurationSharedUses.AsNoTracking().ToArrayAsync());
    }
}
