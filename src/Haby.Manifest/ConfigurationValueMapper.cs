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
        JsonValueKind.Object => MapObject(value),
        _ => throw new ArgumentException("The value must be validated JSON.", nameof(value))
    };

    private static ConfigurationObject MapObject(JsonElement value)
    {
        // Secret references are produced by trusted domain operations, never by manifest JSON.
        if (value.TryGetProperty("$secretRef"u8, out _))
            throw new ArgumentException("Manifest JSON cannot declare a SecretReference.", nameof(value));
        return new ConfigurationObject(value.EnumerateObject().Select(member =>
            new KeyValuePair<string, ConfigurationValue>(member.Name, FromValidatedJson(member.Value))));
    }
}
