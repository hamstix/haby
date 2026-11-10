using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Haby.PostgreSql;

public enum PostgreSqlLoginOutcome { Confirmed, Conflict, InputUnavailable, SecretUnavailable, Connectivity, Rejected, Uncertain }
public sealed record PostgreSqlLoginTargetInput(PostgreSqlDatabaseCreateInput Provider, PostgreSqlDatabaseOutput Database,
    string Administrator, ManagedResourceKey Owner, string RoleName, SecretRef Password)
{
    public ResourceNamingPolicy Naming { get; init; } = ResourceNamingPolicy.Exact;
}
public sealed record PostgreSqlLoginResult(PostgreSqlLoginOutcome Outcome, PostgreSqlRoleEvidence? Evidence = null);

/// <summary>Fault boundaries, never alternate successful effects.</summary>
public interface IPostgreSqlLoginFaultHooks
{
    ValueTask AfterMarkerAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterCommitAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeVerificationAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeAuthenticationAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeFinalizationAsync(CancellationToken token) => ValueTask.CompletedTask;
}
public sealed class NullPostgreSqlLoginFaultHooks : IPostgreSqlLoginFaultHooks;

/// <summary>Dedicated managed SCRAM DDL and independent evidence/authentication. No raw exceptions escape.</summary>
public sealed class PostgreSqlApplicationLoginTarget(PostgreSqlDatabaseCreateTarget connections, ISecretStore secrets,
    IPostgreSqlLoginFaultHooks hooks)
{
    // Explicit, connection-local logging policy, never cluster/role configuration. Other SQL keeps
    // its configured logging. Unsupported audit extensions/preloads fail closed before password SQL.
    private const string PrivateOptions = "-c log_statement=none -c log_min_error_statement=panic -c log_min_messages=panic " +
        "-c log_min_duration_statement=-1 -c log_min_duration_sample=-1 -c log_transaction_sample_rate=0 " +
        "-c log_duration=off -c log_parameter_max_length_on_error=0 -c log_error_verbosity=terse " +
        "-c debug_print_parse=off -c debug_print_rewritten=off -c debug_print_plan=off " +
        "-c log_statement_stats=off -c log_parser_stats=off -c log_planner_stats=off -c log_executor_stats=off";

    private static NpgsqlDataSource PrivateSource(NpgsqlConnectionStringBuilder builder)
    {
        builder.Options = PrivateOptions;
        builder.IncludeErrorDetail = false;
        builder.LogParameters = false;
        builder.Pooling = false;
        var source = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        source.UseLoggerFactory(NullLoggerFactory.Instance).EnableParameterLogging(false);
        source.ConfigureTracing(t => t.ConfigureCommandFilter(_ => false).ConfigureBatchFilter(_ => false)
            .ConfigureCopyOperationFilter(_ => false).EnablePhysicalOpenTracing(false));
        return source.Build();
    }

    public async ValueTask<PostgreSqlLoginOutcome> CheckAsync(PostgreSqlLoginTargetInput input, CancellationToken token)
    {
        var resolved = await connections.ResolveAsync(input.Provider, token);
        try
        {
            if (resolved.Outcome != PostgreSqlDatabaseAbsenceOutcome.Absent)
                return resolved.Outcome == PostgreSqlDatabaseAbsenceOutcome.SecretUnavailable ? PostgreSqlLoginOutcome.SecretUnavailable : PostgreSqlLoginOutcome.InputUnavailable;
            if (input.RoleName != PostgreSqlResourceNaming.Role(input.Owner, input.Naming) ||
                input.Provider.DatabaseName != input.Database.DatabaseName || resolved.Builder!.Username != input.Administrator)
                return PostgreSqlLoginOutcome.InputUnavailable;
            using var material = await secrets.ResolveAsync(new(new SecretOwner.Resource(input.Owner), SecretPurpose.ApplicationLogin), input.Password, token);
            if (material is null) return PostgreSqlLoginOutcome.SecretUnavailable;
            var bytes = material.CopyBytes();
            try { if (!PostgreSqlScramVerifier.ValidPassword(bytes)) return PostgreSqlLoginOutcome.SecretUnavailable; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            resolved.Builder.Database = input.Database.DatabaseName;
            await using var source = PrivateSource(resolved.Builder);
            await using var connection = await source.OpenConnectionAsync(token);
            if (!await LoggingSupported(connection, token)) return PostgreSqlLoginOutcome.InputUnavailable;
            return await DatabaseEvidence(connection, null, input, token) && await RoleAbsent(connection, null, input.RoleName, token)
                ? PostgreSqlLoginOutcome.Confirmed : PostgreSqlLoginOutcome.Conflict;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return PostgreSqlLoginOutcome.Connectivity; }
        finally { if (resolved.Builder is not null) resolved.Builder.Password = null; }
    }

    public async ValueTask<PostgreSqlLoginResult> DispatchAsync(PostgreSqlLoginTargetInput input, CancellationToken token)
    {
        var resolved = await connections.ResolveAsync(input.Provider, token);
        byte[]? bytes = null;
        try
        {
            if (resolved.Outcome != PostgreSqlDatabaseAbsenceOutcome.Absent || resolved.Builder!.Username != input.Administrator)
                return new(PostgreSqlLoginOutcome.Uncertain);
            using var material = await secrets.ResolveAsync(new(new SecretOwner.Resource(input.Owner), SecretPurpose.ApplicationLogin), input.Password, token);
            if (material is null) return new(PostgreSqlLoginOutcome.Uncertain);
            bytes = material.CopyBytes();
            if (!PostgreSqlScramVerifier.ValidPassword(bytes) || input.RoleName != PostgreSqlResourceNaming.Role(input.Owner, input.Naming))
                return new(PostgreSqlLoginOutcome.Uncertain);
            resolved.Builder.Database = input.Database.DatabaseName;
            await using var source = PrivateSource(resolved.Builder);
            await using var connection = await source.OpenConnectionAsync(token);
            if (!await LoggingSupported(connection, token)) return new(PostgreSqlLoginOutcome.Uncertain);
            uint oid;
            await using (var transaction = await connection.BeginTransactionAsync(token))
            {
                var committing = false;
                try
                {
                    await using (var path = new NpgsqlCommand("SET LOCAL search_path = pg_catalog", connection, transaction))
                        await path.ExecuteNonQueryAsync(token);
                    if (!await DatabaseEvidence(connection, transaction, input, token) || !await RoleAbsent(connection, transaction, input.RoleName, token))
                    {
                        await transaction.RollbackAsync(token);
                        return new(PostgreSqlLoginOutcome.Conflict);
                    }
                    // Dedicated PostgreSQL password DDL cannot bind a parameter. ADR-034 permits
                    // only this format-validated confidential verifier, never the original password.
                    await hooks.BeforeRoleAsync(connection, transaction, token);
                    var verifier = PostgreSqlScramVerifier.Create(bytes);
                    using var quote = new NpgsqlCommandBuilder();
                    // NpgsqlCommand emits SQL through Npgsql.Sql EventSource independently of
                    // ILogger/Activity filters. The public batch API in pinned Npgsql 10.0.3 emits
                    // no batch SQL there. Captured EventSource tests gate this version-specific
                    // property on success and rejection; never replace this with NpgsqlCommand.
                    await using (var create = new NpgsqlBatch(connection, transaction))
                    {
                        var command = new NpgsqlBatchCommand("CREATE ROLE " + quote.QuoteIdentifier(input.RoleName) +
                            " LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '" + verifier.Replace("'", "''", StringComparison.Ordinal) + "'");
                        create.BatchCommands.Add(command);
                        try { await create.ExecuteNonQueryAsync(token); }
                        finally { command.CommandText = string.Empty; create.BatchCommands.Clear(); }
                    }
                    oid = await RoleOid(connection, transaction, input.RoleName, token);
                    await hooks.AfterRoleAsync(connection, transaction, token);
                    await using (var grant = new NpgsqlCommand("GRANT CONNECT ON DATABASE " + quote.QuoteIdentifier(input.Database.DatabaseName) +
                        " TO " + quote.QuoteIdentifier(input.RoleName), connection, transaction))
                        await grant.ExecuteNonQueryAsync(token);
                    token.ThrowIfCancellationRequested();
                    committing = true;
                    await transaction.CommitAsync(token);
                    await hooks.AfterCommitAsync(token);
                }
                catch (PostgresException error) when (!committing && !token.IsCancellationRequested &&
                    !error.SqlState.StartsWith("08", StringComparison.Ordinal) && error.SqlState != PostgresErrorCodes.QueryCanceled)
                {
                    try { await transaction.RollbackAsync(token); }
                    catch (Exception) { return new(PostgreSqlLoginOutcome.Uncertain); }
                    return new(error.SqlState == PostgresErrorCodes.DuplicateObject ? PostgreSqlLoginOutcome.Conflict : PostgreSqlLoginOutcome.Rejected);
                }
                catch (Exception) { return new(PostgreSqlLoginOutcome.Uncertain); }
            }
            await hooks.BeforeVerificationAsync(token);
            await using var verify = await source.OpenConnectionAsync(token);
            if (oid == 0 || !await DatabaseEvidence(verify, null, input, token) ||
                !await RoleEvidence(verify, input, oid, token)) return new(PostgreSqlLoginOutcome.Uncertain);
            await hooks.BeforeAuthenticationAsync(token);
            if (!await Authenticate(resolved.Builder, input, bytes, token)) return new(PostgreSqlLoginOutcome.Uncertain);
            return new(PostgreSqlLoginOutcome.Confirmed, new(input.RoleName, oid));
        }
        catch (Exception) { return new(PostgreSqlLoginOutcome.Uncertain); }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (resolved.Builder is not null) resolved.Builder.Password = null;
        }
    }

    internal static async Task<bool> Authenticate(NpgsqlConnectionStringBuilder admin, PostgreSqlLoginTargetInput input,
        byte[] password, CancellationToken token, string? schema = null)
    {
        // Do not give a non-superuser the administrator-only logging startup options.
        var builder = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        { Username = input.RoleName, Password = Encoding.ASCII.GetString(password), Options = null, Pooling = false };
        try
        {
            await using (var source = new NpgsqlDataSourceBuilder(builder.ConnectionString).UseLoggerFactory(NullLoggerFactory.Instance)
                .ConfigureTracing(t => t.ConfigureCommandFilter(_ => false).EnablePhysicalOpenTracing(false)).Build())
            await using (var login = await source.OpenConnectionAsync(token))
            {
                await using var identity = new NpgsqlCommand("SELECT current_user = $1 AND pg_catalog.current_database() = $2", login);
                identity.Parameters.AddWithValue(input.RoleName); identity.Parameters.AddWithValue(input.Database.DatabaseName);
                if (await identity.ExecuteScalarAsync(token) is not true) return false;
                if (schema is not null)
                {
                    await using var access = new NpgsqlCommand("""
                        SELECT pg_catalog.current_schema() = $1
                          AND pg_catalog.has_schema_privilege(current_user, $1, 'USAGE,CREATE')
                          AND pg_catalog.has_database_privilege(current_user, pg_catalog.current_database(), 'CONNECT,CREATE,TEMP')
                        """, login);
                    access.Parameters.AddWithValue(schema);
                    if (await access.ExecuteScalarAsync(token) is not true) return false;
                }
            }
            // A successful correct password under trust is insufficient. Refuse publication unless
            // an independent wrong-password connection actually receives invalid_password.
            builder.Password = Guid.NewGuid().ToString("N");
            await using var wrongSource = new NpgsqlDataSourceBuilder(builder.ConnectionString).UseLoggerFactory(NullLoggerFactory.Instance)
                .ConfigureTracing(t => t.ConfigureCommandFilter(_ => false).EnablePhysicalOpenTracing(false)).Build();
            try { await using var wrong = await wrongSource.OpenConnectionAsync(token); return false; }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidPassword) { return true; }
        }
        finally { builder.Password = null; }
    }

    private static async Task<bool> LoggingSupported(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT current_setting('server_version_num')::int BETWEEN 170000 AND 179999
              AND current_setting('shared_preload_libraries') = ''
              AND current_setting('session_preload_libraries') = '' AND current_setting('local_preload_libraries') = ''
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_extension WHERE extname <> 'plpgsql')
              AND current_setting('log_statement') = 'none' AND current_setting('log_min_error_statement') = 'panic'
              AND current_setting('log_min_messages') = 'panic' AND current_setting('log_min_duration_statement') = '-1'
              AND current_setting('log_min_duration_sample') = '-1' AND current_setting('log_transaction_sample_rate') = '0'
              AND current_setting('log_duration') = 'off'
            """, connection);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task<bool> DatabaseEvidence(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        PostgreSqlLoginTargetInput input, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT d.oid = $1 AND d.datname = $2 AND NOT d.datistemplate AND d.datallowconn
              AND owner.rolname = $3 AND current_user = $3 AND owner.rolsuper
              AND n.nspowner = 'pg_database_owner'::pg_catalog.regrole
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) a WHERE a.grantee = 0)
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a
                WHERE a.grantee = 0 AND a.privilege_type IN ('USAGE', 'CREATE'))
            FROM pg_catalog.pg_database d JOIN pg_catalog.pg_roles owner ON owner.oid = d.datdba
            CROSS JOIN pg_catalog.pg_namespace n
            WHERE d.datname = pg_catalog.current_database() AND n.nspname = 'public'
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, input.Database.DatabaseOid);
        command.Parameters.AddWithValue(input.Database.DatabaseName); command.Parameters.AddWithValue(input.Administrator);
        return await command.ExecuteScalarAsync(token) is true;
    }
    private static async Task<bool> RoleAbsent(NpgsqlConnection connection, NpgsqlTransaction? transaction, string role, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = $1)", connection, transaction);
        command.Parameters.AddWithValue(role);
        return await command.ExecuteScalarAsync(token) is true;
    }
    private static async Task<uint> RoleOid(NpgsqlConnection connection, NpgsqlTransaction transaction, string role, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_roles WHERE rolname = $1", connection, transaction);
        command.Parameters.AddWithValue(role);
        return await command.ExecuteScalarAsync(token) is uint oid ? oid : 0;
    }
    internal static async Task<bool> RoleEvidence(NpgsqlConnection connection, PostgreSqlLoginTargetInput input, uint oid, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.oid = $1 AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole
              AND NOT r.rolreplication AND NOT r.rolbypassrls
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members WHERE member = r.oid)
              AND EXISTS (SELECT 1 FROM pg_catalog.pg_database d,
                pg_catalog.aclexplode(COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) a
                WHERE d.oid = $3 AND a.grantee = r.oid AND a.privilege_type = 'CONNECT' AND NOT a.is_grantable)
              AND NOT pg_catalog.has_database_privilege(r.oid, $3, 'CREATE')
              AND NOT pg_catalog.has_database_privilege(r.oid, $3, 'TEMP')
            FROM pg_catalog.pg_roles r WHERE rolname = $2
            """, connection);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, oid); command.Parameters.AddWithValue(input.RoleName);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, input.Database.DatabaseOid);
        return await command.ExecuteScalarAsync(token) is true;
    }
}
