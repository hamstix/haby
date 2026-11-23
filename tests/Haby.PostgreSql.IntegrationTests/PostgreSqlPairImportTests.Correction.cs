using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.RegularExpressions;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Fact]
    public async Task Correction_migration_preserves_confirmed_import_history_and_seeds_current_authority()
    {
        var pair = await ExistingPair();
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable, operatorRequest: true);
        string receipt;
        string inputs;
        Guid[] management;
        await using (var execute = Services(setup.Connection))
        {
            Assert.Null((await execute.GetRequiredService<DurableGenerationRunner>().RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default)).StopOutcome);
            await using var db = await execute.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            receipt = (await db.ResourceObservationReceipts.AsNoTracking().SingleAsync()).Document;
            inputs = (await db.DeploymentGenerations.AsNoTracking().SingleAsync()).ProviderInputsJson;
            management = await db.ResourceManagement.AsNoTracking().OrderBy(x => x.Resource).Select(x => x.Id).ToArrayAsync();
        }
        // ADR-033 requires a fresh catalog across the observation-model reset boundary.
        // Verify current-build restart and immutable correction authority instead of
        // downgrading confirmed history into a superseded alpha schema.
        await using (var up = Services(setup.Connection))
            await up.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await using var restart = Services(setup.Connection);
        await using (var db = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(receipt, (await db.ResourceObservationReceipts.AsNoTracking().SingleAsync()).Document);
            Assert.Equal(inputs, (await db.DeploymentGenerations.AsNoTracking().SingleAsync()).ProviderInputsJson);
            Assert.Equal(management, await db.CurrentResourceManagement.AsNoTracking().OrderBy(x => x.Resource).Select(x => x.Id).ToArrayAsync());
            Assert.Equal(setup.Staged.Id, (await db.ProtectedCredentialOwners.AsNoTracking().SingleAsync()).Stage);
            Assert.Equal(1, (await db.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        Assert.NotNull(await restart.GetRequiredService<IResourceObservationAcceptance>().FindAsync(setup.Key, default));
        Assert.NotNull(await restart.GetRequiredService<IApplicationCredentialReader>().FindAsync(setup.Owner, setup.Staged.Reference, default));
        await DeliveredConnection(restart, setup, pair);
    }

    private static async Task AssertCurrentCorrectionSchemaStable(ServiceProvider services)
    {
        // Cross-alpha downgrades are outside ADR-033. Initialization of the current
        // model must preserve live correction state and the complete current schema.
        string[] applied;
        await using (var before = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            applied = (await before.Database.GetAppliedMigrationsAsync()).ToArray();
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await using var after = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(applied, (await after.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Contains(applied, x => x.EndsWith("_ResourceObservationKernel", StringComparison.Ordinal));
        Assert.True(await after.OperatorImportRequestRecords.AnyAsync(x => x.PreviousRequest != null));
    }

    [Fact]
    public async Task Failed_unconfirmed_import_corrects_to_next_generation_without_rewriting_history_or_external_state()
    {
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var before = await Snapshot(pair);
        await VerificationLogging();
        var rejected = Guid.NewGuid().ToString("N");
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable, password: rejected, operatorRequest: true);
        string originalInputs;
        string originalGraph;
        await using (var fail = Services(setup.Connection))
        {
            Assert.Equal(DurableExecutionOutcome.Failed, (await fail.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default)).StopOutcome);
            await using var db = await fail.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            var original = await db.DeploymentGenerations.AsNoTracking().SingleAsync();
            originalInputs = original.ProviderInputsJson; originalGraph = original.GraphJson;
        }
        await AssertNoUsableCredential(setup);
        OperatorImportRequestBinding binding;
        DeploymentChangeSetId change;
        DurableChangeSetCommit corrected;
        ProtectedCredentialReference candidate;
        DurablePlan correctionPlan;
        await using (var create = Services(setup.Connection))
        {
            var prepared = await create.GetRequiredService<PrepareResourceImport>().ExecuteAsync(new(Guid.NewGuid(), setup.Owner.EnvironmentRecordId,
                setup.Plan.Inputs.Revision, PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema))), default);
            Assert.Equal(ResourceImportPreparationStatus.Prepared, prepared.Status);
            binding = prepared.Binding! with { Correction = new(setup.Request, setup.Commit.DeploymentId, setup.Commit.Generation,
                OperatorImportCorrectionReason.CredentialRejected) };
            using var material = Material(pair.Password);
            var result = await create.GetRequiredService<OperatorImportRequests>().CreateAsync(binding, material, "correcting-operator", default);
            Assert.Equal(OperatorImportRequestStatus.Created, result.Status);
            await AssertCurrentCorrectionSchemaStable(create);
            change = result.Request!.ChangeSetId!;
            candidate = (await create.GetRequiredService<IDurableDeploymentPlanner>().FindChangeSetAsync(change, default))!
                .Import!.Resources.Single(x => x.Resource == setup.Owner.ResourceLocalId).Credential!;
            using var unavailable = await create.GetRequiredService<IProtectedApplicationCredentialIntake>().ResolveStagedAsync(candidate, setup.Owner, default);
            Assert.Null(unavailable);
            Assert.Equal(OperatorImportRequestStatus.Created, (await create.GetRequiredService<OperatorImportRequests>().GetAsync(setup.Request, default)).Status);
            var planner = create.GetRequiredService<IDurableDeploymentPlanner>();
            var planned = await planner.CreatePlanAsync(change, "planning-operator", default);
            Assert.Equal(DurablePlanStatus.Planned, planned.Status);
            correctionPlan = planned.Plan!;
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(planned.Plan!.Id, default));
            corrected = Assert.IsType<DurableCommitResult.Committed>(await create.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(change, planned.Plan.Id, "committing-operator", default)).Commit;
            Assert.Equal(setup.Commit.DeploymentId, corrected.DeploymentId);
            Assert.Equal(2, corrected.Generation.Value);
        }
        await using (var restart = Services(setup.Connection))
        {
            await AssertCurrentCorrectionSchemaStable(restart);
            var service = restart.GetRequiredService<OperatorImportRequests>();
            var old = await service.GetAsync(setup.Request, default);
            Assert.Equal(OperatorImportRequestStatus.Superseded, old.Status);
            Assert.Equal(binding.RequestId, old.Request!.SupersededByRequestId);
            Assert.Equal("original-operator", old.Request.CreatedBy);
            using var oldUnavailable = await restart.GetRequiredService<IProtectedApplicationCredentialIntake>().ResolveStagedAsync(setup.Staged, setup.Owner, default);
            Assert.Null(oldUnavailable);
            using var material = Material(pair.Password);
            Assert.Equal(OperatorImportRequestStatus.Existing, (await service.CreateAsync(binding, material, "retry-operator", default)).Status);
            Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(corrected.DeploymentId, corrected.Generation, default)).StopOutcome);
            var history = await restart.GetRequiredService<IDurableOperationJournal>().ProgressAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default);
            Assert.NotNull(history);
            Assert.Null(history.ResourcesReady);
            Assert.Equal(2, history.DesiredGeneration);
            Assert.Equal(DurableExecutionOutcome.InputUnavailable, (await restart.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default)).StopOutcome);
            using var oldConsumer = await restart.GetRequiredService<ISecretStore>().ResolveAsync(
                new(new SecretOwner.Resource(setup.Owner), SecretPurpose.ApplicationLogin), setup.Staged.Reference, default);
            Assert.Null(oldConsumer);
            Assert.Null(await restart.GetRequiredService<IApplicationCredentialReader>().FindAsync(setup.Owner, setup.Staged.Reference, default));
            Assert.NotNull(await restart.GetRequiredService<IApplicationCredentialReader>().FindAsync(setup.Owner, candidate.Reference, default));
            await DeliveredConnection(restart, new(setup.Connection, corrected, correctionPlan, setup.Owner, candidate, binding.RequestId), pair);
            await using var db = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Equal(2, await db.DeploymentGenerations.CountAsync());
            Assert.Equal(4, await db.ResourceManagement.CountAsync());
            Assert.Equal(2, await db.CurrentResourceManagement.CountAsync());
            Assert.All(await db.CurrentResourceManagement.ToArrayAsync(), x => Assert.Equal(2, x.SourceGeneration));
            var original = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == 1);
            Assert.Equal(originalInputs, original.ProviderInputsJson); Assert.Equal(originalGraph, original.GraphJson);
            Assert.Null((await db.ProtectedApplicationCredentials.AsNoTracking().SingleAsync(x => x.Id == setup.Staged.Id)).ConfirmedReceipt);
            Assert.NotNull((await db.ProtectedApplicationCredentials.AsNoTracking().SingleAsync(x => x.Id == candidate.Id)).ConfirmedReceipt);
            Assert.Equal(2, (await db.ResourceObservationReceipts.AsNoTracking().SingleAsync()).Generation);
            var audit = await db.ImportSupersessions.AsNoTracking().SingleAsync();
            Assert.Equal("committing-operator", audit.Actor);
            Assert.Equal(nameof(OperatorImportCorrectionReason.CredentialRejected), audit.Reason);
            Assert.Equal(2, (await db.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        Assert.True(before == await Snapshot(pair), "Correction changed existing external identities, owners, ACLs or data.");
        Assert.True(await PasswordUnchanged(passwordGuard, pair), "Correction changed the existing external password verifier.");
        var logs = await fixture.ReadServerLogsAsync();
        Assert.False(logs.Contains(pair.Password, StringComparison.Ordinal) || logs.Contains(rejected, StringComparison.Ordinal), "Credential material appeared in server logs.");
        var verification = logs.Split('\n').Where(x => x.StartsWith(PostgreSqlImportVerifier.ApplicationName + "|" + pair.Database + "|", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(verification);
        Assert.False(Regex.IsMatch(string.Join('\n', verification), @"(?:statement:|execute[^:]*:)\s*(?:CREATE|ALTER|GRANT|REVOKE|DROP|TRUNCATE|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase),
            "Correction verification dispatched an external mutation command.");
        for (var attack = 0; attack < 3; attack++)
        {
            await using var guard = Services(setup.Connection);
            await using var db = await guard.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            if (attack == 0) (await db.ImportSupersessions.SingleAsync()).Actor = "must-not-rewrite-history";
            else if (attack == 1) (await db.ProtectedCredentialOwners.SingleAsync()).Request = Guid.NewGuid();
            else (await db.ResourceManagementHeads.FirstAsync()).Management = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }
}
