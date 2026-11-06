using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ConfigurationProfilePersistenceTests
{
    private static readonly ProfileAssignmentTarget EnvironmentTarget = new ProfileAssignmentTarget.Environment();
    private static ProfileAssignmentAdministration AssignmentAdmin(IServiceProvider services, DateTimeOffset? time = null) => new(
        services.GetRequiredService<IEnvironmentCatalog>(), services.GetRequiredService<IProfileAssignmentScopeQuery>(),
        services.GetRequiredService<IConfigurationProfileRevisionQuery>(), services.GetRequiredService<IProfileAssignmentStore>(), new Clock(time ?? Time));
    private static byte[] AssignmentBytes(ProfileAssignmentTarget target, params ProfileDecision[] decisions)
    {
        var t = target switch
        {
            ProfileAssignmentTarget.Environment => new JsonObject { ["kind"] = "environment" },
            ProfileAssignmentTarget.Application a => new JsonObject { ["kind"] = "application", ["applicationId"] = a.ApplicationId.Value },
            ProfileAssignmentTarget.Component c => new JsonObject { ["kind"] = "component", ["applicationId"] = c.ApplicationId.Value, ["componentRef"] = c.ComponentRef.Value },
            _ => throw new ArgumentException()
        };
        return Bytes(new JsonObject
        {
            ["apiVersion"] = "haby.dev/v1alpha1",
            ["kind"] = "ConfigurationProfileAssignment",
            ["spec"] = new JsonObject
            {
                ["target"] = t,
                ["decisions"] = new JsonArray(decisions.Select(d => (JsonNode)(d is ProfileDecision.Use use
                ? new JsonObject { ["kind"] = "use", ["profileRef"] = new JsonObject { ["profileId"] = d.ProfileId.Value, ["revision"] = use.Reference.Revision } }
                : new JsonObject { ["kind"] = "disable", ["profileId"] = d.ProfileId.Value })).ToArray())
            }
        });
    }
    private async Task<(string Connection, EnvironmentMetadata Environment)> AssignmentDatabase()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var env = await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("production"), default);
        return (connection, env.Environment);
    }
    private static ProfileDecision Disable(string id = "absent") => new ProfileDecision.Disable(ProfileId.Create(id));
    private static AssignmentPrecondition Match(PersistedProfileAssignment state) => new AssignmentPrecondition.ExpectedConcurrencyToken(state.Token);

    [Fact]
    public async Task Assignment_preconditions_precede_equality_empty_replacement_tokens_audit_and_scope_isolation()
    {
        var (connection, environment) = await AssignmentDatabase();
        PersistedProfileAssignment first;
        await using (var services = Services(connection))
        {
            var admin = AssignmentAdmin(services);
            Assert.Equal(AssignmentOutcome.NotFound, (await admin.GetAsync("production", EnvironmentTarget)).Outcome);
            Assert.Equal(AssignmentOutcome.EnvironmentNotFound, (await admin.GetAsync("missing", EnvironmentTarget)).Outcome);
            var payload = AssignmentBytes(EnvironmentTarget);
            Assert.Equal(AssignmentOutcome.Conflict, (await admin.ApplyAsync(payload, "production", null,
                new AssignmentPrecondition.ExpectedConcurrencyToken(AssignmentToken.New()), "first")).Outcome);
            var created = await admin.ApplyAsync(payload, "production", null, new AssignmentPrecondition.ExpectedAbsence(), "first");
            Assert.Equal(AssignmentOutcome.Created, created.Outcome);
            first = created.Assignment!;
            Assert.Empty(first.Declaration.Decisions);
            Assert.Equal(AssignmentOutcome.Conflict, (await admin.ApplyAsync(payload, "production", null, new AssignmentPrecondition.ExpectedAbsence(), "other")).Outcome);
        }
        await using (var services = Services(connection))
        {
            var admin = AssignmentAdmin(services, Time.AddDays(1));
            Assert.Equal(first, (await admin.GetAsync("production", EnvironmentTarget)).Assignment);
            var unchanged = await admin.ApplyAsync(AssignmentBytes(EnvironmentTarget), "production", null, Match(first), "later");
            Assert.Equal(AssignmentOutcome.Unchanged, unchanged.Outcome);
            Assert.Equal(first, unchanged.Assignment);
            var updated = await admin.ApplyAsync(AssignmentBytes(EnvironmentTarget, Disable(), Disable("other")), "production", null, Match(first), "later");
            Assert.Equal(AssignmentOutcome.Updated, updated.Outcome);
            Assert.NotEqual(first.Token, updated.Assignment!.Token);
            Assert.Equal("later", updated.Assignment.Audit.TechnicalActorId);
            Assert.Equal(Time.AddDays(1), updated.Assignment.Audit.ChangedAtUtc);
            Assert.Equal(AssignmentOutcome.Conflict, (await admin.ApplyAsync(AssignmentBytes(EnvironmentTarget, Disable("other"), Disable()), "production", null, Match(first), "stale")).Outcome);
            var equivalent = await admin.ApplyAsync(AssignmentBytes(EnvironmentTarget, Disable("other"), Disable()), "production", null, Match(updated.Assignment), "unused");
            Assert.Equal(AssignmentOutcome.Unchanged, equivalent.Outcome);
            Assert.Equal(updated.Assignment, equivalent.Assignment);
            var reset = await admin.ApplyAsync(AssignmentBytes(EnvironmentTarget), "production", null, Match(updated.Assignment), "reset");
            Assert.Equal(AssignmentOutcome.Updated, reset.Outcome);
            Assert.NotEqual(first.Token, reset.Assignment!.Token);
            Assert.NotEqual(updated.Assignment.Token, reset.Assignment.Token);
            await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("independent"), default);
            Assert.Equal(AssignmentOutcome.Created, (await admin.ApplyAsync(AssignmentBytes(EnvironmentTarget), "independent", null, new AssignmentPrecondition.ExpectedAbsence(), "independent")).Outcome);
        }
        await using var check = Context(connection);
        Assert.Equal(2, await check.ProfileAssignments.CountAsync());
        Assert.Empty(await check.DurablePlans.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.Secrets.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.OperationOutputs.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Synchronized_assignment_CAS_has_one_complete_winner_and_conflict_losers(bool update, bool equal)
    {
        var (connection, environment) = await AssignmentDatabase();
        PersistedProfileAssignment? original = null;
        if (update)
        {
            var store = new PostgreSqlProfileAssignmentStore(new Factory(connection));
            original = (await store.ApplyAsync(environment, new(EnvironmentTarget, [Disable("before")]), new AssignmentPrecondition.ExpectedAbsence(), new("before", Time, null), default)).Assignment!;
        }
        var barrier = new CandidateBarrier(6);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            var store = new PostgreSqlProfileAssignmentStore(new Factory(connection, barrier));
            return await store.ApplyAsync(environment, new(EnvironmentTarget, equal ? original!.Declaration.Decisions : [Disable("choice-" + i)]),
                original is null ? new AssignmentPrecondition.ExpectedAbsence() : Match(original), new("actor-" + i, Time.AddMinutes(i), null), default);
        }));
        // Matching equal-content operations all preserve the same token/audit and may all succeed.
        if (equal) Assert.All(results, x => { Assert.Equal(AssignmentOutcome.Unchanged, x.Outcome); Assert.Equal(original, x.Assignment); });
        else
        {
            var winner = Assert.Single(results, x => x.Outcome == (update ? AssignmentOutcome.Updated : AssignmentOutcome.Created));
            Assert.Equal(5, results.Count(x => x.Outcome == AssignmentOutcome.Conflict));
            var actor = winner.Assignment!.Audit.TechnicalActorId;
            Assert.Equal("choice-" + actor[6..], Assert.Single(winner.Assignment.Declaration.Decisions).ProfileId.Value);
            var fresh = new PostgreSqlProfileAssignmentStore(new Factory(connection));
            Assert.Equal(winner.Assignment, await fresh.FindAsync(environment, EnvironmentTarget, default));
        }
        await using var check = Context(connection);
        Assert.Single(await check.ProfileAssignments.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Unchanged_assignment_conflicts_when_changed_writer_commits_after_its_read()
    {
        var (connection, environment) = await AssignmentDatabase();
        var writer = new PostgreSqlProfileAssignmentStore(new Factory(connection));
        var original = (await writer.ApplyAsync(environment, new(EnvironmentTarget, [Disable("before")]),
            new AssignmentPrecondition.ExpectedAbsence(), new("original", Time, null), default)).Assignment!;
        var pause = new AssignmentSavePause();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var unchangedStore = new PostgreSqlProfileAssignmentStore(new Factory(connection, pause));
        var unchanged = unchangedStore.ApplyAsync(environment, original.Declaration, Match(original),
            new("must-not-rewrite-audit", Time.AddDays(2), null), timeout.Token).AsTask();
        AssignmentResult changed;
        var replacement = new ConfigurationProfileAssignment(EnvironmentTarget, [Disable("winner"), Disable("another")]);
        var audit = new AssignmentAudit("changed-writer", Time.AddDays(1), null);
        try
        {
            // The equal contender has read the original row but has not executed its guarded UPDATE.
            await pause.Entered.Task.WaitAsync(timeout.Token);
            changed = await writer.ApplyAsync(environment, replacement, Match(original), audit, timeout.Token);
        }
        finally { pause.Resume.TrySetResult(); }
        var equalResult = await unchanged.WaitAsync(timeout.Token);
        Assert.Equal(AssignmentOutcome.Updated, changed.Outcome);
        Assert.Equal(AssignmentOutcome.Conflict, equalResult.Outcome);
        Assert.Null(equalResult.Assignment);
        Assert.NotEqual(original.Token, changed.Assignment!.Token);
        Assert.Equal(replacement, changed.Assignment.Declaration);
        Assert.Equal(audit, changed.Assignment.Audit);
        var independent = new PostgreSqlProfileAssignmentStore(new Factory(connection));
        Assert.Equal(changed.Assignment, await independent.FindAsync(environment, EnvironmentTarget, default));
        await using var check = Context(connection);
        Assert.Single(await check.ProfileAssignments.AsNoTracking().ToArrayAsync());
    }

    private sealed class AssignmentSavePause : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    [Theory]
    [InlineData("cancel-before-save", false)]
    [InlineData("cancel-after-save", false)]
    [InlineData("fail-after-save", false)]
    [InlineData("cancel-commit-start", true)]
    [InlineData("cancel-after-commit", true)]
    [InlineData("lost-response", true)]
    public async Task Assignment_commit_boundary_rolls_back_or_retains_confirmed_outcome(string mode, bool durable)
    {
        var (connection, environment) = await AssignmentDatabase();
        using var cancellation = new CancellationTokenSource();
        var store = new PostgreSqlProfileAssignmentStore(new Factory(connection, new SaveFault(mode, cancellation), new CommitFault(mode, cancellation)));
        var operation = store.ApplyAsync(environment, new(EnvironmentTarget, [Disable()]), new AssignmentPrecondition.ExpectedAbsence(), new("original", Time.AddTicks(123), null), cancellation.Token).AsTask();
        if (mode.StartsWith("cancel-", StringComparison.Ordinal) && !durable) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else if (mode is "fail-after-save" or "lost-response") await Assert.ThrowsAsync<IOException>(() => operation);
        else Assert.Equal(AssignmentOutcome.Created, (await operation).Outcome);
        var fresh = new PostgreSqlProfileAssignmentStore(new Factory(connection));
        var state = await fresh.FindAsync(environment, EnvironmentTarget, default);
        Assert.Equal(durable, state is not null);
        if (state is not null)
        {
            Assert.Equal(Time.AddTicks(120), state.Audit.ChangedAtUtc);
            Assert.Equal("original", state.Audit.TechnicalActorId);
            // A lost response is not a receipt; a blind repeat cannot report original success.
            Assert.Equal(AssignmentOutcome.Conflict, (await fresh.ApplyAsync(environment, state.Declaration,
                new AssignmentPrecondition.ExpectedAbsence(), new("retry", Time.AddYears(1), null), default)).Outcome);
            Assert.Equal(state, await fresh.FindAsync(environment, EnvironmentTarget, default));
        }
    }

    [Fact]
    public async Task Assignment_additive_upgrade_from_current_main_preserves_data_and_model()
    {
        var connection = await Database(false);
        await using (var arrange = Context(connection))
        {
            await arrange.GetService<IMigrator>().MigrateAsync("20261105151707_PersistConfigurationProfileRevisions");
            arrange.Environments.Add(new() { Id = Guid.CreateVersion7(), EnvironmentId = "retained" });
            await arrange.SaveChangesAsync();
        }
        await using (var services = Services(connection)) await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await using var check = Context(connection);
        Assert.Equal("retained", (await check.Environments.AsNoTracking().SingleAsync()).EnvironmentId);
        Assert.Empty(await check.ProfileAssignments.AsNoTracking().ToArrayAsync());
        Assert.False(check.Database.HasPendingModelChanges());
        Assert.Equal(check.Database.GetMigrations(), await check.Database.GetAppliedMigrationsAsync());
    }
}
