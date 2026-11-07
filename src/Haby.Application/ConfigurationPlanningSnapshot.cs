using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Absence is an exact input, distinct from a persisted empty local decision set.</summary>
public sealed record ConfigurationPlanningScope(ProfileAssignmentTarget Target, AssignmentToken? Token,
    ConfigurationProfileAssignment Declaration);

/// <summary>Frozen structural layouts and exact renderer readiness; grants no secret resolution or delivery permission.</summary>
public sealed class ConfigurationPlanningSnapshot : IEquatable<ConfigurationPlanningSnapshot>
{
    private ConfigurationPlanningSnapshot(ManifestRevisionRef revision, NormalizedApplicationManifest manifest,
        ImmutableArray<ConfigurationPlanningScope> scopes, ImmutableArray<ConfigurationProfileRevision> profiles,
        IReadOnlyList<EffectiveComponentLayout> layouts, IReadOnlyList<ConfigurationRenderPin> pins)
    {
        Revision = revision; Manifest = manifest; Scopes = scopes; Profiles = profiles; Layouts = layouts; RenderPins = pins;
    }

    public IReadOnlyList<ConfigurationRenderPin> RenderPins { get; }
    public ManifestRevisionRef Revision { get; }
    public NormalizedApplicationManifest Manifest { get; }
    public IReadOnlyList<ConfigurationPlanningScope> Scopes { get; }
    public IReadOnlyList<ConfigurationProfileRevision> Profiles { get; }
    public IReadOnlyList<EffectiveComponentLayout> Layouts { get; }

    public static IReadOnlyList<ProfileAssignmentTarget> Targets(NormalizedApplicationManifest manifest) =>
        new ProfileAssignmentTarget[] { new ProfileAssignmentTarget.Environment(), new ProfileAssignmentTarget.Application(manifest.ApplicationId) }
            .Concat(manifest.Spec.Components.Keys.OrderBy(x => x.Value, StringComparer.Ordinal)
                .Select(x => new ProfileAssignmentTarget.Component(manifest.ApplicationId, x))).ToImmutableArray();

    public static (ConfigurationPlanningSnapshot? Snapshot, ProfileExpansionResult Expansion) Prepare(
        ManifestRevisionRef revision, NormalizedApplicationManifest manifest,
        IEnumerable<ConfigurationPlanningScope> scopes, IEnumerable<ConfigurationProfileRevision> profiles,
        ConfigurationRenderingCatalog? rendering = null) => PrepareCore(revision, manifest, scopes, profiles, rendering);

    internal static (ConfigurationPlanningSnapshot? Snapshot, ProfileExpansionResult Expansion) PrepareCore(
        ManifestRevisionRef revision, NormalizedApplicationManifest manifest,
        IEnumerable<ConfigurationPlanningScope> scopes, IEnumerable<ConfigurationProfileRevision> profiles,
        ConfigurationRenderingCatalog? rendering = null, IReadOnlyList<ConfigurationRenderPin>? frozenPins = null)
    {
        var targets = Targets(manifest);
        var local = scopes.ToImmutableArray();
        if (local.Length != targets.Count || local.Select(x => x.Target).Distinct().Count() != targets.Count ||
            local.Any(x => !targets.Contains(x.Target) || x.Declaration.Target != x.Target ||
                x.Token is null && x.Declaration.Decisions.Count != 0))
            throw new InvalidDataException("Configuration scope inputs are inconsistent.");
        local = targets.Select(t => local.Single(x => x.Target == t)).ToImmutableArray();
        var content = profiles.OrderBy(x => x.Reference.ProfileId.Value, StringComparer.Ordinal).ThenBy(x => x.Reference.Revision).ToImmutableArray();
        var required = local.SelectMany(x => x.Declaration.Decisions).OfType<ProfileDecision.Use>().Select(x => x.Reference).Distinct().ToArray();
        if (content.Select(x => x.Reference).Distinct().Count() != content.Length ||
            content.Length != required.Length || required.Any(x => !content.Any(p => p.Reference == x)))
            throw new InvalidDataException("Configuration Profile inputs are unavailable.");
        var result = new ConfigurationProfileExpander(new(content)).Expand(local[0].Declaration.Decisions,
            local[1].Declaration.Decisions, local.Skip(2).Select(x =>
            {
                var target = (ProfileAssignmentTarget.Component)x.Target;
                return new ProfileComponentInput(target.ComponentRef, manifest.Spec.Components[target.ComponentRef], x.Declaration.Decisions);
            }));
        if (!result.IsSuccess) return (null, result);
        var pins = new List<ConfigurationRenderPin>();
        var errors = new List<ProfileExpansionDiagnostic>();
        foreach (var layout in result.Layouts!)
            for (var i = 0; i < layout.BindingRequests.Count; i++)
            {
                var request = layout.BindingRequests[i];
                if (frozenPins is not null)
                {
                    var pin = frozenPins.SingleOrDefault(x => x.Component == layout.Component && x.Request == i)
                        ?? throw new InvalidDataException();
                    ConfigurationRenderPinCodec.ValidateFrozen(pin, request, manifest.Spec);
                    pins.Add(pin);
                }
                else
                {
                    var resolved = (rendering ?? ConfigurationRenderingCatalog.Empty).Prepare(layout.Component, i, request, manifest.Spec);
                    if (resolved.Pin is { } pin) pins.Add(pin);
                    else errors.Add(new(resolved.Error!, layout.Component.Value, "", "", request.BindingId.Value));
                }
            }
        if (frozenPins is not null && frozenPins.Count != pins.Count) throw new InvalidDataException();
        if (errors.Count != 0) return (null, new([], errors));
        return (new(revision, manifest, local, content, result.Layouts!, pins.ToImmutableArray()), result);
    }

    public bool Equals(ConfigurationPlanningSnapshot? other) => other is not null &&
        ConfigurationPlanningSnapshotCodec.Encode(this) == ConfigurationPlanningSnapshotCodec.Encode(other);
    public override bool Equals(object? obj) => obj is ConfigurationPlanningSnapshot other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ConfigurationPlanningSnapshotCodec.Encode(this));
}
