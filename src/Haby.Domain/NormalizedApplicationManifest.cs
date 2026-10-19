namespace Haby.Domain;

/// <summary>
/// An immutable, versioned Application declaration. Its tree contains only normalized
/// declaration content; import-envelope values and validation reports are separate.
/// </summary>
public sealed class NormalizedApplicationManifest : IEquatable<NormalizedApplicationManifest>
{
    internal NormalizedApplicationManifest(string inputApiVersion, ApplicationId applicationId, ConfigurationObject declaration)
    {
        InputApiVersion = inputApiVersion;
        ApplicationId = applicationId;
        Declaration = declaration;
    }

    public const int CurrentModelVersion = 1;
    public int NormalizedModelVersion => CurrentModelVersion;
    public string InputApiVersion { get; }
    public ApplicationId ApplicationId { get; }
    public ConfigurationObject Declaration { get; }

    public bool Equals(NormalizedApplicationManifest? other) => other is not null &&
        NormalizedModelVersion == other.NormalizedModelVersion &&
        string.Equals(InputApiVersion, other.InputApiVersion, StringComparison.Ordinal) &&
        Declaration.Equals(other.Declaration);

    public override bool Equals(object? obj) => obj is NormalizedApplicationManifest other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(NormalizedModelVersion,
        StringComparer.Ordinal.GetHashCode(InputApiVersion), Declaration);
}
