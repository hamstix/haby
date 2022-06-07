using ClickHouse.Client.ADO;
using Dapper;
using Hamstix.Haby.Shared.PluginsCore;
using Hamstix.Haby.Shared.PluginsCore.Exceptions;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.ClickHouseHttp
{
    public class ClickHouseStrategy : IStrategy
    {
        const string CuUsernameKey = "username";
        const string CuPasswordKey = "password";
        const string CuDatabaseKey = "database";
        const string CuPrivilegeKey = "privilege";

        const string ServiceRootUserKey = "rootUser";
        const string ServiceRootPasswordKey = "rootPassword";
        const string ServiceHostKey = "host";

        const string ServiceDisableSslVerificationKey = "disableSslVerification";

        readonly IHttpClientFactory _httpClientFactory;

        public ClickHouseStrategy(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

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

            if (!variables.ContainsKey(ServiceRootUserKey))
                throw new ConfiguratorException($"Can't find variable {ServiceRootUserKey}.");
            if (!variables.ContainsKey(ServiceRootPasswordKey))
                throw new ConfiguratorException($"Can't find variable {ServiceRootPasswordKey}.");
            if (!variables.ContainsKey(ServiceHostKey))
                throw new ConfiguratorException($"Can't find variable {ServiceHostKey}.");

            var host = variables[ServiceHostKey]!.GetValue<string>();
            if (!Uri.TryCreate(host, default(UriCreationOptions), out var _))
                throw new ConfiguratorException($"Can't convert host value {host} to uri.");
        }

        /// <summary>
        /// Create the new user, password, database and claim grants to the user.
        /// </summary>
        /// <param name="service">Service.</param>
        /// <param name="variables">The variables from the configurator.</param>
        /// <param name="cancellationToken">CancellationToken.</param>
        /// <returns></returns>
        async Task CreateUserAndDatabase(Service service, JsonObject variables, CancellationToken cancellationToken)
        {
            var userName = variables[CuUsernameKey]!.GetValue<string>();
            var password = variables[CuPasswordKey]!.GetValue<string>();
            var databaseName = variables[CuDatabaseKey]!.GetValue<string>();
            var privilege = variables[CuPrivilegeKey]!.GetValue<string>() ?? "ALL";

            var rootUser = variables[ServiceRootUserKey]!.GetValue<string>();
            var rootPassword = variables[ServiceRootPasswordKey]!.GetValue<string>();
            var host = variables[ServiceHostKey]!.GetValue<string>();
            var disableSslVerification = variables[ServiceDisableSslVerificationKey]?.GetValue<bool>() ?? false;

            string httpClientName;
            if (disableSslVerification)
                httpClientName = Constants.DisableSslVerification;
            else
                httpClientName = string.Empty;

            using var connection = new ClickHouseConnection(
                $"Host={host};Username={rootUser};Password={rootPassword};Database=system", _httpClientFactory, httpClientName);

            await CreateUser(userName, password, connection);
            await CreateDatabase(databaseName, connection);
            await GrantRole(userName, databaseName, privilege, connection);
        }

        static async Task CreateUser(string userName, string password, ClickHouseConnection connection)
        {
            string sql = "CREATE USER IF NOT EXISTS @UserName IDENTIFIED WITH sha256_password BY '@Password'";
            var parameters = new { UserName = userName, Password = password };
            await connection.ExecuteAsync(sql, parameters);
        }

        static async Task CreateDatabase(string database, ClickHouseConnection connection)
        {
            string sql = "CREATE DATABASE IF NOT EXISTS @Database";
            var parameters = new { Database = database };
            await connection.ExecuteAsync(sql, parameters);
        }

        static async Task GrantRole(string userName, string database, string privilege, ClickHouseConnection connection)
        {
            string sql = "GRANT @Privilige ON @Database TO @UserName";
            var parameters = new { Privilige = privilege, Database = database, UserName = userName };
            await connection.ExecuteAsync(sql, parameters);
        }

        async Task DropUserAndDatabase(Service service, JsonObject variables, CancellationToken cancellationToken)
        {
            var userName = variables[CuUsernameKey]!.GetValue<string>();
            var databaseName = variables[CuDatabaseKey]!.GetValue<string>();

            var rootUser = variables[ServiceRootUserKey]!.GetValue<string>();
            var rootPassword = variables[ServiceRootPasswordKey]!.GetValue<string>();
            var host = variables[ServiceHostKey]!.GetValue<string>();
            var disableSslVerification = variables[ServiceDisableSslVerificationKey]?.GetValue<bool>() ?? false;

            string httpClientName;
            if (disableSslVerification)
                httpClientName = Constants.DisableSslVerification;
            else
                httpClientName = string.Empty;

            using var connection = new ClickHouseConnection(
                $"Host={host};Username={rootUser};Password={rootPassword};Database=system", _httpClientFactory, httpClientName);
            await DropDatabase(databaseName, connection);
            await DropUser(userName, connection);
        }

        static async Task DropDatabase(string databaseName, ClickHouseConnection connection)
        {
            string sql = "DROP DATABASE @Database";
            var parameters = new { Database = databaseName };
            await connection.ExecuteAsync(sql, parameters);
        }

        static async Task DropUser(string username, ClickHouseConnection connection)
        {
            string sql = "DROP USER IF EXISTS @UserName";
            var parameters = new { UserName = username };
            await connection.ExecuteAsync(sql, parameters);
        }
    }
}
