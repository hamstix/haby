using Hamstix.Haby.Server.Models;

namespace Hamstix.Haby.Server.Configurator
{
    public interface ICuConfigurator
    {
        /// <summary>
        /// Configure configuration unit at services.
        /// </summary>
        /// <param name="configurationUnitId">Configuration unit Id.</param>
        /// <returns></returns>
        Task<IEnumerable<ConfigurationUnitKeyResult>> Configure(long configurationUnitId);
    }
}
