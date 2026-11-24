using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;

namespace Haby.Operator.Host;

/// <summary>Trusted additive build-time metadata; stock catalogs remain the sole engine snapshots.</summary>
internal static class HostCatalogComposition
{
    internal static void Register(IServiceCollection services)
    {
        services.AddSingleton(PostgreSqlDatabaseContract.Definition);
        services.AddSingleton(PostgreSqlApplicationLoginContract.Definition);
        services.AddSingleton(Haby.Kubernetes.Contracts.KubernetesContract.Definition);
        services.AddSingleton(new OutputContractDefinition(OutputContractId.Create(PostgreSqlApplicationConnectionContract.Contract), PostgreSqlConsumerOptions.Schema));
        services.AddSingleton<ConfigurationRendererContract>(new PostgreSqlConnectionRendererContract());
        services.AddSingleton<ConfigurationFormatterContract>(new PostgreSqlConnectionFormatterContract());
        services.AddSingleton(new ConfigurationFormatterImplementation(PostgreSqlConnectionRendering.Implementation, PostgreSqlConnectionRendering.Formatter));
        services.AddSingleton<IConfigurationRenderer, PostgreSqlConnectionRenderer>();
        services.AddSingleton<IConfigurationFormatter, PostgreSqlConnectionFormatter>();
        services.AddSingleton(p => new ManifestContractCatalog(p.GetServices<ResourceTypeDefinition>(), p.GetServices<WorkloadTypeDefinition>(),
            p.GetServices<OutputContractDefinition>(), p.GetServices<ConfigurationRendererContract>().Select(x => new ConfigurationRendererDefinition(x.Id))));
        services.AddSingleton(p => new ConfigurationFormatterCatalog(p.GetServices<ConfigurationFormatterContract>(), p.GetServices<ConfigurationFormatterImplementation>()));
        services.AddSingleton(p => new ConfigurationRenderingCatalog(p.GetServices<ConfigurationRendererContract>(), p.GetServices<IConfigurationRenderer>(),
            p.GetRequiredService<ConfigurationFormatterCatalog>(), p.GetServices<IConfigurationFormatter>()));
    }

    internal static void Validate(IServiceProvider services)
    {
        var manifest = services.GetRequiredService<ManifestContractCatalog>();
        _ = services.GetRequiredService<ConfigurationRenderingCatalog>();
        var registry = services.GetRequiredService<DurableActionRegistry>();
        var admissions = services.GetServices<IDurableActionAdmissionHandler>().ToArray();
        var providers = services.GetRequiredService<ProviderDeclarationRegistry>();
        var executors = services.GetServices<IDurableActionHandler>().ToArray();
        var evidence = services.GetServices<IExternalOperationOutputContract>().ToArray();
        bool Complete(DurableActionExecutionKey key) => manifest.TryGet(key.Admission.ResourceContract, out _) &&
            providers.Find(new(key.Admission.ProviderKind.Value, key.Admission.ConfigurationContract.Value, key.Admission.ImplementationId.Value)) is not null &&
            registry.Find(key.Admission)?.Key == key && registry.Find(key) is { } executor &&
            (key.Admission.Mode != ResourceAcquisitionMode.Reference || executor is IReadOnlyResourceObservationHandler &&
                executor.RequiresExternalOutput && executor.OutputIdentity is not null &&
                evidence.SingleOrDefault(x => x.Producer == key) is IResourceReferenceOutputContract output && output.Identity == executor.OutputIdentity);
        var references = admissions.Select(x => x.Key).Concat(executors.Select(x => x.Key)).Concat(evidence.Select(x => x.Producer))
            .Where(x => x.Admission.Mode == ResourceAcquisitionMode.Reference).Distinct();
        if (references.Any(key => !Complete(key)))
            throw new InvalidOperationException("Reference capability registration is incomplete.");
        // Only the current build's contributed capabilities are checked, never stored history.
        var contributed = services.GetServices<ResourceTypeDefinition>().Where(x =>
            x.Id != PostgreSqlDatabaseContract.Id && x.Id != PostgreSqlApplicationLoginContract.Id).ToArray();
        foreach (var resource in contributed)
        {
            var actions = admissions.Where(x => x.Key.Admission.ResourceContract == resource.Id).ToArray();
            if (actions.Length == 0 || actions.Any(x => !Complete(x.Key)))
                throw new InvalidOperationException("Contributed Resource capability is incomplete.");
        }
        var renderers = services.GetServices<IConfigurationRenderer>().ToArray();
        var formatters = services.GetServices<IConfigurationFormatter>().ToArray();
        var bindings = services.GetServices<IConfigurationResolutionBinding>().ToArray();
        foreach (var definition in services.GetServices<ConfigurationRendererContract>())
        {
            if (!manifest.TryGet(definition.Resource, out _) || !manifest.TryGet(OutputContractId.Create(definition.OutputContract), out _) ||
                renderers.Count(x => x.Contract == definition.Id) != 1 ||
                formatters.SingleOrDefault(x => x.Contract == definition.Formatter) is not { } formatter ||
                bindings.Count(x => x.Formatter == formatter.Contract && x.Implementation == formatter.Identity) != 1 ||
                registry.Find(definition.ProducerAction) is null)
                throw new InvalidOperationException("Configuration capability registration is incomplete.");
        }
    }
}
