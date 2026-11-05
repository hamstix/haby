using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Exact immutable catalog; equivalent content is idempotent, conflicting identities refuse construction.</summary>
public sealed class ConfigurationProfileCatalog
{
    private readonly ImmutableDictionary<ProfileRevisionRef, ConfigurationProfileRevision> _revisions;
    public ConfigurationProfileCatalog(IEnumerable<ConfigurationProfileRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        var builder = ImmutableDictionary.CreateBuilder<ProfileRevisionRef, ConfigurationProfileRevision>();
        foreach (var revision in revisions)
        {
            ArgumentNullException.ThrowIfNull(revision);
            if (builder.TryGetValue(revision.Reference, out var existing) && !existing.Equals(revision))
                throw new ArgumentException("Conflicting Profile revision content.", nameof(revisions));
            builder[revision.Reference] = revision;
        }
        _revisions = builder.ToImmutable();
    }
    public ConfigurationProfileRevision? Find(ProfileRevisionRef reference) => _revisions.GetValueOrDefault(reference);
}

public enum ProfileSelectionScope { Environment, Application, Component }
public enum ProfileOptionScope { Profile, Binding }
public sealed record EffectiveProfileDecision(ProfileDecision Decision, ProfileSelectionScope Scope);
public sealed record ConfigurationLayoutOrigin(ProfileRevisionRef? Profile, ManifestLocalId Source);
public sealed record BindingSlotAssociation(ManifestLocalId Binding, ManifestLocalId Slot);
public sealed record ProfileExpansionDiagnostic(string Code, string Component, string Document, string Path, string Source);

/// <summary>Already parsed Component plus author-declared slots. No new manifest fields or inferred associations.</summary>
public sealed class ProfileComponentInput
{
    public ProfileComponentInput(ManifestLocalId id, NormalizedComponent component, IEnumerable<ProfileDecision> decisions,
        IEnumerable<BindingSlotAssociation> slots, IEnumerable<ProfileStaticContribution>? contributions = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Component = component ?? throw new ArgumentNullException(nameof(component));
        Decisions = Snapshot(decisions);
        Slots = Snapshot(slots);
        Contributions = Snapshot(contributions ?? []);
    }
    private static ImmutableArray<T> Snapshot<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToImmutableArray();
        if (result.Any(x => x is null)) throw new ArgumentException("Items cannot be null.", nameof(values));
        return result;
    }
    public ManifestLocalId Id { get; }
    public NormalizedComponent Component { get; }
    public IReadOnlyList<ProfileDecision> Decisions { get; }
    public IReadOnlyList<BindingSlotAssociation> Slots { get; }
    public IReadOnlyList<ProfileStaticContribution> Contributions { get; }
}

public sealed record EffectiveProfileDocument(ProfileDocument Definition, ConfigurationLayoutOrigin Origin);
public sealed record EffectiveStaticContribution(ProfileStaticContribution Contribution, ConfigurationLayoutOrigin Origin);

/// <summary>An unresolved exact request. Expansion grants no output, renderer or option compatibility permission.</summary>
public sealed class ProfileBindingRenderRequest
{
    internal ProfileBindingRenderRequest(ManifestLocalId bindingId, NormalizedBinding binding, ManifestLocalId? slot,
        ConfigurationRendererId renderer, ConfigurationObject options, IEnumerable<ProfileProjection> projections,
        ConfigurationLayoutOrigin origin, IEnumerable<KeyValuePair<string, ProfileOptionScope>> optionOrigins)
    {
        BindingId = bindingId; Binding = binding; Slot = slot; Renderer = renderer; Options = options;
        Projections = projections.OrderBy(x => x.Document.Value, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.Ordinal).ToImmutableArray();
        Origin = origin; OptionOrigins = optionOrigins.ToImmutableDictionary(StringComparer.Ordinal);
    }
    public ManifestLocalId BindingId { get; }
    public NormalizedBinding Binding { get; }
    public ManifestLocalId? Slot { get; }
    public ConfigurationRendererId Renderer { get; }
    public ConfigurationObject Options { get; }
    public IReadOnlyList<ProfileProjection> Projections { get; }
    public ConfigurationLayoutOrigin Origin { get; }
    public IReadOnlyDictionary<string, ProfileOptionScope> OptionOrigins { get; }
}

public sealed class EffectiveComponentLayout
{
    internal EffectiveComponentLayout(ManifestLocalId component, IEnumerable<EffectiveProfileDecision> decisions,
        IEnumerable<EffectiveProfileDecision> scopeDecisions, IEnumerable<EffectiveProfileDocument> documents,
        IEnumerable<EffectiveStaticContribution> contributions, IEnumerable<ProfileBindingRenderRequest> requests)
    {
        Component = component;
        Decisions = decisions.OrderBy(x => x.Decision.ProfileId.Value, StringComparer.Ordinal).ToImmutableArray();
        ScopeDecisions = scopeDecisions.OrderBy(x => x.Scope).ThenBy(x => x.Decision.ProfileId.Value, StringComparer.Ordinal).ToImmutableArray();
        Documents = documents.OrderBy(x => x.Definition.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        Contributions = contributions.OrderBy(x => x.Contribution.Document.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Contribution.Path, StringComparer.Ordinal).ToImmutableArray();
        BindingRequests = requests.OrderBy(x => x.BindingId.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Renderer.Value, StringComparer.Ordinal).ThenBy(x => x.Slot?.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Projections[0].Document.Value, StringComparer.Ordinal).ThenBy(x => x.Projections[0].Path, StringComparer.Ordinal).ToImmutableArray();
    }
    public ManifestLocalId Component { get; }
    public IReadOnlyList<EffectiveProfileDecision> Decisions { get; }
    public IReadOnlyList<EffectiveProfileDecision> ScopeDecisions { get; }
    public IReadOnlyList<EffectiveProfileDocument> Documents { get; }
    public IReadOnlyList<EffectiveStaticContribution> Contributions { get; }
    public IReadOnlyList<ProfileBindingRenderRequest> BindingRequests { get; }
}

public sealed class ProfileExpansionResult
{
    internal ProfileExpansionResult(IEnumerable<EffectiveComponentLayout> layouts, IEnumerable<ProfileExpansionDiagnostic> errors)
    {
        var ordered = errors.Distinct().OrderBy(x => x.Component, StringComparer.Ordinal).ThenBy(x => x.Document, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Source, StringComparer.Ordinal).ToArray();
        ErrorCount = ordered.Length;
        Diagnostics = ordered.Take(100).ToImmutableArray();
        Layouts = ErrorCount == 0 ? layouts.OrderBy(x => x.Component.Value, StringComparer.Ordinal).ToImmutableArray() : null;
    }
    public bool IsSuccess => Layouts is not null;
    public IReadOnlyList<EffectiveComponentLayout>? Layouts { get; }
    public IReadOnlyList<ProfileExpansionDiagnostic> Diagnostics { get; }
    public int ErrorCount { get; }
    public bool IsTruncated => ErrorCount > Diagnostics.Count;
}

/// <summary>Pure whole-Profile selection and static layout expansion, without rendering, I/O or planning integration.</summary>
public sealed class ConfigurationProfileExpander(ConfigurationProfileCatalog catalog)
{
    private readonly ConfigurationProfileCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    public ProfileExpansionResult Expand(IEnumerable<ProfileDecision> environment, IEnumerable<ProfileDecision> application,
        IEnumerable<ProfileComponentInput> components)
    {
        var environmentInputs = environment.ToImmutableArray();
        var applicationInputs = application.ToImmutableArray();
        var inputs = components.ToImmutableArray();
        var errors = new List<ProfileExpansionDiagnostic>();
        var layouts = new List<EffectiveComponentLayout>();
        foreach (var duplicate in inputs.GroupBy(x => x.Id).Where(x => x.Count() > 1))
            errors.Add(new("profile.component.duplicate", duplicate.Key.Value, "", "", ""));
        // Validate scopes even if no Components were supplied.
        ValidateDecisions(environmentInputs, ProfileSelectionScope.Environment, "", errors);
        ValidateDecisions(applicationInputs, ProfileSelectionScope.Application, "", errors);
        foreach (var input in inputs)
        {
            var component = input.Id.Value;
            void Error(string code, string document = "", string path = "", string source = "") => errors.Add(new(code, component, document, path, source));
            ValidateDecisions(input.Decisions, ProfileSelectionScope.Component, component, errors);
            var history = environmentInputs.Select(x => new EffectiveProfileDecision(x, ProfileSelectionScope.Environment))
                .Concat(applicationInputs.Select(x => new EffectiveProfileDecision(x, ProfileSelectionScope.Application)))
                .Concat(input.Decisions.Select(x => new EffectiveProfileDecision(x, ProfileSelectionScope.Component))).ToArray();
            var effective = new Dictionary<ProfileId, EffectiveProfileDecision>();
            foreach (var scope in history.GroupBy(x => x.Scope).OrderBy(x => x.Key))
                foreach (var group in scope.GroupBy(x => x.Decision.ProfileId))
                    if (group.Count() == 1) effective[group.Key] = group.Single();
            var documents = new List<EffectiveProfileDocument>();
            var contributions = new List<EffectiveStaticContribution>();
            var rules = new List<(ProfileBindingRule Rule, ConfigurationLayoutOrigin Origin)>();
            var requests = new List<ProfileBindingRenderRequest>();
            foreach (var decision in effective.Values.OrderBy(x => x.Decision.ProfileId.Value, StringComparer.Ordinal))
            {
                if (decision.Decision is not ProfileDecision.Use use) continue;
                if (_catalog.Find(use.Reference) is not { } profile)
                {
                    Error("profile.revision.not-found", source: use.Reference.ProfileId.Value);
                    continue;
                }
                foreach (var document in profile.Documents) documents.Add(new(document, new(profile.Reference, document.Id)));
                foreach (var contribution in profile.Contributions) contributions.Add(new(contribution, new(profile.Reference, contribution.Id)));
                foreach (var rule in profile.Rules)
                {
                    rules.Add((rule, new(profile.Reference, rule.Id)));
                    if (!Ordinary(rule.Defaults)) Error("profile.value.unsupported", source: Source(new(profile.Reference, rule.Id)));
                }
                DuplicateIds(profile.Contributions.Select(x => x.Id), "profile.contribution.duplicate", profile.Reference.ProfileId.Value);
                DuplicateIds(profile.Rules.Select(x => x.Id), "profile.rule.duplicate", profile.Reference.ProfileId.Value);
            }
            foreach (var (id, document) in input.Component.Documents)
            {
                if (document is NormalizedStructuredDocument structured)
                    documents.Add(new(new(id, structured.FileName, structured.Format, structured.Template), new(null, id)));
                else Error("profile.document.unsupported", id.Value);
            }
            contributions.AddRange(input.Contributions.Select(x => new EffectiveStaticContribution(x, new(null, x.Id))));
            DuplicateIds(input.Contributions.Select(x => x.Id), "profile.contribution.duplicate", "application");
            foreach (var group in documents.GroupBy(x => x.Definition.Id).Where(x => x.Count() > 1)) Error("profile.document.duplicate", group.Key.Value);
            foreach (var group in documents.GroupBy(x => x.Definition.FileName, StringComparer.Ordinal).Where(x => x.Count() > 1))
                foreach (var document in group) Error("profile.document.file-conflict", document.Definition.Id.Value);
            foreach (var document in documents)
            {
                if (!ValidFileName(document.Definition.FileName)) Error("profile.document.invalid-file-name", document.Definition.Id.Value);
                if (document.Definition.Format != StructuredDocumentFormat.Json) Error("profile.document.unsupported", document.Definition.Id.Value);
                if (!Ordinary(document.Definition.Template)) Error("profile.value.unsupported", document.Definition.Id.Value, source: Source(document.Origin));
            }
            foreach (var contribution in contributions)
                if (!Ordinary(contribution.Contribution.Value)) Error("profile.value.unsupported", contribution.Contribution.Document.Value, source: Source(contribution.Origin));
            foreach (var slot in input.Slots.GroupBy(x => x.Slot))
            {
                if (slot.Count() != 1) { Error("profile.slot.duplicate", source: slot.Key.Value); continue; }
                var association = slot.Single();
                if (!input.Component.Bindings.TryGetValue(association.Binding, out var binding))
                { Error("profile.binding.not-found", source: association.Binding.Value); continue; }
                var matches = rules.Where(x => x.Rule.Slot == slot.Key).ToArray();
                if (matches.Length != 1)
                { Error(matches.Length == 0 ? "profile.slot.not-found" : "profile.slot.ambiguous", source: slot.Key.Value); continue; }
                var (rule, origin) = matches[0];
                AddRequest(association.Binding, binding, association.Slot, rule.Renderer, rule.Defaults, rule.Projections, origin);
            }
            // Existing direct projections remain authoritative and share every collision check.
            foreach (var (id, binding) in input.Component.Bindings)
                foreach (var projection in binding.DocumentProjections)
                    AddRequest(id, binding, null, projection.RendererRef, new ConfigurationObject([]),
                        [new(projection.DocumentRef, projection.Path.Value)], new(null, id));
            foreach (var target in contributions.Select(x => (Document: x.Contribution.Document, x.Origin))
                .Concat(requests.SelectMany(x => x.Projections.Select(p => (p.Document, x.Origin)))))
                if (!documents.Any(x => x.Definition.Id == target.Document))
                    Error("profile.document.not-found", target.Document.Value, source: Source(target.Origin));
            foreach (var document in documents.GroupBy(x => x.Definition.Id).Where(x => x.Count() == 1).Select(x => x.Single()))
            {
                var targets = contributions.Where(x => x.Contribution.Document == document.Definition.Id)
                    .Select(x => (x.Contribution.Path, x.Origin))
                    .Concat(requests.SelectMany(x => x.Projections.Where(p => p.Document == document.Definition.Id).Select(p => (p.Path, x.Origin)))).ToArray();
                // Reuse structural validation only: unresolved requests never become placeholder values or successful documents.
                foreach (var diagnostic in ConfigurationComposer.ValidateStructure(document.Definition.Template, targets.Select(x => (x.Path, Source(x.Origin)))))
                    Error(diagnostic.Code, document.Definition.Id.Value, diagnostic.Path, diagnostic.Source);
            }
            layouts.Add(new(input.Id, effective.Values, history, documents, contributions, requests));
            void DuplicateIds(IEnumerable<ManifestLocalId> ids, string code, string source)
            {
                foreach (var group in ids.GroupBy(x => x).Where(x => x.Count() > 1)) Error(code, source: source + "/" + group.Key.Value);
            }
            void AddRequest(ManifestLocalId id, NormalizedBinding binding, ManifestLocalId? slot, ConfigurationRendererId renderer,
                ConfigurationObject defaults, IEnumerable<ProfileProjection> projections, ConfigurationLayoutOrigin origin)
            {
                if (binding.Target is not NormalizedLocalResourceTarget) Error("profile.binding.unsupported", source: id.Value);
                if (!Ordinary(binding.Options)) Error("profile.value.unsupported", source: id.Value);
                var options = defaults.Members.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
                var origins = defaults.Members.ToDictionary(x => x.Key, _ => ProfileOptionScope.Profile, StringComparer.Ordinal);
                foreach (var (key, value) in binding.Options.Members) { options[key] = value; origins[key] = ProfileOptionScope.Binding; }
                requests.Add(new(id, binding, slot, renderer, new(options), projections, origin, origins));
            }
        }
        return new(layouts, errors);
    }
    private static void ValidateDecisions(IEnumerable<ProfileDecision> decisions, ProfileSelectionScope scope, string component, List<ProfileExpansionDiagnostic> errors)
    {
        foreach (var group in decisions.GroupBy(x => x.ProfileId).Where(x => x.Count() > 1))
            errors.Add(new("profile.selection.duplicate", component, "", "", scope + "/" + group.Key.Value));
    }
    private static string Source(ConfigurationLayoutOrigin origin) => origin.Profile is { } profile
        ? profile.ProfileId.Value + "/" + profile.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + origin.Source.Value
        : "application/" + origin.Source.Value;
    private static bool ValidFileName(string value) => value.Length is >= 1 and <= 255 &&
        value[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') &&
        value.All(x => x is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-');
    private static bool Ordinary(ConfigurationValue value) => value switch
    {
        ConfigurationSecretReference or ConfigurationDeferredValue => false,
        ConfigurationObject obj => obj.Members.Values.All(Ordinary),
        ConfigurationArray array => array.Items.All(Ordinary),
        _ => true
    };
}
