using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace Hamstix.Haby.Server.Configurator
{
    public class ForeignKeyConfigurator : IForeignKeyConfigurator
    {
        readonly HabbyContext _context;
        readonly ILogger<CuConfigurator> _log;

        public ForeignKeyConfigurator(HabbyContext context,
            ILogger<CuConfigurator> log)
        {
            _log = log;
            _context = context;
        }

        public async Task<ConfigurationUnitAtService?> GetCuForeignKey(
            ConfigurationUnit cu, string serviceName, string cuServiceFromKeyValue)
        {
            var splittedKey = cuServiceFromKeyValue.Split("/", StringSplitOptions.RemoveEmptyEntries);
            if (splittedKey.Length == 0)
            {
                _log.LogWarning("The key {ServiceName} has field \"fromKey\" but the key is empty.", serviceName);
                return null;
            }

            // The key is from the same configuration unit template.
            // And it must be processed earlier in the loop.
            if (splittedKey.Length == 1)
            {
                var key = splittedKey[0];

                var foreignKeyService = cu.Services.FirstOrDefault(x => x.Service.Name == serviceName && x.Key == key);
                if (foreignKeyService is null)
                {
                    _log.LogWarning("The key {ServiceName} has field \"fromKey\"=\"{Key}\" " +
                        "but the key is not present in the configuration unit template.",
                            serviceName, key);
                    return null;
                }
                return foreignKeyService;
            }

            // The key is from the foreign configuration unit.
            if (splittedKey.Length == 2)
            {
                var foreignCuName = splittedKey[0];
                var foreignKey = splittedKey[1];

                var foreignKeyService = await _context
                    .ConfigurationUnitsAtServices
                    .Include(x => x.ConfigurationUnit)
                    .FirstOrDefaultAsync(x => x.Service.Name == serviceName
                        && x.ConfigurationUnit.Name == foreignCuName
                        && x.Key == foreignKey);
                if (foreignKeyService is null)
                {
                    _log.LogWarning("The key {ServiceName} has field \"fromKey\"=\"{Key}\" " +
                        "but the key is not present in the configuration unit template of the cu {ConfigurationUnitName}.",
                            serviceName,
                            cuServiceFromKeyValue,
                            foreignCuName);
                    return null;
                }

                return foreignKeyService;
            }

            if (splittedKey.Length > 2)
            {
                _log.LogWarning("The key {ServiceName} has field \"fromKey\"=\"{Key}\" " +
                        "but the key contains more then 2 values splitted by \"/\". " +
                        "Currently the service is not supported such path.",
                            serviceName,
                            cuServiceFromKeyValue);
                return null;
            }

            return null;
        }
    }
}
