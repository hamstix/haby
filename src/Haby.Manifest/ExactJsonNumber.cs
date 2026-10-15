using System.Buffers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.Manifest;

/// <summary>An exact JSON number lexeme for the manifest wire contract.</summary>
[JsonConverter(typeof(ExactJsonNumberConverter))]
public readonly struct ExactJsonNumber : IEquatable<ExactJsonNumber>
{
    private readonly string? _coefficient;
    private readonly BigInteger _exponent;

    private ExactJsonNumber(string rawText)
    {
        RawText = rawText;
        var unsigned = rawText.AsSpan();
        var negative = false;
        if (unsigned[0] == '-')
        {
            negative = true;
            unsigned = unsigned[1..];
        }

        var e = unsigned.IndexOfAny('e', 'E');
        var significand = e < 0 ? unsigned : unsigned[..e];
        var exponent = e < 0 ? BigInteger.Zero : BigInteger.Parse(unsigned[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var dot = significand.IndexOf('.');
        if (dot >= 0)
            exponent -= significand.Length - dot - 1;

        var digits = significand.ToString().Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0)
        {
            _coefficient = "0";
            _exponent = BigInteger.Zero;
            return;
        }

        var significantLength = digits.TrimEnd('0').Length;
        exponent += digits.Length - significantLength;
        _coefficient = (negative ? "-" : "") + digits[..significantLength];
        _exponent = exponent;
    }

    public string RawText { get; }

    public bool IsInteger => _coefficient is not null && _exponent >= 0;

    public static ExactJsonNumber Parse(string rawText)
    {
        using var document = JsonDocument.Parse(rawText);
        if (document.RootElement.ValueKind != JsonValueKind.Number)
            throw new FormatException("The value must be one JSON number.");
        return new ExactJsonNumber(rawText);
    }

    public bool Equals(ExactJsonNumber other) =>
        string.Equals(_coefficient, other._coefficient, StringComparison.Ordinal) && _exponent == other._exponent;

    public override bool Equals(object? obj) => obj is ExactJsonNumber other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_coefficient, _exponent);
    public override string ToString() => RawText ?? string.Empty;

}

/// <summary>An arbitrary-precision mathematical integer kept in JSON-number wire form.</summary>
[JsonConverter(typeof(ExactJsonIntegerConverter))]
public readonly struct ExactJsonInteger : IEquatable<ExactJsonInteger>
{
    private readonly ExactJsonNumber _number;

    private ExactJsonInteger(ExactJsonNumber number) => _number = number;

    public string RawText => _number.RawText;

    public static ExactJsonInteger Parse(string rawText)
    {
        var number = ExactJsonNumber.Parse(rawText);
        if (!number.IsInteger)
            throw new FormatException("The value must be a mathematical integer.");
        return new ExactJsonInteger(number);
    }

    public bool Equals(ExactJsonInteger other) => _number.Equals(other._number);
    public override bool Equals(object? obj) => obj is ExactJsonInteger other && Equals(other);
    public override int GetHashCode() => _number.GetHashCode();
    public override string ToString() => RawText;
}

public sealed class ExactJsonNumberConverter : JsonConverter<ExactJsonNumber>
{
    public override ExactJsonNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException("A JSON number is required.");
        var raw = reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
            : Encoding.UTF8.GetString(reader.ValueSpan);
        return ExactJsonNumber.Parse(raw);
    }

    public override void Write(Utf8JsonWriter writer, ExactJsonNumber value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.RawText);
}

public sealed class ExactJsonIntegerConverter : JsonConverter<ExactJsonInteger>
{
    public override ExactJsonInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException("A JSON integer is required.");
        var raw = reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
            : Encoding.UTF8.GetString(reader.ValueSpan);
        try
        {
            return ExactJsonInteger.Parse(raw);
        }
        catch (FormatException) { }
        throw new JsonException("A mathematical integer is required.");
    }

    public override void Write(Utf8JsonWriter writer, ExactJsonInteger value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.RawText);
}
