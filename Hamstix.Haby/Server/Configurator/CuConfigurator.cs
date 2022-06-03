using Fluid;
using Fluid.Values;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Shared.PluginsCore.Exceptions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Models;
using System.Text.RegularExpressions;
using Hamstix.Haby.Server.Helpers;

namespace Hamstix.Haby.Server.Configurator
{
    public class CuConfigurator : ICuConfigurator
    {
        const string AppsettingsKey = "key";
        const string CuServicesKey = "services";
        const string CuParametersKey = "parameters";
        const string CuServiceVariablesKey = "variables";
        const string CuServiceFromKeyKey = "fromKey";
        const string CuParamaterNameKey = "name";
        const string CuParamaterValueKey = "value";

        static readonly FluidParser _parser = new FluidParser(new FluidParserOptions { AllowFunctions = true }); // Parser is thread safe.

        readonly HabbyContext _context;
        readonly ILogger<CuConfigurator> _log;
        readonly IServiceConfigurator _serviceConfigurator;

        List<Service> _services;
        List<SystemVariable> _systemVariables;
        List<Generator> _generators;

        public CuConfigurator(HabbyContext context, IServiceConfigurator serviceConfigurator, ILogger<CuConfigurator> log)
        {
            _serviceConfigurator = serviceConfigurator;
            _log = log;
            _context = context;
        }

        static CuConfigurator()
        {
            // When a property of a JsonObjectvalue is accessed, try to look into its properties
            TemplateOptions.Default.MemberAccessStrategy.Register<JsonObject, object?>((source, name) => source[name]);

            TemplateOptions.Default.ValueConverters.Add(x => x is JsonObject o ? new ObjectValue(o) : null);
            TemplateOptions.Default.ValueConverters.Add(x => x is JsonValue v ? v.GetValue<object>() : null);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<ConfigurationUnitKeyResult>> Configure(long configurationUnitId)
        {
            var cu = await _context
                .ConfigurationUnits
                .Include(x => x.Services)
                    .ThenInclude(x => x.Variables)
                .Include(x => x.Parameters)
                .FirstOrDefaultAsync(x => x.Id == configurationUnitId);

            if (cu is null)
                throw new ConfiguratorException($"The configuration unit id {configurationUnitId} is not found.");

            _services = await _context.Services.ToListAsync(); // Load all services to memory. The total count shouldn't be more the 1000.

            _systemVariables = await _context
                .SystemVariables // Load all system variables to memory. The total count shouldn't be more the 1000.
                .AsNoTracking()
                .ToListAsync();

            _generators = await _context
                .Generators // Load all generators to memory. The total count shouldn't be more the 1000.
                .AsNoTracking()
                .ToListAsync();
            var cuTemplateKeys = cu.Template.AsArray();

            var resultStatuses = new List<ConfigurationUnitKeyResult>();
            foreach (var cuTemplateKey in cuTemplateKeys)
            {
                var keyResult = await ConfigureConfigurationUnitTemplateKey(cu, cuTemplateKey?.AsObject() ?? new JsonObject());
                if (keyResult is not null)
                    resultStatuses.Add(keyResult);
            }

            await _context.SaveChangesAsync();

            return resultStatuses;
        }

        public async Task<ConfigurationUnitKeyResult?> ConfigureConfigurationUnitTemplateKey(ConfigurationUnit cu, JsonObject ConfigrationUnitTemplateKey)
        {
            var keyName = ConfigrationUnitTemplateKey[AppsettingsKey]?.GetValue<string>();

            if (keyName is null)
            {
                _log.LogWarning("The node {TemplateNode} has no key \"{Key}\"", ConfigrationUnitTemplateKey.ToJsonString(), AppsettingsKey);
                return null; // Skip not valid keys.
            }

            var resultStatus = new ConfigurationUnitKeyResult(keyName);
            
            var servicesObject = ConfigrationUnitTemplateKey[CuServicesKey]?.AsObject();
            if (servicesObject is not null)
            {
                resultStatus.Results = await ConfigureServices(cu, servicesObject, keyName);
            }

            var parametersObject = ConfigrationUnitTemplateKey[CuParametersKey]?.AsArray();
            if (parametersObject is not null)
                ConfigureParameters(cu, parametersObject, keyName);

            await UpdateConfigurationKey(cu, keyName);

            return resultStatus;
        }

        async Task<IEnumerable<Shared.PluginsCore.ConfigurationResult>> ConfigureServices(
            ConfigurationUnit cu, JsonObject servicesObject, string configurationKey)
        {
            // Loop over object nodes where key must be service name.
            // Example: "PostreSql": {}

            var resultStatuses = new List<Shared.PluginsCore.ConfigurationResult>();
            foreach (var cuServiceNode in servicesObject)
            {
                var cuServiceObject = cuServiceNode.Value?.AsObject();
                if (cuServiceObject is null)
                {
                    _log.LogWarning("The node {ServiceObject} is not an object.", servicesObject.ToJsonString());
                    continue; // Skip not valid keys.
                }

                var serviceName = cuServiceNode.Key;

                var service = _services.FirstOrDefault(x => x.Name == serviceName);
                if (service is null)
                {
                    _log.LogWarning("Can't find the service name {ServiceName} in the database while parsing the configuration unit template.", serviceName);
                    continue; // Skip not valid keys.
                }

                // Get or Create configuration unit at service model.
                var configurationUnitAtService = cu.Services.FirstOrDefault(x => x.ServiceId == service.Id) ??
                    new ConfigurationUnitAtService(cu, service, configurationKey);

                JsonNode renderedTemplate;

                var cuServiceFromKeyValue = cuServiceNode.Value?[CuServiceFromKeyKey]?.GetValue<string>();

                if (cuServiceFromKeyValue is not null)
                {
                    // Trying to read configuration from the other cu key.
                    // If this option is enabled, then the key template will be fully replaced by the cu key reference.
                    renderedTemplate = await GetForeignKeyFromCu(cu, serviceName, cuServiceFromKeyValue);
                }
                else
                {
                    var cuServiceVariablesObject = cuServiceNode.Value?[CuServiceVariablesKey]?.AsObject();

                    // Read all variables.
                    var systemVariables = GetSystemVariables(cu);
                    var serviceVariables = GetServiceVariables(service);
                    var savedVariables = GetSavedVariables(configurationUnitAtService.Variables);
                    var cuVariables = GetConfigurationUnitVariables(cuServiceVariablesObject);

                    try
                    {
                        var joinedVariables = systemVariables
                            .Merge(serviceVariables)
                            .Merge(savedVariables)
                            .Merge(cuVariables);
                        // Render the service template to the JSON.
                        renderedTemplate = RenderServiceTemplate(service, configurationUnitAtService, joinedVariables);
                    }
                    catch (ParseException e)
                    {
                        _log.LogError("Can't render the service {ServiceName} template. Error: {Error}",
                            serviceName, e.Message);
                        continue; // Skip not valid keys.
                    }
                }

                var result = await TryConfigureAtService(service, renderedTemplate);
                if (result.Status == Shared.PluginsCore.ConfigurationResultStatuses.Ok)
                    configurationUnitAtService.RenderedTemplateJson = renderedTemplate;

                resultStatuses.Add(result);
            }
            return resultStatuses;
        }

        /// <summary>
        /// Get the key configuration from the other key or other configuration unit key.
        /// </summary>
        /// <param name="cu">Configuration unit.</param>
        /// <param name="serviceName">Configuration unit key service node. Example: "PostgreSql": {}.</param>
        /// <param name="cuServiceFromKeyValue">The value of the "fomrKey" propery.</param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        async Task<JsonObject> GetForeignKeyFromCu(ConfigurationUnit cu, string serviceName, string cuServiceFromKeyValue)
        {
            var emptyResult = new JsonObject();
            var splittedKey = cuServiceFromKeyValue.Split("/", StringSplitOptions.RemoveEmptyEntries);
            if (splittedKey.Length == 0)
            {
                _log.LogWarning("The key {ServiceName} has field \"fromKey\" but the key is empty.", serviceName);
                return emptyResult;
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
                    return emptyResult;
                }
                return foreignKeyService.RenderedTemplateJson?.CloneJsonNode()?.AsObject() ?? emptyResult;
            }

            // The key is from the foreign configuration unit.
            if (splittedKey.Length == 2)
            {
                var foreignCuName = splittedKey[0];
                var foreignKey = splittedKey[1];

                var foreignKeyService = await _context
                    .ConfigurationUnitsAtServices
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
                    return emptyResult;
                }

                return foreignKeyService.RenderedTemplateJson?.AsObject()?.CloneJsonNode()?.AsObject() ?? emptyResult;
            }

            if (splittedKey.Length > 2)
            {
                _log.LogWarning("The key {ServiceName} has field \"fromKey\"=\"{Key}\" " +
                        "but the key contains more then 2 values splitted by \"/\". " +
                        "Currently the service is not supported such path.",
                            serviceName,
                            cuServiceFromKeyValue);
                return emptyResult;
            }

            return emptyResult;
        }

        JsonNode RenderServiceTemplate(Service service,
            ConfigurationUnitAtService configurationUnitAtService,
            JsonObject variables)
        {
            var serviceTemplate = service.Template;
            if (serviceTemplate is null)
                return new JsonObject();

            var dbVariables = (List<Variable>)configurationUnitAtService.Variables;

            // Render Liquid template.
            var template = _parser.Parse(serviceTemplate);

            var regex = new FunctionValue((args, context) =>
            {
                var input = args.At(0).ToStringValue();
                var pattern = args.At(1).ToStringValue();
                var replacement = args.At(2).ToStringValue();

                var result = Regex.Replace(input, pattern, replacement);

                return new ValueTask<FluidValue>(new StringValue(result));
            });

            var password = new FunctionValue((args, context) =>
            {
                var requiredLenght = (int)args.At(0).ToNumberValue();
                var requiredUniqueChars = (int)args.At(1).ToNumberValue();
                var requireDigits = args.At(2).ToBooleanValue();
                var requireNonAlphanumeric = args.At(3).ToBooleanValue();
                var requireLowercase = args.At(4).ToBooleanValue();
                var requireUppercase = args.At(5).ToBooleanValue();

                var result = PasswordGenerator.Generate(requiredLenght,
                               requiredUniqueChars,
                               requireDigits,
                               requireNonAlphanumeric,
                               requireLowercase,
                               requireUppercase);

                return new ValueTask<FluidValue>(new StringValue(result));
            });

            var generate = new FunctionValue((args, context) =>
                {
                    var firstArg = args.At(0).ToStringValue();
                    var secondArg = args.At(1).ToStringValue();

                    if (dbVariables.Any(x => x.Name == secondArg))
                        return new ValueTask<FluidValue>(
                            new StringValue(dbVariables.First(x => x.Name == secondArg).Value.GetValue<string>()));
                    else
                    {
                        // Find the generator by first argument.
                        var generator = _generators.FirstOrDefault(x => x.Name == firstArg);
                        if (generator is null)
                            return new ValueTask<FluidValue>(new StringValue(String.Empty));

                        var generatorTemplate = _parser.Parse(generator.Template);

                        var generatorContext = new TemplateContext(variables);
                        generatorContext.SetValue("regexReplace", regex);
                        generatorContext.SetValue("password", password);
                        var generatedVariable = generatorTemplate.Render(generatorContext);

                        // Generate new variable.
                        var newVariable = new Variable(secondArg, JsonValue.Create(generatedVariable), VariableTypes.Service);
                        // Save to the variables list.
                        dbVariables.Add(newVariable);
                        return new ValueTask<FluidValue>(new StringValue(generatedVariable));
                    }
                });

            var context = new TemplateContext(variables);
            context.SetValue("generate", generate);
            context.SetValue("regexReplace", regex);
            context.SetValue("password", password);

            return JsonNode.Parse(template.Render(context));
        }

        void ConfigureParameters(ConfigurationUnit cu, JsonArray parametersObject, string key)
        {
            var dbParameters = cu.Parameters;
            foreach (var parameter in parametersObject)
            {
                var name = parameter[CuParamaterNameKey]?.GetValue<string>();
                if (name is null)
                    continue;
                var value = parameter[CuParamaterValueKey];
                if (value is null)
                    continue;

                var dbParameter = dbParameters.FirstOrDefault(x => x.Name == name && x.Key == key);
                // Adding new parameter.
                if (dbParameter is null)
                    cu.Parameters.Add(new ConfigurationUnitParameter(name, key)
                    {
                        Value = value
                    });
            }
        }

        JsonObject GetSystemVariables(ConfigurationUnit cu)
        {
            var configuration = new JsonObject();
            foreach (var systemVariable in _systemVariables)
                configuration.Add(systemVariable.Name, systemVariable.Value.CloneJsonNode());

            var cuObject = new JsonObject();
            cuObject.Add("id", cu.Id);
            cuObject.Add("name", cu.Name);
            cuObject.Add("version", cu.Version);
            cuObject.Add("previousVersion", cu.PreviousVersion);

            configuration.Add("cu", cuObject);

            return configuration;
        }

        static JsonObject GetServiceVariables(Service service) => service.JsonConfig;

        static JsonObject GetSavedVariables(IEnumerable<Variable> variables)
        {
            var result = new JsonObject();
            foreach (var variable in variables)
            {
                result.Add(variable.Name, variable.Value);
            }
            return result;
        }

        JsonObject GetConfigurationUnitVariables(JsonObject? cuVariables)
        {
            var result = new JsonObject();
            if (cuVariables is null)
                return result;

            // Loop through the configuration unit template variables.
            // When the key is simple variable, then use it as value.
            // If the key is an object, then try to find key with name "fromKey".
            // If the key "fromKey" is found, then read the value from anoather key of the configuration unit.
            // If the key "fromKey" is not found, then read the value of the key as object.
            foreach (var cuVariable in cuVariables)
            {
                if (cuVariable.Value is JsonObject obj && obj.ContainsKey(CuServiceFromKeyKey))
                {
                    // Read key value from the other key.
                }
                else
                {
                    // Clone json properties.
                    var val = cuVariable.Value.CloneJsonNode();
                    result.Add(cuVariable.Key, val);
                }
            }
            return result;
        }

        async Task UpdateConfigurationKey(ConfigurationUnit cu, string key)
        {
            JsonObject jsonConfiguration = new JsonObject();
            foreach (var cuAtService in cu.Services)
            {
                jsonConfiguration.Add(cuAtService.Service.Name, cuAtService.RenderedTemplateJson.CloneJsonNode());
            }

            foreach (var cuParameter in cu.Parameters)
            {
                jsonConfiguration.Add(cuParameter.Name, cuParameter.Value.CloneJsonNode());
            }

            var configurationKey = await _context
                .ConfigurationKeys
                .FirstOrDefaultAsync(x => x.ConfigurationUnitId == cu.Id && x.Name == key);
            if (configurationKey != null)
                configurationKey.Configuration = jsonConfiguration;
            else
            {
                configurationKey = new ConfigurationKey(key, cu) { Configuration = jsonConfiguration };
                _context.ConfigurationKeys.Add(configurationKey);
            }
        }

        async Task<Shared.PluginsCore.ConfigurationResult> TryConfigureAtService(Service service, JsonNode renderedTemplate)
        {
            return await _serviceConfigurator.Configure(service, renderedTemplate);
        }
    }
}
