using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Models;

/// <summary>
/// Configuration unit parameters.
/// </summary>
public class ConfigurationUnitParameter
{
    /// <summary>
    /// Configuration unit Id.
    /// </summary>
    public long ConfigurationUnitId { get; private set; }

    /// <summary>
    /// Configuration unit.
    /// </summary>
    public ConfigurationUnit ConfigurationUnit { get; private set; }

    /// <summary>
    /// The name of the key for which the configuration unit configuration will be generated.
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// The paramater name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The paramter value.
    /// </summary>
    public JsonNode Value { get; set; } = new JsonObject();

    public ConfigurationUnitParameter(string name, string key)
    {
        Name = name;
        Key = key;
    }

    ConfigurationUnitParameter() { } // For serializers.
}
