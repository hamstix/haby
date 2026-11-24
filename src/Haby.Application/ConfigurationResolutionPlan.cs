using System.Collections.Immutable;
using System.Text.Json;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Server-derived frozen output/projection scope, never accepted from a delivery caller.</summary>
public sealed record ConfigurationSharedCredentialUse(ConfigurationRenderPin Pin, Guid Attempt,
    ExternalOperationOutput Output, string Projection, SharedCredentialScope Scope);

/// <summary>Fresh metadata-only checks shared by static and runtime delivery, before any material read.</summary>
public interface IConfigurationApplicationAuthorization
{
    ValueTask<bool> AuthorizeAsync(ConfigurationDeliveryArtifact artifact,
        IReadOnlyList<ConfigurationSharedCredentialUse> uses, CancellationToken token);
}

internal sealed record AuthorizedConfigurationResolution(ConfigurationRenderPin Pin, ConfigurationDeferredValue Value,
    SecretDefinition Definition, SecretRef Reference, IConfigurationResolutionBinding Adapter);

internal sealed record ConfigurationResolutionPlan(
    ImmutableDictionary<string, (SecretDefinition Definition, ConfigurationProjectedSecret Node)> StaticSecrets,
    ImmutableDictionary<string, AuthorizedConfigurationResolution> Authorized,
    ImmutableArray<ConfigurationSharedCredentialUse> SharedUses)
{
    internal static ConfigurationResolutionPlan Build(ConfigurationDeliveryArtifact artifact, ConfigurationRenderingCatalog rendering,
        IEnumerable<IConfigurationResolutionBinding> registrations, CancellationToken token)
    {
        var bindings = registrations.ToArray();
        var revision = artifact.Revision;
        token.ThrowIfCancellationRequested();
        ConfigurationDocumentCodec.ValidateTree(revision.Tree);
        var frozen = ConfigurationPlanningSnapshotCodec.Decode(revision.FrozenInputs, revision.Metadata.Revision);
        var layout = frozen.Layouts.Single(x => x.Component == revision.Metadata.Target.Component);
        var staticSecrets = new Dictionary<string, (SecretDefinition Definition, ConfigurationProjectedSecret Node)>(StringComparer.Ordinal);
        foreach (var contribution in layout.Contributions.Where(x => x.Contribution.Document == revision.Metadata.Target.Document))
            foreach (var projection in contribution.Contribution.SecretProjections)
            {
                if (!frozen.FrozenSecrets.TryGetValue(projection.SecretRef, out var exact)) throw new InvalidDataException();
                var path = contribution.Contribution.Path + projection.Path;
                if (At(revision.Tree, path) is not ConfigurationProjectedSecret node || node.Reference != exact ||
                    node.Format != projection.Format || !staticSecrets.TryAdd(path,
                        (new(new SecretOwner.Environment(artifact.Environment), SecretPurpose.Configuration), node)))
                    throw new InvalidDataException();
            }
        using var associations = JsonDocument.Parse(revision.OutputAssociations);
        if (associations.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var outputs = associations.RootElement.EnumerateArray().Select(a =>
        {
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 5 || !Guid.TryParse(a[1].GetString(), out var attempt) || attempt == Guid.Empty) throw new InvalidDataException();
            return (Producer: a[0].GetString(), Attempt: attempt, Output: new ExternalOperationOutput(a[2].GetString()!, a[3].GetString()!, a[4].GetString()!));
        }).ToArray();
        var authorized = new Dictionary<string, AuthorizedConfigurationResolution>(StringComparer.Ordinal);
        var shared = new List<ConfigurationSharedCredentialUse>();
        var nextOutput = 0;
        var confirmed = new Dictionary<(string Producer, string Contract, string Key), (Guid Attempt, ExternalOperationOutput Output)>();
        for (var i = 0; i < layout.BindingRequests.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var request = layout.BindingRequests[i];
            var projections = request.Projections.Where(x => x.Document == revision.Metadata.Target.Document).ToArray();
            if (projections.Length == 0) continue;
            var pin = frozen.RenderPins.Single(x => x.Component == layout.Component && x.Request == i);
            // Composition saves one occurrence per participating request, in frozen
            // request order. Reuse is valid only with the same confirmed evidence.
            if (nextOutput >= outputs.Length) throw new InvalidDataException();
            var output = outputs[nextOutput++];
            if (output.Producer != pin.Producer.Value || output.Output.Contract != pin.OutputContract || output.Output.Key != pin.OutputKey)
                throw new InvalidDataException();
            var key = (pin.Producer.Value, pin.OutputContract, pin.OutputKey);
            if (confirmed.TryGetValue(key, out var prior) && (prior.Attempt != output.Attempt || prior.Output != output.Output))
                throw new InvalidDataException();
            confirmed[key] = (output.Attempt, output.Output);
            var adapters = bindings.Where(x => x.Formatter == pin.Formatter && x.Implementation == pin.FormatterImplementation).ToArray();
            if (adapters.Length != 1 || !rendering.SupportsDeliveryFormatter(pin)) throw new InvalidDataException();
            foreach (var projection in projections)
            {
                if (At(revision.Tree, projection.Path) is not ConfigurationDeferredValue deferred) throw new InvalidDataException();
                var bound = adapters[0].Bind(pin, output.Output, deferred) ?? throw new InvalidDataException();
                SecretDefinition definition;
                if (bound.Purpose == SecretPurpose.ApplicationLogin && bound.SharedUse is null &&
                    pin.ProducerAction.Admission.Mode != ResourceAcquisitionMode.Reference)
                    definition = new(new SecretOwner.Resource(new(artifact.Environment,
                        revision.Metadata.Revision.ApplicationRecordId, pin.ResourceLocalId)), bound.Purpose);
                else if (bound.Purpose == SecretPurpose.SharedApplicationCredential && bound.SharedUse is { Use: SharedCredentialUse.ApplicationDelivery } scope &&
                    pin.ProducerAction.Admission.Mode == ResourceAcquisitionMode.Reference && scope.Environment == artifact.Environment && scope.Reference == bound.Reference)
                {
                    definition = new(new SecretOwner.Environment(artifact.Environment), bound.Purpose);
                    shared.Add(new(pin, output.Attempt, output.Output, projection.Path, scope));
                }
                else throw new InvalidDataException();
                if (!rendering.CanFormatDelivery(pin, deferred)) throw new InvalidDataException();
                var references = References(deferred.Arguments).ToArray();
                if (references.Length != 1 || references[0] != bound.Reference) throw new InvalidDataException();
                authorized.Add(projection.Path, new(pin, deferred, definition, bound.Reference, adapters[0]));
            }
        }
        if (nextOutput != outputs.Length) throw new InvalidDataException();
        // Validate every confidential node before resolving even the first SecretRef.
        Validate(revision.Tree, "");
        return new(staticSecrets.ToImmutableDictionary(StringComparer.Ordinal), authorized.ToImmutableDictionary(StringComparer.Ordinal), shared.ToImmutableArray());

        void Validate(ConfigurationValue node, string path)
        {
            token.ThrowIfCancellationRequested();
            switch (node)
            {
                case ConfigurationDeferredValue d:
                    if (!authorized.TryGetValue(path, out var binding) || binding.Value != d) throw new InvalidDataException();
                    break;
                case ConfigurationSecretReference: throw new InvalidDataException();
                case ConfigurationProjectedSecret s:
                    if (!staticSecrets.TryGetValue(path, out var bound) || bound.Node != s) throw new InvalidDataException();
                    break;
                case ConfigurationObject o: foreach (var (key, child) in o.Members) Validate(child, path + "/" + ConfigurationComposer.Escape(key)); break;
                case ConfigurationArray a: for (var j = 0; j < a.Items.Count; j++) Validate(a.Items[j], path + "/" + j.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
            }
        }
    }

    private static ConfigurationValue At(ConfigurationValue tree, string path)
    {
        if (path.Length == 0) return tree;
        if (!path.StartsWith('/')) throw new InvalidDataException();
        foreach (var segment in path[1..].Split('/'))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (ConfigurationComposer.Escape(key) != segment) throw new InvalidDataException();
            if (tree is ConfigurationObject o && o.Members.TryGetValue(key, out var child)) tree = child;
            else if (tree is ConfigurationArray a && int.TryParse(key, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index) && index >= 0 && index < a.Items.Count &&
                key == index.ToString(System.Globalization.CultureInfo.InvariantCulture)) tree = a.Items[index];
            else throw new InvalidDataException();
        }
        return tree;
    }
    private static IEnumerable<SecretRef> References(ConfigurationValue value)
    {
        switch (value)
        {
            case ConfigurationSecretReference s: yield return s.Reference; break;
            case ConfigurationObject o: foreach (var child in o.Members.Values) foreach (var r in References(child)) yield return r; break;
            case ConfigurationArray a: foreach (var child in a.Items) foreach (var r in References(child)) yield return r; break;
            case ConfigurationDeferredValue or ConfigurationProjectedSecret: throw new InvalidDataException();
        }
    }
}
