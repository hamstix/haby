using System.Security.Cryptography;
using Haby.Application;
using Haby.Domain;
using Npgsql;

namespace Haby.PostgreSql;

public sealed record PostgreSqlAccessTargetInput(PostgreSqlLoginTargetInput Login, PostgreSqlRoleEvidence Role, string Schema);

/// <summary>Deterministic interruption boundaries around real ownership DDL.</summary>
public interface IPostgreSqlAccessFaultHooks
{
    ValueTask AfterMarkerAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterOwnershipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) => ValueTask.CompletedTask;
    ValueTask AfterCommitAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeVerificationAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeAuthenticationAsync(CancellationToken token) => ValueTask.CompletedTask;
    ValueTask BeforeFinalizationAsync(CancellationToken token) => ValueTask.CompletedTask;
}
public sealed class NullPostgreSqlAccessFaultHooks : IPostgreSqlAccessFaultHooks;

/// <summary>PostgreSQL 17 initial owner/access policy. Never adopts or repairs an existing schema.</summary>
public sealed class PostgreSqlApplicationAccessTarget(PostgreSqlDatabaseCreateTarget connections, ISecretStore secrets,
    IProviderResourceReservations reservations, IPostgreSqlAccessFaultHooks hooks)
{
    private static bool Valid(PostgreSqlAccessTargetInput input) => PostgreSqlApplicationLoginContract.ValidSchema(input.Schema) &&
        input.Role.RoleOid != 0 && input.Role.RoleName == input.Login.RoleName &&
        input.Login.RoleName == PostgreSqlPairResourceAdmission.RoleName(input.Login.Owner) &&
        input.Login.Provider.DatabaseName == input.Login.Database.DatabaseName;

    private async Task<bool> Reserved(PostgreSqlLoginTargetInput input, CancellationToken token) =>
        await reservations.ContainsAsync(new(input.Provider.ProviderInstance, input.Owner.EnvironmentRecordId,
            input.Owner.ApplicationRecordId, input.Owner.ResourceLocalId, PostgreSqlApplicationLoginAction.RoleReservationNamespace,
            input.RoleName), token);

    public async ValueTask<PostgreSqlLoginOutcome> CheckAsync(PostgreSqlAccessTargetInput input, CancellationToken token)
    {
        if (!Valid(input)) return PostgreSqlLoginOutcome.InputUnavailable;
        var resolved = await connections.ResolveAsync(input.Login.Provider, token);
        byte[]? bytes = null;
        try
        {
            if (resolved.Outcome != PostgreSqlDatabaseAbsenceOutcome.Absent || resolved.Builder!.Username != input.Login.Administrator)
                return PostgreSqlLoginOutcome.InputUnavailable;
            using var material = await secrets.ResolveAsync(new(new SecretOwner.Resource(input.Login.Owner), SecretPurpose.ApplicationLogin), input.Login.Password, token);
            if (material is null) return PostgreSqlLoginOutcome.SecretUnavailable;
            bytes = material.CopyBytes();
            if (!PostgreSqlScramVerifier.ValidPassword(bytes) || !await Reserved(input.Login, token)) return PostgreSqlLoginOutcome.InputUnavailable;
            resolved.Builder.Database = input.Login.Database.DatabaseName;
            resolved.Builder.Options = "-c search_path=pg_catalog";
            await using var connection = new NpgsqlConnection(resolved.Builder.ConnectionString);
            await connection.OpenAsync(token);
            if (!await Initial(connection, null, input, token)) return PostgreSqlLoginOutcome.Conflict;
            return await PostgreSqlApplicationLoginTarget.Authenticate(resolved.Builder, input.Login, bytes, token)
                ? PostgreSqlLoginOutcome.Confirmed : PostgreSqlLoginOutcome.InputUnavailable;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return PostgreSqlLoginOutcome.Connectivity; }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (resolved.Builder is not null) resolved.Builder.Password = null;
        }
    }

    public async ValueTask<PostgreSqlLoginOutcome> DispatchAsync(PostgreSqlAccessTargetInput input, CancellationToken token)
    {
        if (!Valid(input)) return PostgreSqlLoginOutcome.Uncertain;
        var resolved = await connections.ResolveAsync(input.Login.Provider, token);
        byte[]? bytes = null;
        try
        {
            if (resolved.Outcome != PostgreSqlDatabaseAbsenceOutcome.Absent || resolved.Builder!.Username != input.Login.Administrator ||
                !await Reserved(input.Login, token)) return PostgreSqlLoginOutcome.Uncertain;
            using var material = await secrets.ResolveAsync(new(new SecretOwner.Resource(input.Login.Owner), SecretPurpose.ApplicationLogin), input.Login.Password, token);
            if (material is null) return PostgreSqlLoginOutcome.Uncertain;
            bytes = material.CopyBytes();
            if (!PostgreSqlScramVerifier.ValidPassword(bytes)) return PostgreSqlLoginOutcome.Uncertain;
            resolved.Builder.Database = input.Login.Database.DatabaseName;
            resolved.Builder.Options = "-c search_path=pg_catalog";
            await using var connection = new NpgsqlConnection(resolved.Builder.ConnectionString);
            await connection.OpenAsync(token);
            if (!await PostgreSqlApplicationLoginTarget.Authenticate(resolved.Builder, input.Login, bytes, token)) return PostgreSqlLoginOutcome.Uncertain;
            uint schemaOid;
            await using (var transaction = await connection.BeginTransactionAsync(token))
            {
                var committing = false;
                try
                {
                    await Execute(connection, transaction, "SET LOCAL search_path = pg_catalog", token);
                    if (!await Initial(connection, transaction, input, token))
                    {
                        await transaction.RollbackAsync(token);
                        return PostgreSqlLoginOutcome.Conflict;
                    }
                    using var quote = new NpgsqlCommandBuilder();
                    var database = quote.QuoteIdentifier(input.Login.Database.DatabaseName);
                    var role = quote.QuoteIdentifier(input.Role.RoleName);
                    var schema = quote.QuoteIdentifier(input.Schema);
                    // Ownership/default privileges/search_path require PostgreSQL DDL (ADR-026).
                    // Identifiers come from the exact validated graph and are always quoted.
                    await Execute(connection, transaction, $"ALTER DATABASE {database} OWNER TO {role}", token);
                    await hooks.AfterOwnershipAsync(connection, transaction, token);
                    await Execute(connection, transaction, $"CREATE SCHEMA {schema} AUTHORIZATION {role}", token);
                    await Execute(connection, transaction, $"REVOKE ALL PRIVILEGES ON SCHEMA {schema} FROM PUBLIC", token);
                    await using (var oid = new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = $1", connection, transaction))
                    {
                        oid.Parameters.AddWithValue(input.Schema);
                        schemaOid = (uint)(await oid.ExecuteScalarAsync(token))!;
                    }
                    await hooks.AfterSchemaAsync(connection, transaction, token);
                    await Execute(connection, transaction, $"ALTER ROLE {role} IN DATABASE {database} SET search_path TO {schema}", token);
                    await Execute(connection, transaction, $"ALTER DEFAULT PRIVILEGES FOR ROLE {role} REVOKE EXECUTE ON ROUTINES FROM PUBLIC", token);
                    await Execute(connection, transaction, $"ALTER DEFAULT PRIVILEGES FOR ROLE {role} REVOKE USAGE ON TYPES FROM PUBLIC", token);
                    await hooks.BeforeCommitAsync(connection, transaction, token);
                    token.ThrowIfCancellationRequested();
                    committing = true;
                    await transaction.CommitAsync(token);
                    await hooks.AfterCommitAsync(token);
                }
                catch (PostgresException error) when (!committing && !token.IsCancellationRequested &&
                    !error.SqlState.StartsWith("08", StringComparison.Ordinal) && error.SqlState != PostgresErrorCodes.QueryCanceled)
                {
                    try { await transaction.RollbackAsync(token); }
                    catch (Exception) { return PostgreSqlLoginOutcome.Uncertain; }
                    return error.SqlState is PostgresErrorCodes.DuplicateSchema or PostgresErrorCodes.DuplicateObject
                        ? PostgreSqlLoginOutcome.Conflict : PostgreSqlLoginOutcome.Rejected;
                }
                catch (Exception) { return PostgreSqlLoginOutcome.Uncertain; }
            }
            await hooks.BeforeVerificationAsync(token);
            await using var verify = new NpgsqlConnection(resolved.Builder.ConnectionString);
            await verify.OpenAsync(token);
            if (!await Reserved(input.Login, token) || !await Committed(verify, input, schemaOid, token)) return PostgreSqlLoginOutcome.Uncertain;
            await hooks.BeforeAuthenticationAsync(token);
            if (!await PostgreSqlApplicationLoginTarget.Authenticate(resolved.Builder, input.Login, bytes, token, input.Schema)) return PostgreSqlLoginOutcome.Uncertain;
            return PostgreSqlLoginOutcome.Confirmed;
        }
        catch (Exception) { return PostgreSqlLoginOutcome.Uncertain; }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (resolved.Builder is not null) resolved.Builder.Password = null;
        }
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> Initial(NpgsqlConnection connection, NpgsqlTransaction? transaction, PostgreSqlAccessTargetInput input, CancellationToken token)
    {
        if (!await PostgreSqlApplicationLoginTarget.DatabaseEvidence(connection, transaction, input.Login, token)) return false;
        await using var command = new NpgsqlCommand("""
            SELECT current_setting('server_version_num')::int BETWEEN 170000 AND 179999
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = $3)
              AND r.oid = $1 AND r.rolcanlogin AND NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
              AND r.rolconfig IS NULL AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members WHERE member = r.oid)
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_db_role_setting WHERE setrole = r.oid)
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl WHERE defaclrole = r.oid)
              AND EXISTS (SELECT 1 FROM pg_catalog.pg_database d,
                pg_catalog.aclexplode(COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) a
                WHERE d.oid = $4 AND a.grantee = r.oid AND a.privilege_type = 'CONNECT' AND NOT a.is_grantable)
              AND NOT pg_catalog.has_database_privilege(r.oid, $4, 'CREATE')
              AND NOT pg_catalog.has_database_privilege(r.oid, $4, 'TEMP')
            FROM pg_catalog.pg_roles r WHERE r.rolname = $2
            """, connection, transaction);
        Parameters(command, input);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static void Parameters(NpgsqlCommand command, PostgreSqlAccessTargetInput input)
    {
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, input.Role.RoleOid);
        command.Parameters.AddWithValue(input.Role.RoleName);
        command.Parameters.AddWithValue(input.Schema);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, input.Login.Database.DatabaseOid);
    }

    private static async Task<bool> Committed(NpgsqlConnection connection, PostgreSqlAccessTargetInput input, uint schemaOid, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.oid = $1 AND d.oid = $4 AND d.datname = $5 AND NOT d.datistemplate AND d.datallowconn AND d.datdba = r.oid
              AND current_user = $6 AND (SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user)
              AND r.rolcanlogin AND NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
              AND r.rolconfig IS NULL AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members WHERE member = r.oid)
              AND n.oid = $7 AND n.nspowner = r.oid
              AND p.nspowner = 'pg_database_owner'::pg_catalog.regrole
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) a WHERE a.grantee = 0)
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(p.nspacl, pg_catalog.acldefault('n', p.nspowner))) a WHERE a.grantee = 0)
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a WHERE a.grantee = 0)
              AND EXISTS (SELECT 1 FROM pg_catalog.pg_db_role_setting s WHERE s.setrole = r.oid AND s.setdatabase = d.oid
                AND s.setconfig = ARRAY['search_path=' || pg_catalog.quote_ident($3)])
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_db_role_setting s WHERE s.setrole = r.oid AND s.setdatabase <> d.oid)
              AND (SELECT count(*) FROM pg_catalog.pg_default_acl a WHERE a.defaclrole = r.oid AND a.defaclnamespace = 0
                AND a.defaclobjtype IN ('f','T') AND NOT EXISTS
                  (SELECT 1 FROM pg_catalog.aclexplode(a.defaclacl) x WHERE x.grantee = 0)) = 2
            FROM pg_catalog.pg_roles r CROSS JOIN pg_catalog.pg_database d
            CROSS JOIN pg_catalog.pg_namespace n CROSS JOIN pg_catalog.pg_namespace p
            WHERE r.rolname = $2 AND d.oid = $4 AND n.nspname = $3 AND p.nspname = 'public'
            """, connection);
        Parameters(command, input);
        command.Parameters.AddWithValue(input.Login.Database.DatabaseName);
        command.Parameters.AddWithValue(input.Login.Administrator);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, schemaOid);
        return await command.ExecuteScalarAsync(token) is true;
    }
}
