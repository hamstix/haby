using Hamstix.Haby.Shared.PluginsCore;
using Hamstix.Haby.Shared.PluginsCore.Exceptions;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.RabbitMQ
{
    public class RabbitMQStrategy : IStrategy
    {
        const string CuUsernameKey = "username";
        const string CuPasswordKey = "password";

        const string ServiceRootUserKey = "rootUser";
        const string ServiceRootPasswordKey = "rootPassword";
        const string ServiceHostKey = "host";
        const string ServiceDisableSslVerificationKey = "disableSslVerification";
        const string ServiceConfigureGrantKey = "configureGrant";
        const string ServiceWriteGrantKey = "writeGrant";
        const string ServiceReadGrantKey = "readGrant";

        readonly IHttpClientFactory _httpClientFactory;

        public RabbitMQStrategy(IHttpClientFactory httpClientFactory)
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

            var rootUser = variables[ServiceRootUserKey]!.GetValue<string>();
            var rootPassword = variables[ServiceRootPasswordKey]!.GetValue<string>();
            var host = variables[ServiceHostKey]!.GetValue<string>();
            var disableSslVerification = variables[ServiceDisableSslVerificationKey]?.GetValue<bool>() ?? false;

            var configureGrant = variables[ServiceConfigureGrantKey]!.GetValue<string>() ?? ".*";
            var writeGrant = variables[ServiceWriteGrantKey]!.GetValue<string>() ?? ".*";
            var readGrant = variables[ServiceReadGrantKey]!.GetValue<string>() ?? ".*";

            var client = CreateClient(rootUser, rootPassword, host, disableSslVerification);

            var createUserModel = new { password = password, tags = "none" };

            var response = await client.PutAsJsonAsync($"api/users/{userName}", createUserModel, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ConfiguratorException($"Error while creating user at RabbitMQ server. " +
                    $"StatusCode: {response.StatusCode}. Response: {await response.Content.ReadAsStringAsync()}");

            var setGrantsModel = new { configure = configureGrant, write = writeGrant, read = readGrant };
            response = await client.PutAsJsonAsync($"api/permissions/%2F/{userName}", setGrantsModel, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ConfiguratorException($"Error while setting grant to the user at RabbitMQ server. " +
                    $"StatusCode: {response.StatusCode}. Response: {await response.Content.ReadAsStringAsync()}");
        }

        HttpClient CreateClient(string rootUser, string rootPassword, string host, bool ignoreCertificate)
        {
            HttpClient client;
            if (ignoreCertificate)
                client = _httpClientFactory.CreateClient(Constants.DisableSslVerification);
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

            var rootUser = variables[ServiceRootUserKey]!.GetValue<string>();
            var rootPassword = variables[ServiceRootPasswordKey]!.GetValue<string>();
            var host = variables[ServiceHostKey]!.GetValue<string>();
            var disableSslVerification = variables[ServiceDisableSslVerificationKey]?.GetValue<bool>() ?? false;

            var client = CreateClient(rootUser, rootPassword, host, disableSslVerification);

            var response = await client.DeleteAsync($"api/users/{userName}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ConfiguratorException($"Error while deleting {userName} at RabbitMQ server. " +
                    $"StatusCode: {response.StatusCode}. Response: {await response.Content.ReadAsStringAsync()}");
        }
    }
}
