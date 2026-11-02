using System.Text.Json;
using Haby.Application;

namespace Haby.PostgreSql;

internal sealed class PostgreSqlDurableCreateInputResolver
{
    public ValueTask<(PostgreSqlDatabaseCreateInput? Input, DurableOperationReason? Reason, bool Definitive)>
        ResolveAsync(DurableOperationKey key, DurableOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PostgreSqlDatabaseActionInput? input = null;
        try
        {
            if (operation.Node.Action == PostgreSqlDatabaseCreateAction.RegistrationKey &&
                operation.Node.Input.Format == PostgreSqlDatabaseCreateAction.Format)
                input = JsonSerializer.Deserialize(operation.Node.Input.Json, PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput);
        }
        catch (JsonException) { }
        return ValueTask.FromResult<(PostgreSqlDatabaseCreateInput?, DurableOperationReason?, bool)>(input is null
            ? (null, DurableOperationReason.InputUnavailable, false)
            : (new(operation.Environment, operation.ProviderInstance, operation.ConfigurationRevision,
                operation.ImplementationId, input.DatabaseName), null, false));
    }
}
