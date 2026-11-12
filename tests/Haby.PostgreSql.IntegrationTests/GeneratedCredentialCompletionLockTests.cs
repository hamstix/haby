using System.Data.Common;
using Haby.Application;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    private sealed class TransactionMaterialReads : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader reader, CancellationToken token = default)
        {
            if (command.Transaction is not null && command.CommandText.Contains("secret_payloads", StringComparison.Ordinal) &&
                Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetName(i) == "bytes")) Count++;
            return ValueTask.FromResult(reader);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generated_completion_and_final_checkpoint_never_select_secret_material_inside_write_transaction(bool producerCompletion)
    {
        var scenario = await GraphCase(new QueueBoundary { Generated = true });
        await using var services = Services(scenario.Connection, new QueueBoundary { Generated = true });
        var producer = MaterialKey(scenario.Commit);
        var trap = new TransactionMaterialReads();
        var factory = new OutputFactory(scenario.Connection, trap);
        var journal = new PostgreSqlDurableOperationJournal(factory, []);
        if (producerCompletion)
        {
            var outputs = services.GetRequiredService<IGeneratedCredentialOperations>();
            var input = (await outputs.FindInputAsync(producer, default))!;
            var slot = await services.GetRequiredService<IGeneratedCredentialSlots>().GetOrCreateAsync(new(input.Owner, input.SlotId), input.GenerationProfile, default);
            var attempt = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(producer, DateTimeOffset.UtcNow, default));
            Assert.True(await new PostgreSqlGeneratedCredentialOperations(factory, services.GetRequiredService<ISecretStore>())
                .CompleteAsync(producer, attempt.AttemptId, new(slot.Reference!), DateTimeOffset.UtcNow, default));
        }
        else
        {
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
            var consumer = ConsumerKey(scenario.Commit);
            var attempt = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(consumer, DateTimeOffset.UtcNow, default));
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(consumer, attempt.AttemptId, DateTimeOffset.UtcNow, default));
            Assert.True(await journal.TryRecordSuccessAsync(consumer, attempt.AttemptId, DateTimeOffset.UtcNow, default));
        }
        Assert.Equal(0, trap.Count);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Single(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Equal(producerCompletion ? 0 : 1, await verify.GenerationStageCheckpoints.CountAsync());
    }
}