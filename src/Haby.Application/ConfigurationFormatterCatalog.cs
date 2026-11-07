using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Application;

public enum ConfigurationFormatterResultKind { String }

/// <summary>Immutable semantic contract metadata. Derived records include all schema/semantic metadata
/// in record equality and implement pure validation over the typed tree, without secret resolution.</summary>
public abstract record ConfigurationFormatterContract
{
    protected ConfigurationFormatterContract(ConfigurationFormatterId id) => Id = id ?? throw new ArgumentNullException(nameof(id));
    public ConfigurationFormatterId Id { get; }
    public ConfigurationFormatterResultKind ResultKind => ConfigurationFormatterResultKind.String;
    public abstract bool ValidateArguments(ConfigurationObject arguments);
}

/// <summary>Active capability identity, separate from the public contract definition. No execution capability.</summary>
public sealed record ConfigurationFormatterImplementation(ManifestLocalId Identity, ConfigurationFormatterId Contract);

/// <summary>Explicit build-time registrations; construction checks conflicts and ambiguity.</summary>
public sealed class ConfigurationFormatterCatalog
{
    private readonly ImmutableDictionary<ConfigurationFormatterId, ConfigurationFormatterContract> _definitions;
    private readonly ImmutableHashSet<ConfigurationFormatterId> _active;
    private readonly ImmutableDictionary<ConfigurationFormatterId, ManifestLocalId> _identities;
    public bool Supports(ConfigurationFormatterId contract, ManifestLocalId identity) => _identities.TryGetValue(contract, out var actual) && actual == identity;
    public ConfigurationFormatterCatalog(IEnumerable<ConfigurationFormatterContract> definitions,
        IEnumerable<ConfigurationFormatterImplementation> implementations)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(implementations);
        var builder = ImmutableDictionary.CreateBuilder<ConfigurationFormatterId, ConfigurationFormatterContract>();
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (builder.TryGetValue(definition.Id, out var prior) && prior != definition)
                throw new ArgumentException("Conflicting formatter contract definitions.", nameof(definitions));
            builder[definition.Id] = definition;
        }
        _definitions = builder.ToImmutable();
        var registrations = implementations.ToArray();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentNullException.ThrowIfNull(registration.Identity);
            ArgumentNullException.ThrowIfNull(registration.Contract);
            if (!_definitions.ContainsKey(registration.Contract))
                throw new ArgumentException("Formatter implementation has no contract definition.", nameof(implementations));
        }
        if (registrations.GroupBy(item => item.Contract).Any(group => group.Count() > 1))
            throw new ArgumentException("Ambiguous active formatter implementations.", nameof(implementations));
        _identities = registrations.ToImmutableDictionary(x => x.Contract, x => x.Identity);
        _active = registrations.Select(item => item.Contract).ToImmutableHashSet();
    }
    internal string? Validate(ConfigurationDeferredValue value)
    {
        if (!_definitions.TryGetValue(value.Formatter, out var contract)) return "render.formatter.unknown";
        if (!_active.Contains(value.Formatter)) return "render.formatter.unavailable";
        try { return contract.ValidateArguments(value.Arguments) ? null : "render.formatter.arguments-invalid"; }
        catch (Exception) { return "render.formatter.arguments-invalid"; }
    }
}
