using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Models;

/// <summary>
/// The configuration unit related to the service.
/// </summary>
public class ConfigurationUnitAtService
{
    /// <summary>
    /// Service Id.
    /// </summary>
    public long ServiceId { get; private set; }

    /// <summary>
    /// Configuration unit Id.
    /// </summary>
    public long ConfigurationUnitId { get; private set; }

    /// <summary>
    /// Configuration unit.
    /// </summary>
    public ConfigurationUnit ConfigurationUnit { get; private set; }

    /// <summary>
    /// Service.
    /// </summary>
    public Service Service { get; private set; }

    /// <summary>
    /// The name of the key for which the configuration unit configuration will be generated.
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// The rendered service tempalate for the configuration unit.
    /// </summary>
    public JsonNode? RenderedTemplateJson { get; set; }

    /// <summary>
    /// The list of the variables, created for the configuration unit at service.
    /// </summary>
    public IEnumerable<Variable> Variables { get; private set; } = new List<Variable>();

    /// <summary>
    /// Add variable to the list.
    /// </summary>
    /// <param name="variable">Variable.</param>
    public void AddVariable(Variable variable)
    {
        ((List<Variable>)Variables).Add(variable);
        variable.SetCuAtService(this);
    }

    public ConfigurationUnitAtService(ConfigurationUnit cu, Service service, string configurationKey)
    {
        if (string.IsNullOrEmpty(configurationKey))
        {
            throw new ArgumentException($"{nameof(configurationKey)} is null or empty.", nameof(configurationKey));
        }

        ConfigurationUnit = cu ?? throw new ArgumentNullException(nameof(cu), $"{nameof(cu)} is null.");
        ConfigurationUnitId = cu.Id;
        Service = service ?? throw new ArgumentNullException(nameof(service), $"{nameof(service)} is null.");
        ServiceId = service.Id;
        Key = configurationKey;

        ConfigurationUnit.Services.Add(this);
    }

    private ConfigurationUnitAtService() { } // For serializers.
}
