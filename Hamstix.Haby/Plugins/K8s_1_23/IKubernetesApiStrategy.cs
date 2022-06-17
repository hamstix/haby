using k8s;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.K8s_1_23
{
    public interface IKubernetesHandler
    {
        Task CreateResource(Kubernetes client,
            string resource,
            JsonObject variables,
            JsonObject resourceTemplate,
            CancellationToken cancellationToken);

        Task DropResource(Kubernetes client, 
            string resource, 
            string @namespace, 
            CancellationToken token);
    }
}
