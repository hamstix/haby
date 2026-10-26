namespace Haby.Domain;

/// <summary>The Haby-issued identity of one Environment-owned Provider target.</summary>
public sealed record ProviderInstanceId
{
    private readonly Guid _value;
    private ProviderInstanceId(Guid value) => _value = value;
    public static ProviderInstanceId New() => new(Guid.CreateVersion7());
    public static ProviderInstanceId Parse(string value) => new(UuidV7.Parse(value));
    public override string ToString() => _value.ToString("D");
}

/// <summary>A positive, instance-local configuration revision number.</summary>
public sealed record ProviderConfigurationRevisionNumber
{
    private ProviderConfigurationRevisionNumber(long value) => Value = value;
    public long Value { get; }
    public static ProviderConfigurationRevisionNumber Create(long value) => value > 0 ? new(value) :
        throw new ArgumentOutOfRangeException(nameof(value));
}
