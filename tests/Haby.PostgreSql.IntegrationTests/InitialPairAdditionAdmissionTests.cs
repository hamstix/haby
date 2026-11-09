using Google.Protobuf;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Initial_pair_with_independent_databases_is_refused_by_Host_without_acceptance_or_external_effects(int extraCount)
    {
        await using var scenario = await ExecutionScenarioAsync();
        var envelope = PostgreSqlPairPlanningTests.Example();
        var resources = envelope["manifest"]!["spec"]!["resources"]!;
        resources["db"]!["properties"]!["name"] = scenario.Database;
        var names = new List<string> { scenario.Database };
        for (var i = 0; i < extraCount; i++)
        {
            var name = "initial_extra_" + Guid.NewGuid().ToString("N"); names.Add(name);
            resources["extra-" + i] = resources["db"]!.DeepClone();
            resources["extra-" + i]!["properties"]!["name"] = name;
        }
        await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var channel = Channel(host, scenario.Certificate); var client = OperatorTestCredentials.Client(channel);
        var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) });
        Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase);
        var change = await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = "pilot", Reference = imported.Created.Revision.Reference,
            Precondition = PlanningPrecondition.ExpectedAbsence
        });
        Assert.Equal(PlanningStatus.Created, change.Status);
        var planned = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(PlanningStatus.PlanInputUnavailable, planned.Status); Assert.Null(planned.Plan);

        await using (var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).Options))
        {
            Assert.Empty(await verify.DurablePlans.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ResourceManagement.AsNoTracking().ToArrayAsync());
        }
        await using var external = new NpgsqlConnection(fixture.ConnectionString); await external.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = ANY($1)", external);
        command.Parameters.AddWithValue(names.ToArray());
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
