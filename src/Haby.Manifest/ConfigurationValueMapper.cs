using System.Text.Json;
using Haby.Domain;

namespace Haby.Manifest;

/// <summary>Copies a validated JSON extension value into the immutable non-secret domain tree.</summary>
public static class ConfigurationValueMapper
{
    public static ConfigurationValue FromValidatedJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => ConfigurationNull.Instance,
        JsonValueKind.True => new ConfigurationBoolean(true),
        JsonValueKind.False => new ConfigurationBoolean(false),
        JsonValueKind.String => new ConfigurationString(value.GetString()!),
        JsonValueKind.Number => ConfigurationNumber.Parse(value.GetRawText()),
        JsonValueKind.Array => new ConfigurationArray(value.EnumerateArray().Select(FromValidatedJson)),
        JsonValueKind.Object => new ConfigurationObject(value.EnumerateObject().Select(member =>
            new KeyValuePair<string, ConfigurationValue>(member.Name, FromValidatedJson(member.Value)))),
        _ => throw new ArgumentException("The value must be validated JSON.", nameof(value))
    };
}
