using System.Collections.Immutable;

namespace Haby.Domain;

/// <summary>A portable Profile identity with the Application ID lexical rules.</summary>
public sealed record ProfileId
{
    private ProfileId(string value) => Value = value;
    public string Value { get; }
    public static ProfileId Create(string value) => new(ApplicationId.Create(value).Value);
    public override string ToString() => Value;
}

public sealed record ProfileRevisionRef
{
    public ProfileRevisionRef(ProfileId profileId, int revision)
    {
        ProfileId = profileId ?? throw new ArgumentNullException(nameof(profileId));
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
    }
    public ProfileId ProfileId { get; }
    public int Revision { get; }
}

public abstract record ProfileDecision
{
    private protected ProfileDecision(ProfileId profileId) => ProfileId = profileId ?? throw new ArgumentNullException(nameof(profileId));
    public ProfileId ProfileId { get; }
    public sealed record Use : ProfileDecision
    {
        public Use(ProfileRevisionRef reference) : base(reference.ProfileId) => Reference = reference;
        public ProfileRevisionRef Reference { get; }
    }
    public sealed record Disable : ProfileDecision
    {
        public Disable(ProfileId profileId) : base(profileId) { }
    }
}

/// <summary>Static semantic inputs only. Expansion refuses unsupported formats and confidential nodes.</summary>
public sealed record ProfileDocument
{
    public ProfileDocument(ManifestLocalId id, string fileName, StructuredDocumentFormat format, ConfigurationValue template)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        FileName = fileName; Format = format;
        Template = template ?? throw new ArgumentNullException(nameof(template));
    }
    public ManifestLocalId Id { get; }
    public string FileName { get; }
    public StructuredDocumentFormat Format { get; }
    public ConfigurationValue Template { get; }
}
/// <summary>A named operator secret reference, without resolved identity or material.</summary>
public sealed record ProfileNamedSecretRef
{
    public ProfileNamedSecretRef(string name, int version)
    {
        Name = EnvironmentId.Create(name).Value;
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
    }
    public string Name { get; }
    public int Version { get; }
}
public enum ProfileSecretFormat { String, Json }
public sealed record ProfileSecretProjection
{
    public ProfileSecretProjection(string path, ProfileSecretFormat format, ProfileNamedSecretRef secretRef)
    {
        ProfileSecretPaths.Decode(path);
        if (format is not (ProfileSecretFormat.String or ProfileSecretFormat.Json)) throw new ArgumentOutOfRangeException(nameof(format));
        Path = path; Format = format;
        SecretRef = secretRef ?? throw new ArgumentNullException(nameof(secretRef));
    }
    public string Path { get; }
    public ProfileSecretFormat Format { get; }
    public ProfileNamedSecretRef SecretRef { get; }
}
public sealed class ProfileStaticContribution : IEquatable<ProfileStaticContribution>
{
    public ProfileStaticContribution(ManifestLocalId id, ManifestLocalId document, string path, ConfigurationValue value,
        IEnumerable<ProfileSecretProjection>? secretProjections = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        SecretProjections = ProfileCollections.Copy(secretProjections ?? []);
        ProfileSecretPaths.Validate(value, SecretProjections);
    }
    public ManifestLocalId Id { get; }
    public ManifestLocalId Document { get; }
    public string Path { get; }
    public ConfigurationValue Value { get; }
    public IReadOnlyList<ProfileSecretProjection> SecretProjections { get; }
    public bool Equals(ProfileStaticContribution? other) => other is not null && Id == other.Id && Document == other.Document &&
        Path == other.Path && Value.Equals(other.Value) && ProfileCollections.Equal(SecretProjections, other.SecretProjections);
    public override bool Equals(object? obj) => obj is ProfileStaticContribution other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Id, Document, Path, Value, ProfileCollections.Hash(SecretProjections));
}
internal static class ProfileSecretPaths
{
    internal static string[] Decode(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') throw new ArgumentException("Invalid secret projection path.");
        var parts = path[1..].Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var encoded = parts[i];
            parts[i] = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (parts[i].Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal) != encoded)
                throw new ArgumentException("Invalid secret projection path.");
        }
        return parts;
    }
    internal static void Validate(ConfigurationValue value, IEnumerable<ProfileSecretProjection> projections)
    {
        var paths = projections.Select(x => Decode(x.Path)).ToArray();
        foreach (var parts in paths)
        {
            var current = value;
            for (var i = 0; i < parts.Length; i++)
            {
                if (current is not ConfigurationObject obj) throw new ArgumentException("Secret projection insertion is blocked.");
                if (!obj.Members.TryGetValue(parts[i], out var next)) break;
                if (i == parts.Length - 1) throw new ArgumentException("Secret projection insertion is blocked.");
                current = next;
            }
        }
        for (var i = 0; i < paths.Length; i++)
            for (var j = i + 1; j < paths.Length; j++)
                if (Prefix(paths[i], paths[j]) || Prefix(paths[j], paths[i])) throw new ArgumentException("Secret projection paths conflict.");
    }
    private static bool Prefix(string[] a, string[] b) => a.Length <= b.Length && a.SequenceEqual(b.Take(a.Length), StringComparer.Ordinal);
}

/// <summary>Portable target identity. The trusted Environment context is deliberately absent.</summary>
public abstract record ProfileAssignmentTarget
{
    private protected ProfileAssignmentTarget() { }
    public sealed record Environment : ProfileAssignmentTarget;
    public sealed record Application : ProfileAssignmentTarget
    {
        public Application(ApplicationId applicationId) => ApplicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        public ApplicationId ApplicationId { get; }
    }
    public sealed record Component : ProfileAssignmentTarget
    {
        public Component(ApplicationId applicationId, ManifestLocalId componentRef)
        {
            ApplicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
            ComponentRef = componentRef ?? throw new ArgumentNullException(nameof(componentRef));
        }
        public ApplicationId ApplicationId { get; }
        public ManifestLocalId ComponentRef { get; }
    }
}
public sealed class ConfigurationProfileAssignment : IEquatable<ConfigurationProfileAssignment>
{
    public ConfigurationProfileAssignment(ProfileAssignmentTarget target, IEnumerable<ProfileDecision> decisions)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Decisions = ProfileCollections.Copy(decisions);
        if (Decisions.Count > 64 || Decisions.Select(x => x.ProfileId).Distinct().Count() != Decisions.Count)
            throw new ArgumentException("Invalid Profile decisions.", nameof(decisions));
    }
    public ProfileAssignmentTarget Target { get; }
    public IReadOnlyList<ProfileDecision> Decisions { get; }
    public bool Equals(ConfigurationProfileAssignment? other) => other is not null && Target == other.Target && ProfileCollections.Equal(Decisions, other.Decisions);
    public override bool Equals(object? obj) => obj is ConfigurationProfileAssignment other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Target, ProfileCollections.Hash(Decisions));
}
public sealed record ProfileProjection
{
    public ProfileProjection(ManifestLocalId document, string path)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }
    public ManifestLocalId Document { get; }
    public string Path { get; }
}

public sealed class ProfileBindingRule : IEquatable<ProfileBindingRule>
{
    public ProfileBindingRule(ManifestLocalId id, ManifestLocalId slot, ConfigurationRendererId renderer,
        ConfigurationObject defaults, IEnumerable<ProfileProjection> projections)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Slot = slot ?? throw new ArgumentNullException(nameof(slot));
        Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        Defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        Projections = ProfileCollections.Copy(projections);
        if (Projections.Count == 0) throw new ArgumentException("At least one projection is required.", nameof(projections));
    }
    public ManifestLocalId Id { get; }
    public ManifestLocalId Slot { get; }
    public ConfigurationRendererId Renderer { get; }
    public ConfigurationObject Defaults { get; }
    public IReadOnlyList<ProfileProjection> Projections { get; }
    public bool Equals(ProfileBindingRule? other) => other is not null && Id == other.Id && Slot == other.Slot &&
        Renderer == other.Renderer && Defaults.Equals(other.Defaults) && ProfileCollections.Equal(Projections, other.Projections);
    public override bool Equals(object? obj) => obj is ProfileBindingRule other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Id, Slot, Renderer, Defaults, ProfileCollections.Hash(Projections));
}

/// <summary>Immutable structural content. Collection order is not behavior or precedence; multiplicity is retained.</summary>
public sealed class ConfigurationProfileRevision : IEquatable<ConfigurationProfileRevision>
{
    public ConfigurationProfileRevision(ProfileRevisionRef reference, IEnumerable<ProfileDocument> documents,
        IEnumerable<ProfileStaticContribution> contributions, IEnumerable<ProfileBindingRule> rules)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Documents = ProfileCollections.Copy(documents);
        Contributions = ProfileCollections.Copy(contributions);
        Rules = ProfileCollections.Copy(rules);
    }
    public ProfileRevisionRef Reference { get; }
    public IReadOnlyList<ProfileDocument> Documents { get; }
    public IReadOnlyList<ProfileStaticContribution> Contributions { get; }
    public IReadOnlyList<ProfileBindingRule> Rules { get; }
    public bool Equals(ConfigurationProfileRevision? other) => other is not null && Reference == other.Reference &&
        ProfileCollections.Equal(Documents, other.Documents) && ProfileCollections.Equal(Contributions, other.Contributions) &&
        ProfileCollections.Equal(Rules, other.Rules);
    public override bool Equals(object? obj) => obj is ConfigurationProfileRevision other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Reference, ProfileCollections.Hash(Documents),
        ProfileCollections.Hash(Contributions), ProfileCollections.Hash(Rules));
}

internal static class ProfileCollections
{
    internal static ImmutableArray<T> Copy<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToImmutableArray();
        if (result.Any(x => x is null)) throw new ArgumentException("Items cannot be null.", nameof(values));
        return result;
    }
    internal static bool Equal<T>(IEnumerable<T> left, IEnumerable<T> right)
    {
        var remaining = right.ToList();
        foreach (var value in left)
        {
            var index = remaining.FindIndex(x => EqualityComparer<T>.Default.Equals(x, value));
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }
        return remaining.Count == 0;
    }
    internal static int Hash<T>(IEnumerable<T> values)
    {
        var hash = 0;
        foreach (var value in values) hash = unchecked(hash + EqualityComparer<T>.Default.GetHashCode(value!));
        return hash;
    }
}
