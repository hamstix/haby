using Haby.Application;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;

namespace Haby.Operator.Host;

internal static class RuntimeAuthenticationComposition
{
    internal const string Prefix = "Haby:RuntimeAuthentication:";
    internal static RuntimeAuthenticationOptions Read(IConfiguration configuration)
    {
        int Setting(string name, int fallback)
        {
            var value = configuration[Prefix + name];
            if (value is null) return fallback;
            if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                throw new ArgumentException("Invalid configuration key: " + Prefix + name);
            return parsed;
        }
        var options = new RuntimeAuthenticationOptions(Setting("GlobalConcurrency", 32), Setting("ProviderConcurrency", 16),
            Setting("GlobalRequestsPerSecond", 50), Setting("ProviderRequestsPerSecond", 25), Setting("QueueCapacity", 256),
            Setting("QueueWaitSeconds", 10), Setting("CallTimeoutSeconds", 5));
        options.Validate(); return options;
    }
    internal static void Add(IServiceCollection services, RuntimeAuthenticationOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(p => new RuntimeAuthenticationAdmission(options, p.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRuntimeAuthenticator>(p => new KubernetesRuntimeAuthenticator(p.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IRuntimeIdentityStore>(p =>
        {
            var contexts = p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            // Private metadata validation composition only. The executor is not registered or exposed.
            IInternalActionHandler[] handlers = [new InternalRuntimeIdentityAction(p.GetRequiredService<ISecretStore>())];
            var codecs = p.GetServices<IProviderConfigurationCodec>().ToArray();
            var outputs = p.GetServices<IExternalOperationOutputContract>().ToArray();
            var admission = p.GetRequiredService<DurableActionAdmission>();
            var origins = new InternalDurableActions(contexts, handlers, codecs, outputs, admission,
                p.GetRequiredService<DurableOperationSerialization>(), p.GetRequiredService<TimeProvider>());
            return new PostgreSqlRuntimeIdentityStore(contexts, origins);
        });
        services.AddSingleton(p => new RuntimeAuthentication(p.GetRequiredService<IRuntimeIdentityStore>(),
            p.GetServices<IRuntimeAuthenticator>(), p.GetRequiredService<RuntimeAuthenticationAdmission>(), p.GetRequiredService<TimeProvider>()));
    }
}
