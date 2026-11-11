using System.Text;
using System.Text.Json;
using Haby.Application;

namespace Haby.Kubernetes.Contracts;

/// <summary>Current-alpha portable snapshot, validated by the same closed decoder as pure planning.</summary>
public sealed class KubernetesPlacementCodec : IPlacementCodec
{
    public const string Contract = "kubernetes.placement/v1alpha1";
    public const int FormatVersion = 1;
    public PlacementDeclaration Decode(ReadOnlyMemory<byte> payload)
    {
        try
        {
            // STJ replaces invalid UTF-8 in some string paths. Refuse it before either decoder runs.
            _ = new UTF8Encoding(false, true).GetCharCount(payload.Span);
            var typed = KubernetesContract.DecodePlacement(payload);
            var root = JsonSerializer.Deserialize(payload.Span, KubernetesJsonContext.Default.JsonElement);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Canonical(writer, root);
            return new(typed.Reference.Name, typed.Reference.Revision, Contract, FormatVersion, Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (WorkloadContractException error) { throw new PlacementDeclarationException(error.Code); }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new PlacementDeclarationException("contract.json"); }
    }

    public PlacementDeclaration Read(string contract, int formatVersion, string payload)
    {
        if (contract != Contract || formatVersion != FormatVersion || payload is null || payload.Length > 1048576)
            throw new InvalidDataException("Stored Placement revision is unavailable.");
        try { return Decode(new UTF8Encoding(false, true).GetBytes(payload)); }
        catch (ArgumentException) { throw new InvalidDataException("Stored Placement revision is unavailable."); }
    }

    public static KubernetesPlacement ToTyped(PlacementDeclaration declaration)
    {
        var validated = new KubernetesPlacementCodec().Read(declaration.Contract, declaration.FormatVersion, declaration.Payload);
        if (validated.Name != declaration.Name || validated.Revision != declaration.Revision)
            throw new InvalidDataException("Stored Placement revision is unavailable.");
        return KubernetesContract.DecodePlacement(Encoding.UTF8.GetBytes(validated.Payload));
    }

    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Canonical(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); break;
            // The closed Placement schema admits only bounded integer JSON numbers.
            case JsonValueKind.Number: writer.WriteNumberValue(checked((int)value.GetDecimal())); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            default: throw new PlacementDeclarationException("contract.schema");
        }
    }
}
