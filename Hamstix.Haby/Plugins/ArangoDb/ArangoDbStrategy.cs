using Hamstix.Haby.Shared.PluginsCore;
using Hamstix.Haby.Shared.PluginsCore.Exceptions;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.ArangoDb
{
    public class ArangoDbStrategy : IStrategy
    {
        const string CuUsernameKey = "username";
        const string CuPasswordKey = "password";
        const string CuDatabaseKey = "database";

        const string ServiceRootUserKey = "rootUser";
        const string ServiceRootPasswordKey = "rootPassword";
        const string ServiceHostKey = "host";

        const string ServiceDisableSslVerificationKey = "disableSslVerification";

        readonly IHttpClientFactory _httpClientFactory;

        public ArangoDbStrategy(IHttpClientFactory httpClientFactory)
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
        /// Create the new user, password, database and claim owner grants to the user.
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

            var rootUser = variables[ServiceRootUserKey]!.GetValue<string>();
            var rootPassword = variables[ServiceRootPasswordKey]!.GetValue<string>();
            var host = variables[ServiceHostKey]!.GetValue<string>();
            var disableSslVerification = variables[ServiceDisableSslVerificationKey]?.GetValue<bool>() ?? false;

            var client = CreateClient(rootUser, rootPassword, host, disableSslVerification);

            var createDatabaseModel = new
            {
                name = databaseName,
                users = new[] { new
                    { username = userName, passwd = password }
                }
            };

            var response = await client.PostAsJsonAsync("_api/database", createDatabaseModel);
            if (!response.IsSuccessStatusCode)
                throw new ConfiguratorException($"Error while creating user and database at ArangoDb server. " +
                    $"StatusCode: {response.StatusCode}. Response: {await response.Content.ReadAsStringAsync()}");
        }

        HttpClient CreateClient(string rootUser, string rootPassword, string host, bool ignoreCertificate)
        {
            HttpClient client;
            if (ignoreCertificate)
                client = _httpClientFactory.CreateClient(Hamstix.Haby.Shared.PluginsCore.Constants.DisableSslVerification);
            else
                client = _httpClientFactory.CreateClient();

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{rootUser}:{rootPassword}")));

            client.BaseAddress = new Uri(host);
            return client;
        }

        async Task DropUserAndDatabase(Service service, JsonObject variables, CancellationToken cancellationToken)
        {
            var userName = variables[CuUsernameKey]!.GetValue<string>();
            var databaseName = variables[CuDatabaseKey]!.GetValue<string>();

            var rootUser = variables[ServiceRootUserKey]!.GetValue<string>();
            var rootPassword = variables[ServiceRootPasswordKey]!.GetValue<string>();
            var host = variables[ServiceHostKey]!.GetValue<string>();
            var disableSslVerification = variables[ServiceDisableSslVerificationKey]?.GetValue<bool>() ?? false;

            var client = CreateClient(rootUser, rootPassword, host, disableSslVerification);

            var response = await client.DeleteAsync($"_api/database/{databaseName}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ConfiguratorException($"Error while deleting database {databaseName} at ArangoDb server. " +
                    $"StatusCode: {response.StatusCode}. Response: {await response.Content.ReadAsStringAsync()}");

            response = await client.DeleteAsync($"_api/user/{userName}");
            if (!response.IsSuccessStatusCode)
                throw new ConfiguratorException($"Error while deleting user {userName} at ArangoDb server. " +
                    $"StatusCode: {response.StatusCode}. Response: {await response.Content.ReadAsStringAsync()}");
        }
    }
}
