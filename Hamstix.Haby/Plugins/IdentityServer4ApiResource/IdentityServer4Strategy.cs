using Hamstix.Haby.Shared.PluginsCore;
using Hamstix.Haby.Shared.PluginsCore.Exceptions;
using IdentityServer4.EntityFramework.DbContexts;
using IdentityServer4.EntityFramework.Entities;
using IdentityServer4.EntityFramework.Options;
using IdentityServer4.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.IdentityServer4EFApiResource
{
    public class IdentityServer4Strategy : IStrategy
    {
        const string CuResourceKey = "resource";
        const string CuClientPasswordKey = "clientPassword";
        const string CuApiResourcePasswordKey = "apiResourcePassword";

        const string ServiceClientKey = "client";
        const string ServiceApiResourceKey = "apiResource";

        const string ServiceClientEnabledKey = "enabled";
        const string ServiceClientLoginKey = "login";

        const string ServiceApiResourceEnabledKey = "enabled";
        const string ServiceApiResourceLoginKey = "login";

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

            await AddSecrets(service, variables, cancellationToken);
        }

        public async Task UnConfigure(Service service, JsonNode renderedTemplate, JsonObject variables, CancellationToken cancellationToken)
        {
            CheckRequiredVariables(variables);

            await RemoveSecrets(service, variables, cancellationToken);
        }

        static void CheckRequiredVariables(JsonObject variables)
        {
            if (!variables.ContainsKey(CuResourceKey))
                throw new ConfiguratorException($"Can't find variable {CuResourceKey}.");

            if (!variables.ContainsKey(ServiceRootUserKey))
                throw new ConfiguratorException($"Can't find variable {ServiceRootUserKey}.");
            if (!variables.ContainsKey(ServiceRootPasswordKey))
                throw new ConfiguratorException($"Can't find variable {ServiceRootPasswordKey}.");
            if (!variables.ContainsKey(ServiceHostKey))
                throw new ConfiguratorException($"Can't find variable {ServiceHostKey}.");
        }

        async Task AddSecrets(Service service, JsonObject variables, CancellationToken cancellationToken)
        {
            var resourceName = variables[CuResourceKey]!.GetValue<string>();

            var rootBuilder = BuildRootConnectionString(service, variables);

            var optionsBuilder = new DbContextOptionsBuilder<ConfigurationDbContext>()
                .UseNpgsql(rootBuilder.ConnectionString);
            var configurationStore = new ConfigurationStoreOptions();

            using var context = new ConfigurationDbContext(optionsBuilder.Options, configurationStore);

            if (variables.ContainsKey(ServiceClientKey))
                await ConfigureClientAsync(context, variables, resourceName);

            if (variables.ContainsKey(ServiceApiResourceKey))
                await ConfigureApiResourceAsync(context, variables, resourceName);

            await context.SaveChangesAsync(cancellationToken);
        }

        async Task RemoveSecrets(Service service, JsonObject variables, CancellationToken cancellationToken)
        {
            var resourceName = variables[CuResourceKey]!.GetValue<string>();

            var rootBuilder = BuildRootConnectionString(service, variables);

            var optionsBuilder = new DbContextOptionsBuilder<ConfigurationDbContext>()
                .UseNpgsql(rootBuilder.ConnectionString);
            var configurationStore = new ConfigurationStoreOptions();

            using var context = new ConfigurationDbContext(optionsBuilder.Options, configurationStore);

            if (variables.ContainsKey(ServiceClientKey))
                await RemoveClientAsync(context, variables, resourceName);

            if (variables.ContainsKey(ServiceApiResourceKey))
                await RemoveApiResourceAsync(context, variables, resourceName);

            await context.SaveChangesAsync(cancellationToken);
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

        async Task ConfigureClientAsync(ConfigurationDbContext context, JsonObject variables, string resourceName)
        {
            var enabled = variables[ServiceClientKey]?[ServiceClientEnabledKey]?.GetValue<bool>() ?? false;
            if (!enabled)
                return;

            var login = variables[ServiceClientKey]?[ServiceClientLoginKey]?.GetValue<string>();
            if (string.IsNullOrEmpty(login))
                throw new ConfiguratorException($"Can't configure \"Client\" because {ServiceClientKey}.{ServiceClientLoginKey} variable is not set.");

            var password = variables[CuClientPasswordKey]?.GetValue<string>();
            if (string.IsNullOrEmpty(password))
                throw new ConfiguratorException($"Can't configure \"Client\" because {CuClientPasswordKey} variable is not set.");

            var client = await context.Clients
                .Include(x => x.ClientSecrets)
                .FirstOrDefaultAsync(x => x.ClientId == login);

            if (client is not null)
            {
                var secret = new ClientSecret { Description = resourceName, Type = "SharedSecret", Value = password.Sha256() };
                client.ClientSecrets.Add(secret);
            }
        }

        async Task ConfigureApiResourceAsync(ConfigurationDbContext context, JsonObject variables, string resourceName)
        {
            var enabled = variables[ServiceApiResourceKey]?[ServiceApiResourceEnabledKey]?.GetValue<bool>() ?? false;
            if (!enabled)
                return;

            var login = variables[ServiceApiResourceKey]?[ServiceApiResourceLoginKey]?.GetValue<string>();
            if (string.IsNullOrEmpty(login))
                throw new ConfiguratorException($"Can't configure \"ApiResource\" because {ServiceApiResourceKey}.{ServiceApiResourceLoginKey} variable is not set.");

            var password = variables[CuApiResourcePasswordKey]?.GetValue<string>();
            if (string.IsNullOrEmpty(password))
                throw new ConfiguratorException($"Can't configure \"ApiResource\" because {CuApiResourcePasswordKey} variable is not set.");

            var apiResource = await context.ApiResources
                .Include(x => x.Secrets)
                .FirstOrDefaultAsync(x => x.Name == login);

            if (apiResource is not null)
            {
                var secret = new ApiResourceSecret { Description = resourceName, Type = "SharedSecret", Value = password.Sha256() };
                apiResource.Secrets.Add(secret);
            }
        }

        async Task RemoveClientAsync(ConfigurationDbContext context, JsonObject variables, string resourceName)
        {
            var enabled = variables[ServiceClientKey]?[ServiceClientEnabledKey]?.GetValue<bool>() ?? false;
            if (!enabled)
                return;

            var login = variables[ServiceClientKey]?[ServiceClientLoginKey]?.GetValue<string>();
            if (string.IsNullOrEmpty(login))
                return;

            var client = await context.Clients
                .Include(x => x.ClientSecrets)
                .FirstOrDefaultAsync(x => x.ClientId == login);

            if (client is not null)
            {
                var сlientSecret = client
                    .ClientSecrets
                    .FirstOrDefault(x => x.Description == resourceName && x.Type == "SharedSecret");
                if (сlientSecret is not null)
                    client.ClientSecrets.Remove(сlientSecret);
            }
        }

        async Task RemoveApiResourceAsync(ConfigurationDbContext context, JsonObject variables, string resourceName)
        {
            var enabled = variables[ServiceApiResourceKey]?[ServiceApiResourceEnabledKey]?.GetValue<bool>() ?? false;
            if (!enabled)
                return;

            var login = variables[ServiceApiResourceKey]?[ServiceApiResourceLoginKey]?.GetValue<string>();
            if (string.IsNullOrEmpty(login))
                return;

            var apiResource = await context.ApiResources
                .Include(x => x.Secrets)
                .FirstOrDefaultAsync(x => x.Name == login);

            if (apiResource is not null)
            {
                var apiSecret = apiResource
                    .Secrets
                    .FirstOrDefault(x => x.Description == resourceName && x.Type == "SharedSecret");
                if (apiSecret is not null)
                    apiResource.Secrets.Remove(apiSecret);
            }
        }
    }
}
