using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Npgsql;

namespace Haby.PostgreSql;

/// <summary>The bounded result of one read-only database-absence command on a dedicated connection.</summary>
internal enum DatabaseAbsenceOutcome { Absent, Present, Connectivity }

/// <summary>
/// The bounded result of one dispatched CREATE DATABASE command. A failure that proves the
/// command never reached a session is separated from one that cannot prove the outcome.
/// </summary>
internal enum DatabaseCreateOutcome { Created, Conflict, ServerRejected, OpenFailed, DispatchUncertain }

/// <summary>
/// The shared PostgreSQL command boundary: absence reads and the CREATE command are separate
/// operations over dedicated non-pooled connections so a durable barrier can be committed between
/// them. Driver and transport text never escapes; cancellation is never swallowed.
/// </summary>
internal static class PostgreSqlDatabaseGateway
{
    internal static readonly Regex SafeDatabase = new(@"\A[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static async ValueTask<DatabaseAbsenceOutcome> CheckAbsentAsync(NpgsqlConnectionStringBuilder builder,
        string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", connection);
            command.Parameters.AddWithValue(databaseName);
            return await command.ExecuteScalarAsync(cancellationToken) is not null
                ? DatabaseAbsenceOutcome.Present
                : DatabaseAbsenceOutcome.Absent;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or SocketException)
        {
            return DatabaseAbsenceOutcome.Connectivity;
        }
    }

    internal static async ValueTask<DatabaseCreateOutcome> CreateDatabaseAsync(NpgsqlConnectionStringBuilder builder,
        string databaseName, Func<ValueTask>? onDispatched, CancellationToken cancellationToken)
    {
        using var quote = new NpgsqlCommandBuilder();
        var sql = "CREATE DATABASE " + quote.QuoteIdentifier(databaseName);
        NpgsqlConnection? connection = null;
        try
        {
            connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or SocketException)
        {
            // No session was established, so the command provably never reached the server.
            connection?.Dispose();
            return DatabaseCreateOutcome.OpenFailed;
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
        // From this point, a cancellation or transport error cannot prove that creation did not occur.
        await using (connection)
        {
            try
            {
                if (onDispatched is not null) await onDispatched();
                await using var create = new NpgsqlCommand(sql, connection);
                await create.ExecuteNonQueryAsync(cancellationToken);
                return DatabaseCreateOutcome.Created;
            }
            catch (PostgresException ex) when (cancellationToken.IsCancellationRequested ||
                                               ex.SqlState == "57014")
            {
                // Query cancellation or a timeout does not establish whether creation committed.
                return DatabaseCreateOutcome.DispatchUncertain;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
            {
                return DatabaseCreateOutcome.Conflict;
            }
            catch (PostgresException ex) when (IsConnectionAbort(ex.SqlState))
            {
                // The session died rather than answering the CREATE command; the outcome is unknown.
                return DatabaseCreateOutcome.DispatchUncertain;
            }
            catch (PostgresException)
            {
                // PostgreSQL answered the CREATE command with an error; it did not create this database.
                return DatabaseCreateOutcome.ServerRejected;
            }
            catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or IOException)
            {
                return DatabaseCreateOutcome.DispatchUncertain;
            }
        }
    }

    private static bool IsConnectionAbort(string sqlState) =>
        sqlState.StartsWith("08", StringComparison.Ordinal) ||
        sqlState is PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown
            or PostgresErrorCodes.CannotConnectNow;
}
