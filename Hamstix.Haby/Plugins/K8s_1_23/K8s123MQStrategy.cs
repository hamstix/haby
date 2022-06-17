using Hamstix.Haby.Plugins.K8s_1_23.Handlers;
using Hamstix.Haby.Shared.PluginsCore;
using Hamstix.Haby.Shared.PluginsCore.Exceptions;
using k8s;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.K8s_1_23
{
    public class K8s123MQStrategy : IStrategy
    {
        const string CuResourceKey = "resource";

        const string ServiceHostKey = "host";
        public const string ServiceNamespaceKey = "namespace";
        const string ServiceRootAccessTokenHostKey = "rootAccessToken";

        public const string ServiceHandlerKey = "handler";

        const string ServiceDisableSslVerificationKey = "disableSslVerification";

        readonly IServiceProvider _serviceProvider;

        static Dictionary<string, Type> _resourceHandlers = new Dictionary<string, Type> {
            { "deployment", typeof(KubernetesDeploymentHandler) },
            { "ingress", typeof(KubernetesIngressHandler) },
            { "service", typeof(KubernetesServiceHandler) },
        };

        public K8s123MQStrategy(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task Configure(Service service, JsonNode renderedTemplate, JsonObject variables, CancellationToken cancellationToken)
        {
            CheckRequiredVariables(variables);

            await CreateK8sResources(variables, renderedTemplate.AsObject(), cancellationToken);
        }

        public async Task UnConfigure(Service service, JsonNode renderedTemplate, JsonObject variables, CancellationToken cancellationToken)
        {
            CheckRequiredVariables(variables);

            await DropK8sResources(variables, cancellationToken);
        }

        static void CheckRequiredVariables(JsonObject variables)
        {
            if (!variables.ContainsKey(CuResourceKey))
                throw new ConfiguratorException($"Can't find variable {CuResourceKey}.");

            if (!variables.ContainsKey(ServiceHostKey))
                throw new ConfiguratorException($"Can't find variable {ServiceHostKey}.");
            if (!variables.ContainsKey(ServiceRootAccessTokenHostKey))
                throw new ConfiguratorException($"Can't find variable {ServiceRootAccessTokenHostKey}.");
            if (!variables.ContainsKey(ServiceNamespaceKey))
                throw new ConfiguratorException($"Can't find variable {ServiceNamespaceKey}.");

            var host = variables[ServiceHostKey]!.GetValue<string>();
            if (!Uri.TryCreate(host, default(UriCreationOptions), out var _))
                throw new ConfiguratorException($"Can't convert host value {host} to uri.");
        }

        async Task CreateK8sResources(JsonObject variables, JsonObject renderedTemplate, CancellationToken cancellationToken)
        {
            if (renderedTemplate is null)
                throw new ConfiguratorException($"The service rendered template is not an JSON object.");

            var host = variables[ServiceHostKey]!.GetValue<string>();
            var accessToken = variables[ServiceRootAccessTokenHostKey]!.GetValue<string>();
            var @namespace = variables[ServiceNamespaceKey]!.GetValue<string>();
            var disableSslVerification = variables[ServiceDisableSslVerificationKey]?.GetValue<bool>() ?? false;
            var resource = variables[CuResourceKey]!.GetValue<string>();

            var client = CreateK8sClient(host, accessToken, @namespace, disableSslVerification);

            foreach (var rootObject in variables)
            {
                if (rootObject.Value is JsonObject obj && obj.ContainsKey(ServiceHandlerKey) && obj[ServiceHandlerKey] is JsonValue handlerVal)
                {
                    var handlerValue = handlerVal.GetValue<string>();
                    if (string.IsNullOrEmpty(handlerValue))
                        continue;

                    if (!_resourceHandlers.ContainsKey(handlerValue))
                        continue;

                    var handlerType = _resourceHandlers[handlerValue];

                    // Found handler key. Requestring service from DI.
                    var handler = _serviceProvider.GetService(handlerType) as IKubernetesHandler;
                    if (handler is null)
                        continue;

                    if (!renderedTemplate.ContainsKey(rootObject.Key))
                        continue;

                    if (renderedTemplate[rootObject.Key] is not JsonObject)
                        continue;

                    var partOfRenderedTemplate = renderedTemplate[rootObject.Key]!;

                    await handler.CreateResource(client, resource, variables, partOfRenderedTemplate.AsObject(), cancellationToken);
                }
            }
        }

        async Task DropK8sResources(JsonObject variables, CancellationToken cancellationToken)
        {
            var host = variables[ServiceHostKey]!.GetValue<string>();
            var accessToken = variables[ServiceRootAccessTokenHostKey]!.GetValue<string>();
            var @namespace = variables[ServiceNamespaceKey]!.GetValue<string>();
            var disableSslVerification = variables[ServiceDisableSslVerificationKey]?.GetValue<bool>() ?? false;
            var resource = variables[CuResourceKey]!.GetValue<string>();

            var client = CreateK8sClient(host, accessToken, @namespace, disableSslVerification);

            foreach (var rootObject in variables)
            {
                if (rootObject.Value is JsonObject obj && obj.ContainsKey(ServiceHandlerKey) && obj[ServiceHandlerKey] is JsonValue handlerVal)
                {
                    var handlerValue = handlerVal.GetValue<string>();
                    if (string.IsNullOrEmpty(handlerValue))
                        continue;

                    if (!_resourceHandlers.ContainsKey(handlerValue))
                        continue;

                    var handlerType = _resourceHandlers[handlerValue];

                    // Found handler key. Requestring service from DI.
                    var handler = _serviceProvider.GetService(handlerType) as IKubernetesHandler;
                    if (handler is null)
                        continue;

                    await handler.DropResource(client, resource, @namespace, cancellationToken);
                }
            }
        }

        Kubernetes CreateK8sClient(string host, string accessToken, string @namespace, bool skipTlsVerify)
        {
            var config = new KubernetesClientConfiguration
            {
                Host = host,
                AccessToken = accessToken,
                Namespace = @namespace,
                SkipTlsVerify = skipTlsVerify
            };
            return new Kubernetes(config);
        }
    }
}
