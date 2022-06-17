using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Hamstix.Haby.Plugins.K8s_1_23.Handlers
{
    public class KubernetesDeploymentHandler : KubernetesHandlerBase
    {
        public KubernetesDeploymentHandler(ILogger<KubernetesHandlerBase> log) : base(log)
        {

        }

        protected override Task CreateEntity(Kubernetes client,
            string resource,
            string body,
            string @namespace,
            CancellationToken token)
        {
            var entity = KubernetesJson.Deserialize<V1Deployment>(body);
            return client.CreateNamespacedDeploymentAsync(entity, @namespace, cancellationToken: token);
        }

        protected override async Task<IKubernetesObject?> GetExistedEntity(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token) =>
            await client.ReadNamespacedDeploymentAsync(resource, @namespace, cancellationToken: token);

        protected override Task ReplaceEntity(Kubernetes client,
            string resource,
            string body,
            string @namespace,
            CancellationToken token)
        {
            var entity = KubernetesJson.Deserialize<V1Deployment>(body);
            return client.ReplaceNamespacedDeploymentAsync(entity,
                resource,
                @namespace,
                cancellationToken: token);
        }

        protected override Task DeleteEntity(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token) =>
            client.DeleteNamespacedDeploymentAsync(resource, @namespace, cancellationToken: token);
    }
}
