using System.Collections.Immutable;
using System.Numerics;

namespace Haby.Domain;

/// <summary>An immutable, format-neutral structured configuration value.</summary>
public abstract class ConfigurationValue : IEquatable<ConfigurationValue>
{
    private protected ConfigurationValue() { }
    public abstract bool Equals(ConfigurationValue? other);
    public override bool Equals(object? obj) => obj is ConfigurationValue other && Equals(other);
    public abstract override int GetHashCode();
}

/// <summary>A JSON-compatible null value.</summary>
public sealed class ConfigurationNull : ConfigurationValue
{
    private ConfigurationNull() { }
    public static ConfigurationNull Instance { get; } = new();
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationNull;
    public override int GetHashCode() => 0;
}

/// <summary>A Boolean value.</summary>
public sealed class ConfigurationBoolean(bool value) : ConfigurationValue
{
    public bool Value { get; } = value;
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationBoolean value && Value == value.Value;
    public override int GetHashCode() => HashCode.Combine(1, Value);
}

/// <summary>An ordinal string value.</summary>
public sealed class ConfigurationString : ConfigurationValue
{
    public ConfigurationString(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
    public string Value { get; }
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationString value && string.Equals(Value, value.Value, StringComparison.Ordinal);
    public override int GetHashCode() => HashCode.Combine(2, StringComparer.Ordinal.GetHashCode(Value));
}

/// <summary>An exact JSON number represented by sign, arbitrary-precision coefficient and decimal exponent.</summary>
public sealed class ConfigurationNumber : ConfigurationValue
{
    private readonly string _coefficient;
    private readonly BigInteger _exponent;

    private ConfigurationNumber(string rawText, string coefficient, BigInteger exponent)
    {
        RawText = rawText;
        _coefficient = coefficient;
        _exponent = exponent;
    }

    /// <summary>The original valid JSON number token. Equality uses its mathematical value.</summary>
    public string RawText { get; }

    public static ConfigurationNumber Parse(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        var span = rawText.AsSpan();
        var index = 0;
        var negative = index < span.Length && span[index] == '-';
        if (negative) index++;
        if (index >= span.Length) throw new FormatException("The value must be one JSON number.");
        if (span[index] == '0') index++;
        else
        {
            if (span[index] is < '1' or > '9') throw new FormatException("The value must be one JSON number.");
            while (index < span.Length && IsDigit(span[index])) index++;
        }
        var fractionDigits = 0;
        if (index < span.Length && span[index] == '.')
        {
            index++;
            var start = index;
            while (index < span.Length && IsDigit(span[index])) index++;
            fractionDigits = index - start;
            if (fractionDigits == 0) throw new FormatException("The value must be one JSON number.");
        }
        var significandEnd = index;
        var exponent = BigInteger.Zero;
        if (index < span.Length && span[index] is 'e' or 'E')
        {
            index++;
            var sign = 1;
            if (index < span.Length && span[index] is '+' or '-')
            {
                if (span[index] == '-') sign = -1;
                index++;
            }
            if (index >= span.Length || !IsDigit(span[index])) throw new FormatException("The value must be one JSON number.");
            while (index < span.Length && IsDigit(span[index]))
            {
                exponent = exponent * 10 + (span[index] - '0');
                index++;
            }
            exponent *= sign;
        }
        if (index != span.Length) throw new FormatException("The value must be one JSON number.");
        exponent -= fractionDigits;
        var digits = span[(negative ? 1 : 0)..significandEnd].ToString().Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return new(rawText, "0", BigInteger.Zero);
        var significantLength = digits.TrimEnd('0').Length;
        exponent += digits.Length - significantLength;
        return new(rawText, (negative ? "-" : "") + digits[..significantLength], exponent);
    }

    private static bool IsDigit(char value) => value is >= '0' and <= '9';
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationNumber value &&
        string.Equals(_coefficient, value._coefficient, StringComparison.Ordinal) && _exponent == value._exponent;
    public override int GetHashCode() => HashCode.Combine(3, _coefficient, _exponent);
}

/// <summary>An ordered sequence of immutable configuration values.</summary>
public sealed class ConfigurationArray : ConfigurationValue
{
    private readonly ImmutableArray<ConfigurationValue> _items;
    public ConfigurationArray(IEnumerable<ConfigurationValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToImmutableArray();
        if (_items.Any(item => item is null)) throw new ArgumentException("Array items cannot be null references.", nameof(items));
    }
    public IReadOnlyList<ConfigurationValue> Items => _items;
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationArray value && _items.SequenceEqual(value._items);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(4);
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }
}

/// <summary>A collection of ordinally keyed immutable configuration values.</summary>
public sealed class ConfigurationObject : ConfigurationValue
{
    private readonly ImmutableDictionary<string, ConfigurationValue> _members;
    public ConfigurationObject(IEnumerable<KeyValuePair<string, ConfigurationValue>> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var builder = ImmutableDictionary.CreateBuilder<string, ConfigurationValue>(StringComparer.Ordinal);
        foreach (var (key, value) in members)
        {
            if (key is null || value is null) throw new ArgumentException("Object members cannot contain null references.", nameof(members));
            if (!builder.TryAdd(key, value)) throw new ArgumentException("Object members must have unique ordinal keys.", nameof(members));
        }
        _members = builder.ToImmutable();
    }
    public IReadOnlyDictionary<string, ConfigurationValue> Members => _members;
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationObject value &&
        _members.Count == value._members.Count && _members.All(member =>
            value._members.TryGetValue(member.Key, out var candidate) && member.Value.Equals(candidate));
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(5);
        foreach (var member in _members.OrderBy(member => member.Key, StringComparer.Ordinal))
        {
            hash.Add(member.Key, StringComparer.Ordinal);
            hash.Add(member.Value);
        }
        return hash.ToHashCode();
    }
}

/// <summary>An internal typed placeholder for one exact Secret version.</summary>
public sealed class ConfigurationSecretReference : ConfigurationValue
{
    public ConfigurationSecretReference(SecretRef reference) => Reference = reference ?? throw new ArgumentNullException(nameof(reference));
    public SecretRef Reference { get; }
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationSecretReference value && Reference == value.Reference;
    public override int GetHashCode() => HashCode.Combine(6, Reference);
}
