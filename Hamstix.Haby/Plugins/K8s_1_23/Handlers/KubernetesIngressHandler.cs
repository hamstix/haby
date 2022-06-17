using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Hamstix.Haby.Plugins.K8s_1_23.Handlers
{
    public class KubernetesIngressHandler : KubernetesHandlerBase
    {
        public KubernetesIngressHandler(ILogger<KubernetesHandlerBase> log) : base(log)
        {

        }

        protected override Task CreateEntity(Kubernetes client,
            string resource,
            string body,
            string @namespace,
            CancellationToken token)
        {
            var entity = KubernetesJson.Deserialize<V1Ingress>(body);
            return client.CreateNamespacedIngressAsync(entity, @namespace, cancellationToken: token);
        }

        protected override async Task<IKubernetesObject?> GetExistedEntity(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token) =>
            await client.ReadNamespacedIngressAsync(resource, @namespace, cancellationToken: token);

        protected override Task ReplaceEntity(Kubernetes client,
            string resource,
            string body,
            string @namespace,
            CancellationToken token)
        {
            var deployment = KubernetesJson.Deserialize<V1Ingress>(body);
            return client.ReplaceNamespacedIngressAsync(deployment,
                resource,
                @namespace,
                cancellationToken: token);
        }

        protected override Task DeleteEntity(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token) =>
            client.DeleteNamespacedIngressAsync(resource, @namespace, cancellationToken: token);
    }
}
