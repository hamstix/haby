using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Tests;

internal static class DurableInputSamples
{
    internal static DurablePlanningInputs Create(EnvironmentRecordId environment, EnvironmentId environmentKey,
        ManifestRevisionRef revision, ProviderAlias alias, ProviderInstanceId instance,
        ProviderConfigurationRevisionNumber configuration, string implementation, params string[] resources)
    {
        var declarations = string.Join(",", resources.Select(id => $"\"{id}\":{{\"type\":\"sample.resource/v1\",\"providerRef\":\"{alias.Value}\",\"properties\":{{}}}}"));
        var json = "{\"applicationVersion\":\"1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"sample\"},\"spec\":{\"resources\":{" + declarations + "}}}}";
        var manifest = ((ManifestCandidateResult.Valid)ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes(json),
            new([new(ResourceTypeId.Create("sample.resource/v1"), "{}")], [], [], []))).Candidate.Declaration;
        return ForManifest(manifest, environment, environmentKey, revision, instance, configuration, implementation);
    }

    internal static DurablePlanningInputs ForManifest(NormalizedApplicationManifest manifest, EnvironmentRecordId environment,
        EnvironmentId environmentKey, ManifestRevisionRef revision, ProviderInstanceId instance,
        ProviderConfigurationRevisionNumber configuration, string implementation) => new(environment, environmentKey, revision,
        new(manifest, environment, manifest.Spec.Resources.Select(r => new ExactProviderResolution(new ProviderInput.Resource(r.Key),
            environment, r.Value.ProviderRef, instance, configuration, ProviderImplementationId.Create(implementation), ResourceNamingCodec.Exact(ResourceNamingSnapshot.Default)))));
}
