using Haby.Domain;

namespace Haby.Application;

internal static class ProfileSecretInsertion
{
    internal static ConfigurationValue Apply(ConfigurationValue value, IReadOnlyList<ProfileSecretProjection> projections,
        IReadOnlyDictionary<ProfileNamedSecretRef, SecretRef> frozen)
    {
        foreach (var projection in projections)
        {
            var parts = projection.Path[1..].Split('/').Select(x => x.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            if (!frozen.TryGetValue(projection.SecretRef, out var reference)) throw new InvalidDataException();
            value = Insert(value, parts, 0, new ConfigurationProjectedSecret(reference, projection.Format));
        }
        return value;
    }

    private static ConfigurationValue Insert(ConfigurationValue current, string[] parts, int at, ConfigurationProjectedSecret secret)
    {
        if (current is ConfigurationArray array)
        {
            if (at == parts.Length - 1 || !int.TryParse(parts[at], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index) || index < 0 || index >= array.Items.Count ||
                parts[at] != index.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidDataException();
            var items = array.Items.ToArray();
            items[index] = Insert(items[index], parts, at + 1, secret);
            return new ConfigurationArray(items);
        }
        if (current is not ConfigurationObject obj) throw new InvalidDataException();
        var members = obj.Members.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var key = parts[at];
        if (at == parts.Length - 1)
        {
            if (!members.TryAdd(key, secret)) throw new InvalidDataException();
        }
        else members[key] = Insert(members.TryGetValue(key, out var next) ? next : new ConfigurationObject([]), parts, at + 1, secret);
        return new ConfigurationObject(members);
    }
}
