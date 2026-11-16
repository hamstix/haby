namespace Haby.Domain;

/// <summary>Frozen exact configuration-secret insertion; contains no material.</summary>
public sealed class ConfigurationProjectedSecret(SecretRef reference, ProfileSecretFormat format) : ConfigurationValue
{
    public SecretRef Reference { get; } = reference ?? throw new ArgumentNullException(nameof(reference));
    public ProfileSecretFormat Format { get; } = format is ProfileSecretFormat.String or ProfileSecretFormat.Json
        ? format : throw new ArgumentOutOfRangeException(nameof(format));
    public override bool Equals(ConfigurationValue? other) => other is ConfigurationProjectedSecret value &&
        Reference == value.Reference && Format == value.Format;
    public override int GetHashCode() => HashCode.Combine(8, Reference, Format);
}
