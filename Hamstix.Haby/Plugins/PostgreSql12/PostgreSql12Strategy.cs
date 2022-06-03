using Hamstix.Haby.Shared.PluginsCore;
using Hamstix.Haby.Shared.PluginsCore.Exceptions;
using Npgsql;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.PostgreSql12
{
    public class PostgreSql12Strategy : IStrategy
    {
        const string TmplDefaultConnectionKey = "DefaultConnection";
        const string TmplConnectionStringKey = "ConnectionString";

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

        public async Task Configure(Service service, JsonNode renderedTemplate, CancellationToken cancellationToken)
        {
            var connectionString = ExtractConnectionString(renderedTemplate);
            if (connectionString is null)
                throw new ConfiguratorException($"The rendered template " +
                    $"at service {service.Name} does not contain the key \"{TmplDefaultConnectionKey}\":\"{TmplConnectionStringKey}\"");

            var builder = new NpgsqlConnectionStringBuilder(connectionString);

            if (!CheckConnectionStringHasAllRequiredFields(builder))
                throw new ConfiguratorException($"The connection string from the rendered template " +
                    $"at service {service.Name} does not contain " +
                    $"required fields \"Database\" or \"Username\" or \"Password\".");

            await CreateUserAndDatabase(service, builder, cancellationToken);
        }

        public async Task UnConfigure(Service service, JsonNode renderedTemplate, CancellationToken cancellationToken)
        {
            var connectionString = ExtractConnectionString(renderedTemplate);
            if (connectionString is null)
                throw new ConfiguratorException($"The rendered template " +
                    $"at service {service.Name} does not contain the key \"{TmplDefaultConnectionKey}\":\"{TmplConnectionStringKey}\"");

            var builder = new NpgsqlConnectionStringBuilder(connectionString);

            if (!CheckConnectionStringHasAllRequiredFields(builder))
                throw new ConfiguratorException($"The connection string from the rendered template " +
                    $"at service {service.Name} does not contain " +
                    $"required fields \"Database\" or \"Username\" or \"Password\".");

            await DropUserAndDatabase(service, builder, cancellationToken);
        }

        string? ExtractConnectionString(JsonNode renderedTemplate) => 
            renderedTemplate[TmplDefaultConnectionKey]?[TmplConnectionStringKey]?.GetValue<string>();

        /// <summary>
        /// Checks that the login, password and database name fields are filled in.
        /// </summary>
        /// <returns></returns>
        public bool CheckConnectionStringHasAllRequiredFields(NpgsqlConnectionStringBuilder builder) => 
            !string.IsNullOrEmpty(builder.Database)
                && !string.IsNullOrEmpty(builder.Username)
                && !string.IsNullOrEmpty(builder.Password);

        /// <summary>
        /// Create the new user, password, database and claim owner grants to the user.
        /// </summary>
        /// <param name="service">Service.</param>
        /// <param name="userBuilder">The Npgsql builder with user data.</param>
        /// <param name="cancellationToken">CancellationToken.</param>
        /// <returns></returns>
        async Task CreateUserAndDatabase(Service service, NpgsqlConnectionStringBuilder userBuilder, CancellationToken cancellationToken)
        {
            var userName = userBuilder.Username;
            var databaseName = userBuilder.Database;
            var password = userBuilder.Password;
            var rootBuilder = BuildRootConnectionString(service);

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
                    createCommand.CommandText = $"CREATE DATABASE {databaseName} WITH ENCODING='UTF-8' OWNER={userName};";
                    await createCommand.ExecuteNonQueryAsync(cancellationToken);

                    // Grant database owner to user.
                    createCommand = connection.CreateCommand();
                    createCommand.CommandText = $"GRANT ALL PRIVILEGES ON DATABASE {databaseName} TO {userName};";
                    await createCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await connection.CloseAsync();
            }
        }

        async Task DropUserAndDatabase(Service service, NpgsqlConnectionStringBuilder userBuilder, CancellationToken cancellationToken)
        {
            var userName = userBuilder.Username;
            var databaseName = userBuilder.Database;
            var rootBuilder = BuildRootConnectionString(service);

            // Create connection to database server.
            using (var connection = new NpgsqlConnection(rootBuilder.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);

                // Drop the database.
                var createCommand = connection.CreateCommand();
                createCommand.CommandText = $"DROP DATABASE {databaseName};";
                await createCommand.ExecuteNonQueryAsync(cancellationToken);

                // Drop the role.
                createCommand = connection.CreateCommand();
                createCommand.CommandText = $"DROP ROLE {userName};";
                await createCommand.ExecuteNonQueryAsync(cancellationToken);

                await connection.CloseAsync();
            }
        }

        static NpgsqlConnectionStringBuilder BuildRootConnectionString(Service service)
        {
            var rootUser = service.JsonConfig[ServiceRootUserKey]?.GetValue<string>();
            var rootPassword = service.JsonConfig[ServiceRootPasswordKey]?.GetValue<string>();
            var host = service.JsonConfig[ServiceHostKey]?.GetValue<string>();

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
