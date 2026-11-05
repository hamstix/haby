namespace Haby.Domain;

/// <summary>An exact formatting contract, distinct from renderer and implementation identities.</summary>
public sealed record ConfigurationFormatterId : VersionedContractId
{
    private ConfigurationFormatterId(string value) : base(value) { }
    public static ConfigurationFormatterId Create(string value) => new(value);
}

/// <summary>A confidential String to be formatted only by a future authorized delivery operation.</summary>
public sealed class ConfigurationDeferredValue : ConfigurationValue
{
    public ConfigurationDeferredValue(ConfigurationFormatterId formatter, ConfigurationObject arguments)
    {
        Formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        if (ContainsDeferred(arguments)) throw new ArgumentException("Nested deferred values are unsupported.", nameof(arguments));
    }
    public ConfigurationFormatterId Formatter { get; }
    public ConfigurationObject Arguments { get; }
    private static bool ContainsDeferred(ConfigurationValue value) => value switch
    {
        ConfigurationDeferredValue => true,
        ConfigurationObject obj => obj.Members.Values.Any(ContainsDeferred),
        ConfigurationArray array => array.Items.Any(ContainsDeferred),
        _ => false
    };
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationDeferredValue value &&
        Formatter == value.Formatter && Arguments.Equals(value.Arguments);
    public override int GetHashCode() => HashCode.Combine(7, Formatter, Arguments);
}

/// <summary>One immutable ready renderer result; it contains no serialized output.</summary>
public sealed class ConfigurationFragment(ConfigurationValue value)
{
    public ConfigurationValue Value { get; } = value ?? throw new ArgumentNullException(nameof(value));
}
