using k8s;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.K8s_1_23
{
    public abstract class KubernetesHandlerBase : IKubernetesHandler
    {
        protected ILogger<KubernetesHandlerBase> Log { get; }

        protected KubernetesHandlerBase(ILogger<KubernetesHandlerBase> log)
        {
            Log = log;
        }

        public async Task CreateResource(Kubernetes client,
            string resource,
            JsonObject variables,
            JsonObject resourceTemplate,
            CancellationToken cancellationToken)
        {
            var @namespace = variables[K8s123MQStrategy.ServiceNamespaceKey]!.GetValue<string>();

            var body = resourceTemplate.ToJsonString();

            IKubernetesObject? existedEntity;
            try
            {
                existedEntity = await GetExistedEntity(client, resource, @namespace, cancellationToken);
            }
            catch(k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                existedEntity = null;
            }

            if (existedEntity is null)
                await CreateEntity(client, resource, body, @namespace, cancellationToken);
            else
                await ReplaceEntity(client, resource, body, @namespace, cancellationToken);
        }

        public async Task DropResource(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token)
        {
            var existedEntity = await GetExistedEntity(client, resource, @namespace, token);

            if (existedEntity is not null)
                await DeleteEntity(client, resource, @namespace, token);
        }

        protected abstract Task<IKubernetesObject?> GetExistedEntity(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token);

        protected abstract Task CreateEntity(Kubernetes client,
            string resource,
            string body,
            string @namespace,
            CancellationToken token);

        protected abstract Task ReplaceEntity(Kubernetes client,
            string resource,
            string body,
            string @namespace,
            CancellationToken token);

        protected abstract Task DeleteEntity(Kubernetes client,
            string resource,
            string @namespace,
            CancellationToken token);
    }
}
