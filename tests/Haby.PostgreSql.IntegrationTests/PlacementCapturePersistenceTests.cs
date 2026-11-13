using System.Data.Common;
using Haby.Application;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Target = Haby.Application.PlacementSelectionTarget;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PlacementRevisionPersistenceTests
{
    [Theory]
    [InlineData("environment")]
    [InlineData("application")]
    [InlineData("component")]
    public async Task Capture_excludes_selection_writers_and_absent_higher_precedence_insertions_until_outer_commit(string kind)
    {
        var (connection, first, second) = await SelectionDatabase();
        var service = SelectionService(connection);
        var baseline = await Select(service, Target.Create("environment"), 1, Absent);
        Assert.Equal(PlacementSelectionOutcome.Created, baseline.Outcome);
        // Component administration checks a genuine imported application revision before saving.
        await using (var arrange = Services(connection))
        {
            var importer = new ImportApplicationManifest(arrange.GetRequiredService<IManifestImportStore>(),
                new ManifestContractCatalog([], [], [], []), TimeProvider.System);
            var envelope = "{\"applicationVersion\":\"1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"assigned\"},\"spec\":{\"components\":{\"api\":{}}}}}";
            Assert.IsType<ImportApplicationManifestResult.Succeeded>(await importer.ExecuteAsync(System.Text.Encoding.UTF8.GetBytes(envelope), "importer"));
        }
        var signal = new PlacementWriterSignal();
        await using var writerServices = Services(connection);
        var writerFactory = new Factory(connection, signal);
        var codec = new KubernetesPlacementCodec();
        var writer = new PlacementSelectionAdministration(new PostgreSqlEnvironmentQuery(writerFactory),
            new PostgreSqlPlacementSelectionScopeQuery(writerFactory, writerServices.GetRequiredService<IManifestRevisionQuery>()), new PostgreSqlPlacementRevisionStore(writerFactory, codec),
            new PostgreSqlPlacementSelectionStore(writerFactory, codec), TimeProvider.System);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var capture = Context(connection);
        await using var transaction = await capture.Database.BeginTransactionAsync(deadline.Token);
        var environment = await capture.Environments.AsNoTracking().SingleAsync(x => x.EnvironmentId == "dev", deadline.Token);
        var captured = await PostgreSqlDurableDeploymentPlanner.SelectPlacementAsync(capture, environment.Id, "cluster", "assigned", "api", null, deadline.Token);
        var originalRevision = await capture.PlacementRevisions.AsNoTracking().SingleAsync(x => x.Id == captured, deadline.Token);
        Assert.Equal(first.Key.Revision, originalRevision.Revision);
        var target = kind switch
        {
            "application" => Target.Create(kind, application: "assigned"),
            "component" => Target.Create(kind, application: "assigned", component: "api"),
            _ => Target.Create(kind)
        };
        Task<PlacementSelectionResult>? operation = null;
        try
        {
            operation = writer.ApplyAsync("dev", "cluster", target, "standard-runtime", 2, kind == "component" ? "1" : null,
                kind == "environment" ? Match(baseline.Selection!) : Absent, "contender", deadline.Token).AsTask();
            await signal.Started.Task.WaitAsync(deadline.Token);
            var ownerPid = ((NpgsqlConnection)capture.Database.GetDbConnection()).ProcessID;
            await using var observer = new NpgsqlConnection(connection);
            await observer.OpenAsync(deadline.Token);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.False(operation.IsCompleted);
                await using var command = new NpgsqlCommand("SELECT @owner = ANY(pg_blocking_pids(@writer))", observer);
                command.Parameters.AddWithValue("owner", ownerPid);
                command.Parameters.AddWithValue("writer", signal.BackendPid);
                if (await command.ExecuteScalarAsync(deadline.Token) is true) break;
            }
            // The capture method has returned, but the transaction still owns the exclusion.
            Assert.Equal(captured, await PostgreSqlDurableDeploymentPlanner.SelectPlacementAsync(capture, environment.Id, "cluster", "assigned", "api", null, deadline.Token));
            await transaction.CommitAsync(deadline.Token);
            Assert.Equal(kind == "environment" ? PlacementSelectionOutcome.Updated : PlacementSelectionOutcome.Created,
                (await operation).Outcome);
        }
        finally
        {
            await transaction.DisposeAsync();
            if (operation is not null) await operation;
        }
        await using var fresh = Context(connection);
        await using var freshTransaction = await fresh.Database.BeginTransactionAsync(deadline.Token);
        var next = await PostgreSqlDurableDeploymentPlanner.SelectPlacementAsync(fresh, environment.Id, "cluster", "assigned", "api", null, deadline.Token);
        Assert.NotEqual(captured, next);
        Assert.Equal(second.Key.Revision, (await fresh.PlacementRevisions.AsNoTracking().SingleAsync(x => x.Id == next, deadline.Token)).Revision);
    }

    private sealed class PlacementWriterSignal : DbCommandInterceptor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int BackendPid { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock_shared", StringComparison.Ordinal))
            {
                BackendPid = ((NpgsqlConnection)command.Connection!).ProcessID;
                Started.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }
}
