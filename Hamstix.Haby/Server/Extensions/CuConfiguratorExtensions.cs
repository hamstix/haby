using Hamstix.Haby.Server.Models;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Extensions
{
    public static class CuConfiguratorExtensions
    {
        public static JsonObject GetSavedVariables(this IEnumerable<Variable> variables)
        {
            var result = new JsonObject();
            foreach (var variable in variables)
            {
                result.Add(variable.Name, variable.Value);
            }
            return result;
        }

        public static JsonObject? GetCuServiceNode(this ConfigurationUnit cu, string configurationKey, string serviceName) =>
            cu.Template[configurationKey]?[serviceName]?.AsObject();
    }
}
