using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Hamstix.Haby.Plugins.K8s_1_23.Handlers
{
    public class KubernetesServiceHandler : KubernetesHandlerBase
    {
        public KubernetesServiceHandler(ILogger<KubernetesHandlerBase> log) : base(log)
        {

        }

        protected override Task CreateEntity(Kubernetes client,
            string resource,
            string body,
            string @namespace,
            CancellationToken token)
        {
            var entity = KubernetesJson.Deserialize<V1Service>(body);
            return client.CreateNamespacedServiceAsync(entity, @namespace, cancellationToken: token);
        }

        protected override async Task<IKubernetesObject?> GetExistedEntity(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token) =>
            await client.ReadNamespacedServiceAsync(resource, @namespace, cancellationToken: token);

        protected override Task ReplaceEntity(Kubernetes client,
            string resource,
            string body,
            string @namespace,
            CancellationToken token)
        {
            var entity = KubernetesJson.Deserialize<V1Service>(body);
            return client.ReplaceNamespacedServiceAsync(entity,
                resource,
                @namespace,
                cancellationToken: token);
        }

        protected override Task DeleteEntity(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token) =>
            client.DeleteNamespacedServiceAsync(resource, @namespace, cancellationToken: token);
    }
}
