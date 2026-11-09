using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Adapter-owned pure semantics, separate from executable registrations.</summary>
public abstract record ConfigurationRendererContract(ConfigurationRendererId Id, ResourceTypeId Resource,
    string OutputContract, string OutputKey, DurableActionExecutionKey ProducerAction,
    ConfigurationFormatterId Formatter)
{
    public abstract ConfigurationObject Defaults { get; }
    public abstract bool ValidateOptions(ConfigurationObject options, bool complete);
    public abstract ConfigurationObject NormalizeOptions(ConfigurationObject options);
}

public interface IConfigurationRenderer
{
    ConfigurationRendererId Contract { get; }
    ManifestLocalId Identity { get; }
    ConfigurationFragment? Render(ExternalOperationOutput confirmedOutput, ConfigurationObject settings);
}

/// <summary>Pure formatting only. Supplied material is transient and cannot discover further references.</summary>
public interface IConfigurationFormatter
{
    ConfigurationFormatterId Contract { get; }
    ManifestLocalId Identity { get; }
    ConfidentialConfigurationString? Format(ConfigurationObject arguments, ReadOnlySpan<char> material);
}

/// <summary>Deliberately has no value equality, hash or material-bearing ToString.</summary>
public sealed class ConfidentialConfigurationString(string value)
{
    public string Reveal() => value;
    public override string ToString() => "[Confidential configuration]";
}

public sealed record ConfigurationRenderPin(ManifestLocalId Component, int Request,
    ConfigurationRendererId Renderer, ManifestLocalId RendererImplementation,
    ConfigurationFormatterId Formatter, ManifestLocalId FormatterImplementation,
    ResourceTypeId Resource, ManifestLocalId ResourceLocalId, string OutputContract, string OutputKey, OperationId Producer, DurableActionExecutionKey ProducerAction,
    ConfigurationObject Defaults, ConfigurationObject Options,
    ImmutableDictionary<string, ProfileOptionScope> OptionOrigins);

/// <summary>Explicit trusted build composition. No discovery, output access or material resolution.</summary>
public sealed class ConfigurationRenderingCatalog
{
    public static ConfigurationRenderingCatalog Empty { get; } = new([], [], new([], []), []);
    private readonly ImmutableDictionary<ConfigurationRendererId, ConfigurationRendererContract> _definitions;
    private readonly ImmutableDictionary<ConfigurationRendererId, IConfigurationRenderer> _renderers;
    private readonly ImmutableDictionary<ConfigurationFormatterId, IConfigurationFormatter> _formatters;
    private readonly ConfigurationFormatterCatalog _contracts;

    public ConfigurationRenderingCatalog(IEnumerable<ConfigurationRendererContract> definitions,
        IEnumerable<IConfigurationRenderer> renderers, ConfigurationFormatterCatalog contracts,
        IEnumerable<IConfigurationFormatter> formatters)
    {
        var definitionsArray = definitions.ToArray();
        if (definitionsArray.GroupBy(x => x.Id).Any(g => g.Distinct().Count() != 1))
            throw new ArgumentException("Conflicting renderer definitions.");
        _definitions = definitionsArray.Distinct().ToImmutableDictionary(x => x.Id);
        var rendererArray = renderers.ToArray(); var formatterArray = formatters.ToArray();
        if (rendererArray.GroupBy(x => x.Contract).Any(g => g.Count() != 1) ||
            formatterArray.GroupBy(x => x.Contract).Any(g => g.Count() != 1))
            throw new ArgumentException("Ambiguous active configuration implementations.");
        _contracts = contracts;
        if (rendererArray.Any(x => !_definitions.ContainsKey(x.Contract)) ||
            formatterArray.Any(x => !contracts.Supports(x.Contract, x.Identity)))
            throw new ArgumentException("Configuration implementation has no matching definition.");
        _renderers = rendererArray.ToImmutableDictionary(x => x.Contract);
        _formatters = formatterArray.ToImmutableDictionary(x => x.Contract);
    }

    public (ConfigurationRenderPin? Pin, string? Error) Prepare(ManifestLocalId component, int index,
        ProfileBindingRenderRequest request, NormalizedManifestSpec spec, IReadOnlyList<DurableOperationNode> producers)
    {
        try { return PrepareCore(component, index, request, spec, producers); }
        catch (Exception) { return (null, "render.options.invalid"); }
    }
    private (ConfigurationRenderPin? Pin, string? Error) PrepareCore(ManifestLocalId component, int index,
        ProfileBindingRenderRequest request, NormalizedManifestSpec spec, IReadOnlyList<DurableOperationNode> producers)
    {
        if (!_definitions.TryGetValue(request.Renderer, out var definition)) return (null, "render.renderer.unknown");
        if (!_renderers.TryGetValue(request.Renderer, out var renderer)) return (null, "render.renderer.unavailable");
        if (!_formatters.TryGetValue(definition.Formatter, out var formatter)) return (null, "render.formatter.unavailable");
        if (request.Binding.Target is not NormalizedLocalResourceTarget local ||
            !spec.Resources.TryGetValue(local.ResourceRef, out var resource) || resource.Type != definition.Resource)
            return (null, "render.output.incompatible");
        var matches = producers.Where(n => n.ResourceId == local.ResourceRef && n.Action == definition.ProducerAction).ToArray();
        if (matches.Length != 1) return (null, "render.producer.unavailable");
        if (!definition.ValidateOptions(request.ProfileOptions, false) || !definition.ValidateOptions(request.Binding.Options, false) ||
            !definition.ValidateOptions(definition.Defaults, true)) return (null, "render.options.invalid");
        var settings = definition.Defaults.Members.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var origins = settings.Keys.ToDictionary(x => x, _ => ProfileOptionScope.RendererDefault, StringComparer.Ordinal);
        foreach (var pair in request.Options.Members) { settings[pair.Key] = pair.Value; origins[pair.Key] = request.OptionOrigins[pair.Key]; }
        var complete = new ConfigurationObject(settings);
        if (!definition.ValidateOptions(complete, true)) return (null, "render.options.invalid");
        return (new(component, index, request.Renderer, renderer.Identity, definition.Formatter, formatter.Identity,
            definition.Resource, local.ResourceRef, definition.OutputContract, definition.OutputKey, matches[0].Id, definition.ProducerAction,
            definition.NormalizeOptions(definition.Defaults), definition.NormalizeOptions(complete), origins.ToImmutableDictionary(StringComparer.Ordinal)), null);
    }

    public bool Supports(ConfigurationRenderPin pin)
    {
        try { return SupportsCore(pin); }
        catch (Exception) { return false; }
    }
    private bool SupportsCore(ConfigurationRenderPin pin) => _definitions.TryGetValue(pin.Renderer, out var d) &&
        _renderers.TryGetValue(pin.Renderer, out var r) && r.Identity == pin.RendererImplementation &&
        _formatters.TryGetValue(pin.Formatter, out var f) && f.Identity == pin.FormatterImplementation &&
        d.Formatter == pin.Formatter && d.Resource == pin.Resource && d.OutputContract == pin.OutputContract &&
        d.OutputKey == pin.OutputKey && d.ProducerAction == pin.ProducerAction && d.ValidateOptions(pin.Defaults, true) && d.ValidateOptions(pin.Options, true);

    public ConfigurationFragment? Render(ConfigurationRenderPin pin, ExternalOperationOutput confirmedOutput)
    {
        if (!Supports(pin) || confirmedOutput.Contract != pin.OutputContract || confirmedOutput.Key != pin.OutputKey) return null;
        try
        {
            var fragment = _renderers[pin.Renderer].Render(confirmedOutput, pin.Options);
            return fragment?.Value is ConfigurationDeferredValue value && value.Formatter == pin.Formatter &&
                _contracts.Validate(value) is null ? fragment : null;
        }
        catch (Exception) { return null; }
    }

    public ConfidentialConfigurationString? Format(ConfigurationRenderPin pin, ConfigurationDeferredValue value, ReadOnlySpan<char> material)
    {
        if (!Supports(pin) || value.Formatter != pin.Formatter || _contracts.Validate(value) is not null || material.IsEmpty) return null;
        try { return _formatters[pin.Formatter].Format(value.Arguments, material); }
        catch (Exception) { return null; }
    }

    /// <summary>Delivery requires exact formatter and pure contract metadata, never a renderer instance.</summary>
    public bool SupportsDeliveryFormatter(ConfigurationRenderPin pin) => _definitions.TryGetValue(pin.Renderer, out var d) &&
        d.Formatter == pin.Formatter && d.Resource == pin.Resource && d.OutputContract == pin.OutputContract && d.OutputKey == pin.OutputKey &&
        d.ProducerAction == pin.ProducerAction && d.ValidateOptions(pin.Options, true) &&
        _formatters.TryGetValue(pin.Formatter, out var f) && f.Identity == pin.FormatterImplementation;

    public ConfidentialConfigurationString? FormatDelivery(ConfigurationRenderPin pin, ConfigurationDeferredValue value, ReadOnlySpan<char> material)
    {
        if (!SupportsDeliveryFormatter(pin) || value.Formatter != pin.Formatter || _contracts.Validate(value) is not null) return null;
        try { return _formatters[pin.Formatter].Format(value.Arguments, material); }
        catch (Exception) { return null; }
    }

}

/// <summary>Exact accepted evidence selection. Implementations must check successful producer attempt/output atomically.</summary>
public interface IConfigurationOutputEvidence
{
    ValueTask<ExternalOperationOutput?> ReadAsync(DurableOperationKey producer, ManagedResourceKey owner,
        DurableActionExecutionKey action, string contract, string key, CancellationToken cancellationToken);
}

public sealed class ConfigurationOutputRenderer(ConfigurationRenderingCatalog catalog, IConfigurationOutputEvidence evidence)
{
    public async ValueTask<ConfigurationFragment?> RenderAsync(ConfigurationRenderPin pin, ApplicationDeploymentId deployment,
        DeploymentGeneration generation, ManagedResourceKey owner, CancellationToken cancellationToken)
    {
        if (!catalog.Supports(pin) || owner.ResourceLocalId != pin.ResourceLocalId) return null;
        var output = await evidence.ReadAsync(new(deployment, generation, pin.Producer), owner, pin.ProducerAction,
            pin.OutputContract, pin.OutputKey, cancellationToken);
        return output is null ? null : catalog.Render(pin, output);
    }
}
