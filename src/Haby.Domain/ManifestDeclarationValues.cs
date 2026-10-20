using System.Numerics;
using System.Text.RegularExpressions;

namespace Haby.Domain;

/// <summary>An exact scalar number in a closed manifest parameter definition.</summary>
public sealed class ExactManifestNumber : IEquatable<ExactManifestNumber>
{
    private readonly string _coefficient;
    private readonly BigInteger _exponent;
    private ExactManifestNumber(string coefficient, BigInteger exponent)
    {
        _coefficient = coefficient; _exponent = exponent;
    }
    public string CanonicalText => _coefficient + "e" + _exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool IsInteger => _coefficient == "0" || _exponent >= 0;
    public static ExactManifestNumber Parse(string rawText)
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
        if (digits.Length == 0) return new("0", BigInteger.Zero);
        var significantLength = digits.TrimEnd('0').Length;
        exponent += digits.Length - significantLength;
        return new((negative ? "-" : "") + digits[..significantLength], exponent);
    }
    private static bool IsDigit(char value) => value is >= '0' and <= '9';
    public bool Equals(ExactManifestNumber? other) => other is not null &&
        string.Equals(_coefficient, other._coefficient, StringComparison.Ordinal) && _exponent == other._exponent;
    public override bool Equals(object? obj) => obj is ExactManifestNumber other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(_coefficient), _exponent);
}

/// <summary>A local declaration key, scoped by its containing map.</summary>
public sealed record ManifestLocalId
{
    private static readonly Regex Pattern = new(@"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private ManifestLocalId(string value) => Value = value;
    public string Value { get; }
    public static ManifestLocalId Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 63 || !Pattern.IsMatch(value)) throw new ArgumentException("Invalid manifest-local ID.", nameof(value));
        return new(value);
    }
    public override string ToString() => Value;
}

public sealed record ProviderAlias
{
    private ProviderAlias(string value) => Value = value;
    public string Value { get; }
    public static ProviderAlias Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = ApplicationId.Create(value);
        return new(value);
    }
    public override string ToString() => Value;
}

public sealed record ProjectionPath
{
    private ProjectionPath(string value) => Value = value;
    public string Value { get; }
    internal static ProjectionPath FromValidated(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value[0] != '/') throw new ArgumentException("A non-root absolute JSON Pointer is required.", nameof(value));
        return new(value);
    }
    public override string ToString() => Value;
}

public abstract record VersionedContractId
{
    private static readonly Regex Pattern = new(@"\A[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*/v[1-9][0-9]*(?:(?:alpha|beta)[1-9][0-9]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private protected VersionedContractId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Pattern.IsMatch(value)) throw new ArgumentException("Invalid versioned contract ID.", nameof(value));
        Value = value;
    }
    public string Value { get; }
    public override string ToString() => Value;
}
public sealed record ResourceTypeId : VersionedContractId
{
    private ResourceTypeId(string value) : base(value) { }
    public static ResourceTypeId Create(string value) => new(value);
}
public sealed record WorkloadTypeId : VersionedContractId
{
    private WorkloadTypeId(string value) : base(value) { }
    public static WorkloadTypeId Create(string value) => new(value);
}
public sealed record OutputContractId : VersionedContractId
{
    private OutputContractId(string value) : base(value) { }
    public static OutputContractId Create(string value) => new(value);
}
public sealed record ConfigurationRendererId : VersionedContractId
{
    private ConfigurationRendererId(string value) : base(value) { }
    public static ConfigurationRendererId Create(string value) => new(value);
}
