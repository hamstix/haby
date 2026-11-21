using System.Data;
using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Haby.PostgreSql;

public sealed record PostgreSqlImportVerificationResult(PostgreSqlImportVerificationStatus Status,
    PostgreSqlExistingPairObservation? Observation = null);

/// <summary>Observes an existing pair and authenticates its supplied credential. Executes SELECT
/// only in read-only sessions; never invokes provisioning, DDL probes, permission repair or rotation.</summary>
public sealed class PostgreSqlImportVerifier(IProviderConfigurationStore providers, ISecretStore secrets,
    IProtectedApplicationCredentialIntake protectedInputs, PostgreSqlProviderTransport? transport = null)
{
    internal const string ApplicationName = "haby-resource-import-verification";
    public async ValueTask<PostgreSqlImportVerificationResult> VerifyAsync(AcceptedOperation accepted, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        byte[]? adminBytes = null, appBytes = null;
        NpgsqlConnectionStringBuilder? admin = null, application = null;
        try
        {
            var input = PostgreSqlImportAdmission.Prepare(accepted.Spec, accepted.Inputs);
            if (input is null || PostgreSqlImportContracts.Decode(accepted.Node) != input) return new(PostgreSqlImportVerificationStatus.InputUnavailable);
            var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(accepted.Inputs.Environment,
                accepted.Provider.Instance, accepted.Provider.Configuration, token))?.Configuration;
            if (configuration is null || configuration.ImplementationId != accepted.Provider.Implementation.Value)
                return new(PostgreSqlImportVerificationStatus.InputUnavailable);
            var owner = new ManagedResourceKey(accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId,
                ManifestLocalId.Create(input.Pair.LoginResource));
            using var adminMaterial = await secrets.ResolveAsync(new(new SecretOwner.Environment(accepted.Inputs.Environment),
                SecretPurpose.ProviderAdministrator), configuration.AdministratorPassword, token);
            using var appMaterial = await protectedInputs.ResolveStagedAsync(new(input.Staged, PostgreSqlImportContracts.Reference(input)), owner, token);
            if (adminMaterial is null || appMaterial is null) return new(PostgreSqlImportVerificationStatus.SecretUnavailable);
            adminBytes = adminMaterial.CopyBytes(); appBytes = appMaterial.CopyBytes();
            var utf8 = new UTF8Encoding(false, true);
            admin = Builder(configuration, input.Pair.DatabaseName, configuration.AdministratorUserName, utf8.GetString(adminBytes));
            application = Builder(configuration, input.Pair.DatabaseName, input.Pair.LoginName, utf8.GetString(appBytes));
            var observed = await ObserveAsync(admin, input.Pair, token);
            if (observed is null || PostgreSqlImportObservation.Verify(input.Pair, observed with
            { CredentialAuthenticated = true, ApplicationTls = observed.AdministratorTls }) != PostgreSqlImportVerificationStatus.Confirmed)
                return new(PostgreSqlImportVerificationStatus.Conflict);
            var authenticated = await AuthenticateAsync(application, input.Pair, token);
            if (authenticated is not { } appTls || appTls != (configuration.TransportMode == PostgreSqlTransportMode.VerifyFull) ||
                observed.AdministratorTls != appTls) return new(PostgreSqlImportVerificationStatus.Conflict);
            // A new snapshot detects drift across authentication. It is not a cross-system atomic transaction
            // or a guarantee against later drift between this verification and catalog acceptance.
            var after = await ObserveAsync(admin, input.Pair, token);
            if (after is null || PostgreSqlImportObservation.Verify(input.Pair, after with
            { CredentialAuthenticated = true, ApplicationTls = after.AdministratorTls }) != PostgreSqlImportVerificationStatus.Confirmed ||
                after.SchemaOwner != observed.SchemaOwner || after.SchemaOwnerOid != observed.SchemaOwnerOid ||
                after.PublicSchemaOwner != observed.PublicSchemaOwner || after.PublicSchemaOwnerOid != observed.PublicSchemaOwnerOid ||
                after.AdministratorTls != observed.AdministratorTls) return new(PostgreSqlImportVerificationStatus.Conflict);
            return new(PostgreSqlImportVerificationStatus.Confirmed, observed with { CredentialAuthenticated = true, ApplicationTls = appTls });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (DecoderFallbackException) { return new(PostgreSqlImportVerificationStatus.SecretUnavailable); }
        catch (PostgresException e) when (e.SqlState.StartsWith("28", StringComparison.Ordinal) || e.SqlState == PostgresErrorCodes.InvalidCatalogName ||
            e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        { return new(PostgreSqlImportVerificationStatus.Conflict); }
        catch (Exception e) when (e is NpgsqlException or IOException or TimeoutException or OperationCanceledException)
        { return new(PostgreSqlImportVerificationStatus.Connectivity); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { return new(PostgreSqlImportVerificationStatus.InputUnavailable); }
        finally
        {
            if (adminBytes is not null) CryptographicOperations.ZeroMemory(adminBytes);
            if (appBytes is not null) CryptographicOperations.ZeroMemory(appBytes);
            if (admin is not null) admin.Password = null;
            if (application is not null) application.Password = null;
        }
    }

    private NpgsqlConnectionStringBuilder Builder(PostgreSqlProviderConfiguration configuration, string database, string user, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = configuration.Host,
            Port = configuration.Port,
            Database = database,
            Username = user,
            Password = password,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5,
            Options = "-c default_transaction_read_only=on -c search_path=pg_catalog",
            IncludeErrorDetail = false,
            ApplicationName = ApplicationName
        };
        (transport ?? new()).Apply(builder, configuration);
        return builder;
    }

    private static async Task<PostgreSqlExistingPairObservation?> ObserveAsync(NpgsqlConnectionStringBuilder builder,
        PostgreSqlExistingPairInput input, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using var command = new NpgsqlCommand("""
            SELECT d.datname, d.oid, r.rolname, r.oid, n.nspname, n.oid, p.oid,
              db_owner.rolname, d.datdba, schema_owner.rolname, n.nspowner, public_owner.rolname, p.nspowner,
              d.datacl::text, n.nspacl::text, p.nspacl::text,
              r.rolcanlogin, r.rolsuper, r.rolcreatedb, r.rolcreaterole, r.rolreplication, r.rolbypassrls,
              EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member = r.oid),
              pg_catalog.has_database_privilege(r.oid, d.oid, 'CONNECT'),
              pg_catalog.has_database_privilege(r.oid, d.oid, 'CREATE'),
              pg_catalog.has_schema_privilege(r.oid, n.oid, 'USAGE'),
              pg_catalog.has_schema_privilege(r.oid, n.oid, 'CREATE'),
              COALESCE((SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid = pg_catalog.pg_backend_pid()), false)
            FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_roles r
            CROSS JOIN pg_catalog.pg_namespace n CROSS JOIN pg_catalog.pg_namespace p
            JOIN pg_catalog.pg_roles public_owner ON public_owner.oid = p.nspowner
            JOIN pg_catalog.pg_roles schema_owner ON schema_owner.oid = n.nspowner
            JOIN pg_catalog.pg_roles db_owner ON db_owner.oid = d.datdba
            WHERE d.datname = pg_catalog.current_database() AND d.datname = $1 AND r.rolname = $2
              AND n.nspname = $3 AND p.nspname = 'public'
            """, connection, transaction);
        command.Parameters.AddWithValue(input.DatabaseName); command.Parameters.AddWithValue(input.LoginName); command.Parameters.AddWithValue(input.Schema);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        string? Acl(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        return new(reader.GetString(0), reader.GetFieldValue<uint>(1), reader.GetString(2), reader.GetFieldValue<uint>(3),
            reader.GetString(4), reader.GetFieldValue<uint>(5), reader.GetFieldValue<uint>(6), reader.GetString(7), reader.GetFieldValue<uint>(8),
            reader.GetString(9), reader.GetFieldValue<uint>(10), reader.GetString(11), reader.GetFieldValue<uint>(12), Acl(13), Acl(14), Acl(15),
            reader.GetBoolean(16), reader.GetBoolean(17), reader.GetBoolean(18), reader.GetBoolean(19), reader.GetBoolean(20), reader.GetBoolean(21),
            reader.GetBoolean(22), false, reader.GetBoolean(23), reader.GetBoolean(24), reader.GetBoolean(25), reader.GetBoolean(26), reader.GetBoolean(27), false);
    }

    private static async Task<bool?> AuthenticateAsync(NpgsqlConnectionStringBuilder builder, PostgreSqlExistingPairInput input, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT d.oid, r.oid, n.oid, p.oid,
              pg_catalog.has_database_privilege(r.oid, d.oid, 'CONNECT'), pg_catalog.has_database_privilege(r.oid, d.oid, 'CREATE'),
              pg_catalog.has_schema_privilege(r.oid, n.oid, 'USAGE'), pg_catalog.has_schema_privilege(r.oid, n.oid, 'CREATE'),
              COALESCE((SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid = pg_catalog.pg_backend_pid()), false)
            FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_roles r
            CROSS JOIN pg_catalog.pg_namespace n CROSS JOIN pg_catalog.pg_namespace p
            WHERE d.datname = pg_catalog.current_database() AND d.datname = $3 AND r.rolname = current_user AND r.rolname = $2
              AND session_user = $2 AND n.nspname = $1 AND p.nspname = 'public'
            """, connection);
        command.Parameters.AddWithValue(input.Schema); command.Parameters.AddWithValue(input.LoginName); command.Parameters.AddWithValue(input.DatabaseName);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetFieldValue<uint>(0) != input.DatabaseOid || reader.GetFieldValue<uint>(1) != input.LoginOid ||
            reader.GetFieldValue<uint>(2) != input.SchemaOid || reader.GetFieldValue<uint>(3) != input.PublicSchemaOid ||
            Enumerable.Range(4, 4).Any(i => !reader.GetBoolean(i))) return null;
        return reader.GetBoolean(8);
    }
}
