using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

[Collection("Login logging isolated")]
public sealed partial class PostgreSqlPairImportTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly List<string> databases = [];
    private readonly List<string> roles = [];
    private readonly Dictionary<string, string> originalSettings = [];
    private bool resetAdministratorSearchPath;
    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Existing_pair_with_administrator_owned_public_schema_imports_without_external_mutation_after_restart_and_lost_response()
    {
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var before = await Snapshot(pair);
        await VerificationLogging();
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable);
        await using (var interrupted = Services(setup.Connection, new InterruptMarkers()))
            await Assert.ThrowsAsync<IOException>(() => interrupted.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default).AsTask());
        await AssertNoUsableCredential(setup);
        await using (var lost = Services(setup.Connection, new LoseAcceptanceResponse()))
            await Assert.ThrowsAsync<IOException>(() => lost.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default).AsTask());
        Guid receipt;
        await using (var restart = Services(setup.Connection))
        {
            var result = await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default);
            Assert.Null(result.StopOutcome);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
            var accepted = await restart.GetRequiredService<IResourceImportAcceptance>().FindAsync(setup.Key, default);
            Assert.NotNull(accepted);
            receipt = accepted.Id;
            var observed = PostgreSqlImportObservation.Decode(accepted.Evidence)!;
            Assert.Equal("postgres", observed.SchemaOwner);
            Assert.Contains("=UC/postgres", observed.SchemaAcl);
            Assert.False(observed.AdministratorTls);
            Assert.False(observed.ApplicationTls);
            using var material = Material(pair.Password);
            Assert.Equal(new(ProtectedCredentialStageStatus.Existing, setup.Staged), await restart.GetRequiredService<IProtectedApplicationCredentialIntake>()
                .StageAsync(setup.Request, setup.Owner, material, default));
            using var changed = Material(Guid.NewGuid().ToString());
            Assert.Equal(ProtectedCredentialStageStatus.Conflict, (await restart.GetRequiredService<IProtectedApplicationCredentialIntake>()
                .StageAsync(setup.Request, setup.Owner, changed, default)).Status);
            await DeliveredConnection(restart, setup, pair);
        }
        await using (var verify = Services(setup.Connection))
        await using (var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(receipt, (await db.ResourceImportReceipts.AsNoTracking().SingleAsync()).Id);
            Assert.Equal(2, await db.OperationOutputs.CountAsync());
            Assert.Equal(2, await db.DurableOperationAttempts.CountAsync());
            Assert.Empty(await db.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
            Assert.All(await db.ResourceManagement.AsNoTracking().ToArrayAsync(), x => Assert.Equal(ResourceLifecycleAuthority.UseAndRetain, x.Authority));
        }
        var wrong = await Prepare(pair, PostgreSqlTransportMode.Disable, password: Guid.NewGuid().ToString());
        await using (var act = Services(wrong.Connection))
        {
            var result = await act.GetRequiredService<DurableGenerationRunner>().RunAsync(wrong.Commit.DeploymentId, wrong.Commit.Generation, default);
            Assert.Equal(DurableExecutionOutcome.Failed, result.StopOutcome);
            Assert.All(result.Progress!.Nodes, x => Assert.Equal(DurableOperationReason.Conflict, x.Operation.Reason));
        }
        await AssertNoUsableCredential(wrong);
        Assert.True(before == await Snapshot(pair), "Import changed existing external identities, owners, ACLs, data or password.");
        Assert.True(await PasswordUnchanged(passwordGuard, pair), "Import changed the existing server-side password verifier.");
        var logs = await fixture.ReadServerLogsAsync();
        Assert.False(logs.Contains(pair.Password, StringComparison.Ordinal), "Existing credential material appeared in server logs.");
        var verification = logs.Split('\n').Where(x => x.StartsWith(PostgreSqlImportVerifier.ApplicationName + "|" + pair.Database + "|", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(verification);
        Assert.False(Regex.IsMatch(string.Join('\n', verification), @"(?:statement:|execute[^:]*:)\s*(?:CREATE|ALTER|GRANT|REVOKE|DROP|TRUNCATE|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase),
            "Import verification dispatched an external mutation command.");
    }

    private sealed record Pair(string Database, string Login, string Password, PostgreSqlExistingPairInput Input)
    {
        public override string ToString() => "[Owned existing pair]";
    }
    private sealed record Setup(string Connection, DurableChangeSetCommit Commit, DurablePlan Plan, ManagedResourceKey Owner,
        ProtectedCredentialReference Staged, Guid Request)
    {
        public DurableOperationKey Key => new(Commit.DeploymentId, Commit.Generation, OperationId.Create("app-login-access"));
    }

    private async Task<Pair> ExistingPair()
    {
        var role = "import_login_" + Guid.NewGuid().ToString("N")[..16];
        var database = "existing_import_" + Guid.NewGuid().ToString("N")[..16];
        var password = Guid.NewGuid().ToString("N") + "; existing credential";
        roles.Add(role); databases.Add(database);
        // Test-owned arrangement only. Import starts after the final snapshot below.
        await Sql(Control(), $"CREATE ROLE \"{role}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}'");
        await Sql(Control(), $"CREATE DATABASE \"{database}\" OWNER \"{role}\"");
        var target = new NpgsqlConnectionStringBuilder(Control()) { Database = database }.ConnectionString;
        await Sql(target, $"""
            ALTER SCHEMA public OWNER TO postgres;
            GRANT USAGE, CREATE ON SCHEMA public TO PUBLIC;
            SET ROLE "{role}";
            CREATE TABLE public.fixture_data(id integer PRIMARY KEY, value text NOT NULL);
            INSERT INTO public.fixture_data VALUES (1, 'retained');
            CREATE SEQUENCE public.fixture_sequence;
            RESET ROLE;
            """);
        await using var connection = new NpgsqlConnection(target); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT d.oid, r.oid, n.oid FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_roles r CROSS JOIN pg_catalog.pg_namespace n
            WHERE d.datname = current_database() AND r.rolname = $1 AND n.nspname = 'public'
            """, connection);
        command.Parameters.AddWithValue(role);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        return new(database, role, password, new("db", "app-login", database, reader.GetFieldValue<uint>(0), role,
            reader.GetFieldValue<uint>(1), "public", reader.GetFieldValue<uint>(2), reader.GetFieldValue<uint>(2)));
    }

    private async Task<Setup> Prepare(Pair pair, PostgreSqlTransportMode mode, PostgreSqlProviderTransport? transport = null,
        string? password = null, string? existingCatalog = null)
    {
        var catalog = existingCatalog;
        if (catalog is null)
        {
            var name = "import_catalog_" + Guid.NewGuid().ToString("N"); databases.Add(name);
            await Sql(Control(), $"CREATE DATABASE \"{name}\"");
            catalog = new NpgsqlConnectionStringBuilder(Control()) { Database = name }.ConnectionString;
        }
        await using var services = Services(catalog, transport: transport);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var configurations = services.GetRequiredService<IProviderConfigurationStore>();
        var alias = ProviderAlias.Create("postgresql.primary");
        var previous = await services.GetRequiredService<IProviderAliasStore>().ResolveAsync(environment, alias, default);
        if (previous is null)
        {
            var adminSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
            var control = new NpgsqlConnectionStringBuilder(Control());
            var bytes = Encoding.UTF8.GetBytes(control.Password!);
            try { await secrets.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), adminSecret, bytes, default); }
            finally { Array.Clear(bytes); control.Password = null; }
            var configuration = new PostgreSqlProviderConfiguration(PostgreSqlProviderConfiguration.CurrentShapeVersion, "127.0.0.1", control.Port,
                "postgres", "postgres", adminSecret, PostgreSqlDatabaseCheckpointCapability.ImplementationId, mode);
            var instance = await configurations.CreateInstanceAsync(environment, configuration, default);
            await configurations.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
                alias, null, new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
        }
        var envelope = Declaration("import-app-" + Guid.NewGuid().ToString("N")[..12]);
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(),
            new([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [], [], [new(PostgreSqlConnectionRendering.Renderer)]), TimeProvider.System)
            .ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(envelope), "tester")).ManifestRevisionRef;
        var owner = new ManagedResourceKey(environment, revision.ApplicationRecordId, ManifestLocalId.Create("app-login"));
        var request = Guid.NewGuid();
        using var material = Material(password ?? pair.Password);
        var staged = (await services.GetRequiredService<IProtectedApplicationCredentialIntake>().StageAsync(request, owner, material, default)).Reference!;
        var intent = new ResourceImportIntent([new(ManifestLocalId.Create("db"), PostgreSqlImportContracts.Encode(pair.Input)),
            new(owner.ResourceLocalId, PostgreSqlImportContracts.Encode(pair.Input), staged)]);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateImportChangeSetAsync(environment, revision.ApplicationRecordId, revision, intent, "tester", default)).ChangeSet!;
        var planned = await planner.CreatePlanAsync(change.Id, "tester", default);
        Assert.Equal(DurablePlanStatus.Planned, planned.Status);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(planned.Plan!.Id, default));
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
            .CommitAsync(change.Id, planned.Plan.Id, "tester", default)).Commit;
        return new(catalog, commit, planned.Plan, owner, staged, request);
    }

    private static JsonObject Declaration(string application)
    {
        var envelope = PostgreSqlPairPlanningTests.Example();
        envelope["manifest"]!["metadata"]!["id"] = application;
        envelope["manifest"]!["spec"]!["resources"]!["app-login"]!["properties"]!["schema"] = "public";
        envelope["manifest"]!["spec"]!["components"] = JsonNode.Parse("""
            {"worker":{"documents":{"settings":{"fileName":"resource.json","format":"json","template":{}}},
              "bindings":{"use":{"target":{"kind":"local-resource","resourceRef":"app-login"},"options":{"sslMode":"disable"},"documentProjections":[
                {"documentRef":"settings","path":"/Connection","rendererRef":"postgresql.npgsql-connection/v1alpha1"}]}}}}
            """u8);
        return envelope;
    }

    private static async Task DeliveredConnection(ServiceProvider services, Setup setup, Pair pair)
    {
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var documents = new PostgreSqlConfigurationDocumentStore(factory, new(PostgreSqlConnectionRendering.CreateCatalog(),
            PostgreSqlConnectionRendering.FormatterCatalog(), services.GetRequiredService<IConfigurationOutputEvidence>()), TimeProvider.System);
        var document = await documents.MaterializeAsync(new(setup.Commit.DeploymentId, setup.Commit.Generation,
            ManifestLocalId.Create("worker"), ManifestLocalId.Create("settings")), "tester", default);
        Assert.Equal(ConfigurationDocumentStatus.Created, document.Status);
        var access = new PostgreSqlConfigurationAccessStore(factory, documents, TimeProvider.System);
        var grant = await access.CreateAsync(new(EnvironmentId.Create("pilot"), document.Revision!.Metadata.Application,
            ManifestLocalId.Create("worker"), ManifestLocalId.Create("settings"), setup.Commit.Generation), "tester", null, default);
        Assert.NotNull(grant);
        var delivery = new DeliverConfiguration(access, access, services.GetRequiredService<ISecretStore>(), PostgreSqlConnectionRendering.CreateCatalog(),
            [new PostgreSqlConnectionResolutionBinding()]);
        using var delivered = await delivery.ExecuteAsync(grant.Credential, document.Revision.Metadata.Id, default);
        Assert.Equal(ConfigurationDeliveryStatus.Delivered, delivered.Status);
        using var json = JsonDocument.Parse(delivered.Json);
        var builder = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("Connection"u8).GetString());
        Assert.True(builder.Password == pair.Password, "Delivery must use the exact existing credential.");
        builder.Pooling = false;
        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString); await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT value FROM public.fixture_data WHERE id = 1", connection);
            Assert.Equal("retained", await command.ExecuteScalarAsync());
        }
        finally { builder.Password = null; }
    }

    private static async Task AssertNoUsableCredential(Setup setup)
    {
        await using var services = Services(setup.Connection);
        await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.False(await db.OperationOutputs.AnyAsync(x => x.DeploymentId == Guid.Parse(setup.Commit.DeploymentId.ToString())));
        Assert.False(await db.ResourceImportReceipts.AnyAsync(x => x.Deployment == Guid.Parse(setup.Commit.DeploymentId.ToString())));
        Assert.False(await db.Secrets.AnyAsync(x => x.Id == Guid.Parse(setup.Staged.Reference.SecretId.ToString())));
        Assert.Null(await services.GetRequiredService<IApplicationCredentialReader>().FindAsync(setup.Owner, setup.Staged.Reference, default));
    }

    private string Control() => new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false, SslMode = Npgsql.SslMode.Disable }.ConnectionString;
    private static SecretMaterial Material(string value) => new(Encoding.UTF8.GetBytes(value));
    private async Task<NpgsqlConnection> PasswordGuard(Pair pair)
    {
        var connection = new NpgsqlConnection(Control()); await connection.OpenAsync();
        try
        {
            // The verifier never leaves PostgreSQL; the client only receives the final equality boolean.
            await using var command = new NpgsqlCommand("CREATE TEMP TABLE import_password_before AS SELECT oid, rolpassword FROM pg_catalog.pg_authid WHERE rolname = $1", connection);
            command.Parameters.AddWithValue(pair.Login); await command.ExecuteNonQueryAsync(); return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static async Task<bool> PasswordUnchanged(NpgsqlConnection connection, Pair pair)
    {
        await using var command = new NpgsqlCommand("SELECT r.rolpassword IS NOT DISTINCT FROM p.rolpassword FROM pg_catalog.pg_authid r JOIN import_password_before p ON p.oid = r.oid WHERE r.rolname = $1", connection);
        command.Parameters.AddWithValue(pair.Login); return await command.ExecuteScalarAsync() is true;
    }
    private async Task Setting(string name, string value)
    {
        if (!originalSettings.ContainsKey(name))
        {
            await using var connection = new NpgsqlConnection(Control()); await connection.OpenAsync();
            originalSettings[name] = (string)(await new NpgsqlCommand("SHOW " + name, connection).ExecuteScalarAsync())!;
        }
        await Sql(Control(), "ALTER SYSTEM SET " + name + " = '" + value.Replace("'", "''", StringComparison.Ordinal) + "'");
    }
    private async Task VerificationLogging()
    {
        await Setting("log_parameter_max_length", "0");
        await Setting("log_parameter_max_length_on_error", "0");
        await Setting("log_line_prefix", "%a|%d|");
        await Setting("log_statement", "all");
        await Sql(Control(), "SELECT pg_reload_conf()");
    }
    private static async Task Sql(string connectionString, string sql)
    { await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(); await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync(); }
    private async Task<string> Snapshot(Pair pair)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Control()) { Database = pair.Database }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_array(
              (SELECT jsonb_build_array(oid, datdba, datacl::text) FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database()),
              (SELECT to_jsonb(r) FROM pg_catalog.pg_roles r WHERE rolname = $1),
              (SELECT jsonb_build_array(oid, nspowner, nspacl::text) FROM pg_catalog.pg_namespace WHERE nspname = 'public'),
              (SELECT jsonb_agg(jsonb_build_array(c.oid, c.relname, c.relowner, c.relacl::text) ORDER BY c.relname)
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname IN ('fixture_data','fixture_sequence','fixture_data_pkey')),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM public.fixture_data t),
              (SELECT last_value FROM public.fixture_sequence))::text
            """, connection);
        command.Parameters.AddWithValue(pair.Login);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static ServiceProvider Services(string connection, IPostgreSqlImportFaultHooks? hooks = null, PostgreSqlProviderTransport? transport = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(TimeProvider.System).AddSingleton(PostgreSqlConnectionRendering.CreateCatalog())
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore().AddPostgreSqlProviderConfigurationStore()
            .AddPostgreSqlProviderAliasStore().AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec().AddPostgreSqlProviderRevisionPreflight().AddPostgreSqlDurableDesiredStateCommitter()
            .AddPostgreSqlDurableOperationJournal().AddPostgreSqlDurableOperationExecution().AddPostgreSqlResourceImportAcceptance()
            .AddPostgreSqlExistingPairImport();
        if (hooks is not null) services.AddSingleton(hooks);
        if (transport is not null) services.AddSingleton(transport);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private sealed class InterruptMarkers : IPostgreSqlImportFaultHooks
    {
        public ValueTask AfterMarkerAsync(int confirmedMarkers, CancellationToken token) => throw new IOException("Synthetic import marker interruption.");
    }
    private sealed class LoseAcceptanceResponse : IPostgreSqlImportFaultHooks
    {
        public ValueTask AfterAcceptanceAsync(CancellationToken token) => throw new IOException("Synthetic import response loss.");
    }

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        if (resetAdministratorSearchPath)
            try { await Sql(Control(), "ALTER ROLE postgres RESET search_path"); } catch (Exception e) { failures.Add(e); }
        foreach (var setting in originalSettings)
            try { await Sql(Control(), "ALTER SYSTEM SET " + setting.Key + " = '" + setting.Value.Replace("'", "''", StringComparison.Ordinal) + "'"); }
            catch (Exception e) { failures.Add(e); }
        if (originalSettings.Count != 0)
            try { await Sql(Control(), "SELECT pg_reload_conf()"); } catch (Exception e) { failures.Add(e); }
        foreach (var file in remoteTlsFiles)
            try
            {
                if (!file.StartsWith("/tmp/haby-import-", StringComparison.Ordinal) || file.Contains("..", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid owned TLS fixture path.");
                await fixture.RunClientUtilityAsync("rm", "-f", file);
            }
            catch (Exception e) { failures.Add(e); }
        foreach (var directory in tlsDirectories)
            try
            {
                var path = Path.GetFullPath(directory);
                var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(path).StartsWith("haby-import-tls-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid owned TLS fixture directory.");
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e) { failures.Add(e); }
        foreach (var database in databases.AsEnumerable().Reverse())
            try { await Sql(Control(), $"DROP DATABASE IF EXISTS \"{database}\""); } catch (Exception e) { failures.Add(e); }
        foreach (var role in roles.AsEnumerable().Reverse())
            try { await Sql(Control(), $"DROP ROLE IF EXISTS \"{role}\""); } catch (Exception e) { failures.Add(e); }
        if (failures.Count != 0) throw new AggregateException("Owned import fixture cleanup failed.", failures);
    }
}
