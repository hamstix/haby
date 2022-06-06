using Hamstix.Haby.Server.Models;

namespace Hamstix.Haby.Server.Configurator
{
    public interface IForeignKeyConfigurator
    {
        /// <summary>
        /// Get the key configuration from the other key or other configuration unit key.
        /// </summary>
        /// <param name="cu">Configuration unit.</param>
        /// <param name="serviceName">Configuration unit key service node. Example: "PostgreSql": {}.</param>
        /// <param name="cuServiceFromKeyValue">The value of the "fromKey" propery.</param>
        /// <returns></returns>
        Task<ConfigurationUnitAtService?> GetCuForeignKey(
            ConfigurationUnit cu, string serviceName, string cuServiceFromKeyValue);
    }
}
