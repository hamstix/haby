using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Manifest;

public sealed class ProfileRevisionConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException();
        using var document = JsonDocument.ParseValue(ref reader);
        var number = ConfigurationNumber.Parse(document.RootElement.GetRawText()).CanonicalText.Split('e');
        var exponent = BigInteger.Parse(number[1], CultureInfo.InvariantCulture);
        if (number[0][0] == '-' || exponent < 0 || exponent > 9 || number[0].Length + exponent > 10) throw new JsonException();
        var integer = BigInteger.Parse(number[0], CultureInfo.InvariantCulture) * BigInteger.Pow(10, (int)exponent);
        if (integer < 1 || integer > int.MaxValue) throw new JsonException();
        return (int)integer;
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
