using Hamstix.Haby.Shared.PluginsCore;
using Hamstix.Haby.Shared.PluginsCore.Exceptions;
using Npgsql;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.PostgreSql12
{
    public class PostgreSql12Strategy : IStrategy
    {
        const string CuUsernameKey = "username";
        const string CuPasswordKey = "password";
        const string CuDatabaseKey = "database";

        const string ServiceRootUserKey = "rootUser";
        const string ServiceRootPasswordKey = "rootPassword";
        const string ServiceRootDatabaseKey = "rootDatabase";
        const string ServiceHostKey = "host";
        const string ServicePortKey = "port";
        const string ServiceSslModeKey = "sslMode";
        const string ServiceTrustServerCertificateKey = "trustServerCertificate";
        const string ServiceSslCertificateKey = "sslCertificate";
        const string ServiceSslKeyKey = "sslKey";
        const string ServiceSslPasswordKey = "sslPassword";
        const string ServiceRootCertificateKey = "rootCertificate";
        const string ServiceCheckCertificateRevocationKey = "checkCertificateRevocation";
        const string ServiceIntegratedSecurityKey = "integratedSecurity";
        const string ServiceKerberosServiceNameKey = "kerberosServiceName";
        const string ServiceTimeoutKey = "timeout";

        public async Task Configure(Service service, JsonNode renderedTemplate, JsonObject variables, CancellationToken cancellationToken)
        {
            CheckRequiredVariables(variables);

            await CreateUserAndDatabase(service, variables, cancellationToken);
        }

        public async Task UnConfigure(Service service, JsonNode renderedTemplate, JsonObject variables, CancellationToken cancellationToken)
        {
            CheckRequiredVariables(variables);

            await DropUserAndDatabase(service, variables, cancellationToken);
        }

        static void CheckRequiredVariables(JsonObject variables)
        {
            if (!variables.ContainsKey(CuUsernameKey))
                throw new ConfiguratorException($"Can't find variable {CuUsernameKey}.");
            if (!variables.ContainsKey(CuPasswordKey))
                throw new ConfiguratorException($"Can't find variable {CuPasswordKey}.");
            if (!variables.ContainsKey(CuDatabaseKey))
                throw new ConfiguratorException($"Can't find variable {CuDatabaseKey}.");
        }

        /// <summary>
        /// Create the new user, password, database and claim owner grants to the user.
        /// </summary>
        /// <param name="service">Service.</param>
        /// <param name="variables">The variables from the configurator.</param>
        /// <param name="cancellationToken">CancellationToken.</param>
        /// <returns></returns>
        async Task CreateUserAndDatabase(Service service, JsonObject variables, CancellationToken cancellationToken)
        {
            var userName = variables[CuUsernameKey]!.GetValue<string>();
            var databaseName = variables[CuDatabaseKey]!.GetValue<string>();
            var password = variables[CuPasswordKey]!.GetValue<string>();
            var rootBuilder = BuildRootConnectionString(service, variables);
            var rootUser = variables[ServiceRootUserKey]?.GetValue<string>();

            // Create connection to database server.
            using (var connection = new NpgsqlConnection(rootBuilder.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);

                // Create the user if not exists.
                var createCommand = connection.CreateCommand();
                createCommand.CommandText = $"SELECT 1 FROM pg_roles WHERE rolname = '{userName}';";
                var result = await createCommand.ExecuteScalarAsync(cancellationToken);
                if (result is null)
                {
                    createCommand = connection.CreateCommand();
                    createCommand.CommandText = $"CREATE ROLE {userName} WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE ENCRYPTED PASSWORD '{password}';";
                    await createCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                // Create database.
                createCommand = connection.CreateCommand();
                createCommand.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{databaseName}';";
                result = await createCommand.ExecuteScalarAsync(cancellationToken);
                if (result is null)
                {
                    createCommand = connection.CreateCommand();
                    createCommand.CommandText = $"CREATE DATABASE {databaseName} WITH ENCODING='UTF-8';";
                    await createCommand.ExecuteNonQueryAsync(cancellationToken);

                    createCommand = connection.CreateCommand();
                    createCommand.CommandText = $"GRANT \"{userName}\" TO {rootUser};";
                    await createCommand.ExecuteNonQueryAsync(cancellationToken);

                    createCommand = connection.CreateCommand();
                    createCommand.CommandText = $"ALTER DATABASE {databaseName} OWNER TO {userName};";
                    await createCommand.ExecuteNonQueryAsync(cancellationToken);

                    // Grant database owner to user.
                    createCommand = connection.CreateCommand();
                    createCommand.CommandText = $"GRANT ALL PRIVILEGES ON DATABASE {databaseName} TO {userName};";
                    await createCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await connection.CloseAsync();
            }
        }

        async Task DropUserAndDatabase(Service service, JsonObject variables, CancellationToken cancellationToken)
        {
            var userName = variables[CuUsernameKey]!.GetValue<string>();
            var databaseName = variables[CuDatabaseKey]!.GetValue<string>();
            var rootBuilder = BuildRootConnectionString(service, variables);

            var userBuilder = BuildUserConnectionString(service, variables);

            // Create connection to database server.
            // Connection string from the user.
            using (var connection = new NpgsqlConnection(userBuilder.ConnectionString))
            {
                // TODO: Drop opened sessions.
                await connection.OpenAsync(cancellationToken);

                // Drop the database.
                var createCommand = connection.CreateCommand();
                createCommand.CommandText = $"DROP DATABASE {databaseName};";
                await createCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            // Connection string from the management.
            using (var connection = new NpgsqlConnection(rootBuilder.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                // Drop the role.
                var createCommand = connection.CreateCommand();
                createCommand.CommandText = $"DROP ROLE {userName};";
                await createCommand.ExecuteNonQueryAsync(cancellationToken);

                await connection.CloseAsync();
            }
        }

        static NpgsqlConnectionStringBuilder BuildRootConnectionString(Service service, JsonObject variables)
        {
            var rootUser = variables[ServiceRootUserKey]?.GetValue<string>();
            var rootPassword = variables[ServiceRootPasswordKey]?.GetValue<string>();
            var host = variables[ServiceHostKey]?.GetValue<string>();

            if (rootUser is null || rootPassword is null || host is null)
                throw new ConfiguratorException($"The service \"{service.Name}\" must contains at least" +
                    $"\"{ServiceRootUserKey}\", \"{ServiceRootPasswordKey}\", \"{ServiceHostKey}\" " +
                    $"keys in the Json configuration.");

            var port = service.JsonConfig[ServicePortKey]?.GetValue<int>() ?? 5432;
            var rootDatabase = service.JsonConfig[ServiceRootDatabaseKey]?.GetValue<string>() ?? "postgres";
            var sslMode = service.JsonConfig[ServiceSslModeKey]?.GetValue<SslMode>();
            var trustServerCertificate = service.JsonConfig[ServiceTrustServerCertificateKey]?.GetValue<bool>();
            var sslCertificate = service.JsonConfig[ServiceSslCertificateKey]?.GetValue<string>();
            var sslKey = service.JsonConfig[ServiceSslKeyKey]?.GetValue<string>();
            var sslPassword = service.JsonConfig[ServiceSslPasswordKey]?.GetValue<string>();
            var rootCertificate = service.JsonConfig[ServiceRootCertificateKey]?.GetValue<string>();
            var checkCertificateRevocation = service.JsonConfig[ServiceCheckCertificateRevocationKey]?.GetValue<bool>();
            var integratedSecurity = service.JsonConfig[ServiceIntegratedSecurityKey]?.GetValue<bool>();
            var kerberosServiceName = service.JsonConfig[ServiceKerberosServiceNameKey]?.GetValue<string>();
            var timeout = service.JsonConfig[ServiceTimeoutKey]?.GetValue<int>();

            var rootBuilder = new NpgsqlConnectionStringBuilder();
            rootBuilder.Username = rootUser;
            rootBuilder.Password = rootPassword;
            rootBuilder.Host = host;
            rootBuilder.Port = port;
            rootBuilder.Database = rootDatabase;
            if (sslMode is not null)
                rootBuilder.SslMode = sslMode.Value;
            if (trustServerCertificate is not null)
                rootBuilder.TrustServerCertificate = trustServerCertificate.Value;
            if (sslCertificate is not null)
                rootBuilder.SslCertificate = sslCertificate;
            if (sslKey is not null)
                rootBuilder.SslKey = sslKey;
            if (sslPassword is not null)
                rootBuilder.SslPassword = sslPassword;
            if (rootCertificate is not null)
                rootBuilder.RootCertificate = rootCertificate;
            if (checkCertificateRevocation is not null)
                rootBuilder.CheckCertificateRevocation = checkCertificateRevocation.Value;
            if (integratedSecurity is not null)
                rootBuilder.IntegratedSecurity = integratedSecurity.Value;
            if (kerberosServiceName is not null)
                rootBuilder.KerberosServiceName = kerberosServiceName;
            if (timeout is not null)
                rootBuilder.Timeout = timeout.Value;

            return rootBuilder;
        }

        static NpgsqlConnectionStringBuilder BuildUserConnectionString(Service service, JsonObject variables)
        {
            var rootUser = variables[ServiceRootUserKey]?.GetValue<string>();
            var rootPassword = variables[ServiceRootPasswordKey]?.GetValue<string>();
            var host = variables[ServiceHostKey]?.GetValue<string>();

            if (rootUser is null || rootPassword is null || host is null)
                throw new ConfiguratorException($"The service \"{service.Name}\" must contains at least" +
                    $"\"{ServiceRootUserKey}\", \"{ServiceRootPasswordKey}\", \"{ServiceHostKey}\" " +
                    $"keys in the Json configuration.");

            var port = service.JsonConfig[ServicePortKey]?.GetValue<int>() ?? 5432;
            var rootDatabase = service.JsonConfig[ServiceRootDatabaseKey]?.GetValue<string>() ?? "postgres";
            var sslMode = service.JsonConfig[ServiceSslModeKey]?.GetValue<SslMode>();
            var trustServerCertificate = service.JsonConfig[ServiceTrustServerCertificateKey]?.GetValue<bool>();
            var sslCertificate = service.JsonConfig[ServiceSslCertificateKey]?.GetValue<string>();
            var sslKey = service.JsonConfig[ServiceSslKeyKey]?.GetValue<string>();
            var sslPassword = service.JsonConfig[ServiceSslPasswordKey]?.GetValue<string>();
            var rootCertificate = service.JsonConfig[ServiceRootCertificateKey]?.GetValue<string>();
            var checkCertificateRevocation = service.JsonConfig[ServiceCheckCertificateRevocationKey]?.GetValue<bool>();
            var integratedSecurity = service.JsonConfig[ServiceIntegratedSecurityKey]?.GetValue<bool>();
            var kerberosServiceName = service.JsonConfig[ServiceKerberosServiceNameKey]?.GetValue<string>();
            var timeout = service.JsonConfig[ServiceTimeoutKey]?.GetValue<int>();

            var rootBuilder = new NpgsqlConnectionStringBuilder();
            rootBuilder.Username = rootUser;
            rootBuilder.Password = rootPassword;
            rootBuilder.Host = host;
            rootBuilder.Port = port;
            rootBuilder.Database = rootDatabase;
            if (sslMode is not null)
                rootBuilder.SslMode = sslMode.Value;
            if (trustServerCertificate is not null)
                rootBuilder.TrustServerCertificate = trustServerCertificate.Value;
            if (sslCertificate is not null)
                rootBuilder.SslCertificate = sslCertificate;
            if (sslKey is not null)
                rootBuilder.SslKey = sslKey;
            if (sslPassword is not null)
                rootBuilder.SslPassword = sslPassword;
            if (rootCertificate is not null)
                rootBuilder.RootCertificate = rootCertificate;
            if (checkCertificateRevocation is not null)
                rootBuilder.CheckCertificateRevocation = checkCertificateRevocation.Value;
            if (integratedSecurity is not null)
                rootBuilder.IntegratedSecurity = integratedSecurity.Value;
            if (kerberosServiceName is not null)
                rootBuilder.KerberosServiceName = kerberosServiceName;
            if (timeout is not null)
                rootBuilder.Timeout = timeout.Value;

            return rootBuilder;
        }
    }
}
