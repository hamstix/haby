using Hamstix.Haby.Shared.PluginsCore;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Plugins.K8s
{
    public class K8s123MQStrategy : IStrategy
    {
        const string CuUsernameKey = "username";

        const string ServiceRootUserKey = "rootUser";

        public async Task Configure(Service service, JsonNode renderedTemplate, JsonObject variables, CancellationToken cancellationToken)
        {

        }

        public async Task UnConfigure(Service service, JsonNode renderedTemplate, JsonObject variables, CancellationToken cancellationToken)
        {

        }
    }
}
