using Haby.Application;
using Npgsql;

namespace Haby.PostgreSql;

public enum PostgreSqlRestrictOutcome { Confirmed, Conflict, InputUnavailable, SecretUnavailable, Connectivity, Rejected, Uncertain }
public sealed record PostgreSqlRestrictTargetInput(PostgreSqlDatabaseCreateInput Provider,
    PostgreSqlDatabaseOutput Database, string ExpectedOwnerUserName);

/// <summary>Named fault boundaries only; no alternate success executor.</summary>
public interface IPostgreSqlRestrictFaultHooks
{
    ValueTask AfterMarkerAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterDatabaseRevokeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterCommitAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeVerificationAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeFinalizationAsync(CancellationToken token) => ValueTask.CompletedTask;
}
public sealed class NullPostgreSqlRestrictFaultHooks : IPostgreSqlRestrictFaultHooks;

/// <summary>Exact-database ACL capability. Each connection resolves the saved administrator SecretRef.</summary>
public sealed class PostgreSqlDatabaseRestrictTarget(PostgreSqlDatabaseCreateTarget connections, IPostgreSqlRestrictFaultHooks hooks)
{
    public async ValueTask<PostgreSqlRestrictOutcome> CheckAsync(PostgreSqlRestrictTargetInput input, CancellationToken token)
    {
        var resolved = await connections.ResolveAsync(input.Provider, token);
        try
        {
            if (resolved.Outcome != PostgreSqlDatabaseAbsenceOutcome.Absent) return Map(resolved.Outcome);
            if (resolved.Builder!.Username != input.ExpectedOwnerUserName || input.Provider.DatabaseName != input.Database.DatabaseName)
                return PostgreSqlRestrictOutcome.Conflict;
            resolved.Builder.Database = input.Database.DatabaseName;
            await using var connection = new NpgsqlConnection(resolved.Builder.ConnectionString);
            await connection.OpenAsync(token);
            return await EvidenceAsync(connection, null, input, acl: false, token)
                ? PostgreSqlRestrictOutcome.Confirmed : PostgreSqlRestrictOutcome.Conflict;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return PostgreSqlRestrictOutcome.Connectivity; }
        finally { if (resolved.Builder is not null) resolved.Builder.Password = null; }
    }

    public async ValueTask<PostgreSqlRestrictOutcome> DispatchAsync(PostgreSqlRestrictTargetInput input, CancellationToken token)
    {
        var resolved = await connections.ResolveAsync(input.Provider, token);
        try
        {
            if (resolved.Outcome != PostgreSqlDatabaseAbsenceOutcome.Absent || resolved.Builder!.Username != input.ExpectedOwnerUserName)
                return PostgreSqlRestrictOutcome.Uncertain;
            resolved.Builder.Database = input.Database.DatabaseName;
            await using var connection = new NpgsqlConnection(resolved.Builder.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            var committing = false;
            try
            {
                // Local PostgreSQL ACL/catalog capability. No cluster policy, role or ownership mutation.
                await using (var searchPath = new NpgsqlCommand("SET LOCAL search_path = pg_catalog", connection, transaction))
                    await searchPath.ExecuteNonQueryAsync(token);
                if (!await EvidenceAsync(connection, transaction, input, acl: false, token))
                {
                    await transaction.RollbackAsync(token);
                    return PostgreSqlRestrictOutcome.Conflict;
                }
                using var quote = new NpgsqlCommandBuilder();
                await using (var database = new NpgsqlCommand("REVOKE ALL PRIVILEGES ON DATABASE " +
                    quote.QuoteIdentifier(input.Database.DatabaseName) + " FROM PUBLIC", connection, transaction))
                    await database.ExecuteNonQueryAsync(token);
                await hooks.AfterDatabaseRevokeAsync(connection, transaction, token);
                await using (var schema = new NpgsqlCommand("REVOKE USAGE, CREATE ON SCHEMA public FROM PUBLIC", connection, transaction))
                    await schema.ExecuteNonQueryAsync(token);
                token.ThrowIfCancellationRequested();
                committing = true;
                await transaction.CommitAsync(token);
                await hooks.AfterCommitAsync(token);
            }
            catch (PostgresException error) when (!committing && !token.IsCancellationRequested &&
                !error.SqlState.StartsWith("08", StringComparison.Ordinal) && error.SqlState != PostgresErrorCodes.QueryCanceled)
            {
                // A server rejection is definitive only after PostgreSQL acknowledges rollback.
                try { await transaction.RollbackAsync(token); }
                catch (Exception) { return PostgreSqlRestrictOutcome.Uncertain; }
                return PostgreSqlRestrictOutcome.Rejected;
            }
            catch (Exception) { return PostgreSqlRestrictOutcome.Uncertain; }

            await hooks.BeforeVerificationAsync(token);
            // Independent session after acknowledged commit; never verify through transaction-local state.
            await using var verify = new NpgsqlConnection(resolved.Builder.ConnectionString);
            await verify.OpenAsync(token);
            return await EvidenceAsync(verify, null, input, acl: true, token)
                ? PostgreSqlRestrictOutcome.Confirmed : PostgreSqlRestrictOutcome.Uncertain;
        }
        catch (Exception) { return PostgreSqlRestrictOutcome.Uncertain; }
        finally { if (resolved.Builder is not null) resolved.Builder.Password = null; }
    }

    private static async Task<bool> EvidenceAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        PostgreSqlRestrictTargetInput input, bool acl, CancellationToken token)
    {
        // Inspect PUBLIC entries, not effective superuser/owner privileges. All values are parameters.
        await using var command = new NpgsqlCommand("""
            SELECT d.oid = $1 AND d.datname = $2 AND NOT d.datistemplate
              AND d.datallowconn AND owner.rolname = $3 AND current_user = $3
              AND n.nspowner = 'pg_database_owner'::pg_catalog.regrole
              AND (NOT $4 OR (
                NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) a WHERE a.grantee = 0)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a
                  WHERE a.grantee = 0 AND a.privilege_type IN ('USAGE', 'CREATE'))))
            FROM pg_catalog.pg_database d
            JOIN pg_catalog.pg_roles owner ON owner.oid = d.datdba
            CROSS JOIN pg_catalog.pg_namespace n
            WHERE d.datname = pg_catalog.current_database() AND n.nspname = 'public'
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, input.Database.DatabaseOid);
        command.Parameters.AddWithValue(input.Database.DatabaseName);
        command.Parameters.AddWithValue(input.ExpectedOwnerUserName);
        command.Parameters.AddWithValue(acl);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static PostgreSqlRestrictOutcome Map(PostgreSqlDatabaseAbsenceOutcome outcome) => outcome switch
    {
        PostgreSqlDatabaseAbsenceOutcome.SecretUnavailable => PostgreSqlRestrictOutcome.SecretUnavailable,
        PostgreSqlDatabaseAbsenceOutcome.ProviderInputUnavailable => PostgreSqlRestrictOutcome.InputUnavailable,
        _ => PostgreSqlRestrictOutcome.Connectivity
    };
}
