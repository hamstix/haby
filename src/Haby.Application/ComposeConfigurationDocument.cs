using Haby.Domain;

namespace Haby.Application;

public sealed record ConsumedConfigurationOutput(ConfigurationRenderPin Pin, ExternalOperationOutput Output);
public sealed record ComposedConfigurationDocument(ConfigurationValue Tree, IReadOnlyList<ConsumedConfigurationOutput> Outputs);

/// <summary>The common one-document seam. Receives only frozen inputs and confirmed containing-generation outputs.</summary>
public sealed class ComposeConfigurationDocument(ConfigurationRenderingCatalog rendering,
    ConfigurationFormatterCatalog formatters, IConfigurationOutputEvidence evidence)
{
    public async ValueTask<ComposedConfigurationDocument?> ExecuteAsync(ConfigurationPlanningSnapshot frozen,
        ConfigurationDocumentTarget target, EnvironmentRecordId environment, CancellationToken token)
    {
        var layout = frozen.Layouts.SingleOrDefault(x => x.Component == target.Component);
        var document = layout?.Documents.SingleOrDefault(x => x.Definition.Id == target.Document)?.Definition;
        if (layout is null || document is null || document.Format != StructuredDocumentFormat.Json || frozen.Manifest.Spec.Parameters.Count != 0 ||
            !Static(document.Template)) return null;
        var projections = new List<ConfigurationProjection>();
        var outputs = new List<ConsumedConfigurationOutput>();
        ConfigurationDocumentCodec.ValidateTree(document.Template);
        foreach (var contribution in layout.Contributions.Where(x => x.Contribution.Document == target.Document))
        {
            if (contribution.Contribution.SecretProjections.Count != 0 || !Static(contribution.Contribution.Value)) return null;
            ConfigurationDocumentCodec.ValidateTree(contribution.Contribution.Value);
            projections.Add(new(contribution.Contribution.Path, new(contribution.Contribution.Value), contribution.Origin.Source));
        }
        for (var i = 0; i < layout.BindingRequests.Count; i++)
        {
            var request = layout.BindingRequests[i];
            var targets = request.Projections.Where(x => x.Document == target.Document).ToArray();
            if (targets.Length == 0) continue;
            var pin = frozen.RenderPins.Single(x => x.Component == target.Component && x.Request == i);
            if (!rendering.Supports(pin) || !Static(pin.Options)) return null;
            var output = await evidence.ReadAsync(new(target.Deployment, target.Generation, pin.Producer),
                new(environment, frozen.Revision.ApplicationRecordId, pin.ResourceLocalId), pin.ProducerAction, pin.OutputContract, pin.OutputKey, token);
            if (output is null || rendering.Render(pin, output) is not { } fragment) return null;
            ConfigurationDocumentCodec.ValidateTree(fragment.Value);
            outputs.Add(new(pin, output));
            foreach (var projection in targets) projections.Add(new(projection.Path, fragment, request.BindingId));
        }
        // Bound aggregate work before the composer copies intermediate immutable objects.
        if (projections.Sum(x => (long)x.Path.Length) > ConfigurationDocumentCodec.MaxBytes) throw new ConfigurationDocumentLimitException();
        if (projections.Any(x => x.Path.Count(c => c == '/') > ConfigurationDocumentCodec.MaxContainers)) throw new ConfigurationDocumentLimitException();
        ConfigurationDocumentCodec.ValidateTrees(projections.Select(x => x.Fragment.Value).Prepend(document.Template));
        var composed = new ConfigurationComposer(formatters).Compose(document.Template, projections);
        if (composed.Document is not { } tree) return null;
        ConfigurationDocumentCodec.ValidateTree(tree);
        _ = ConfigurationJson.Preview(tree);
        return new(tree, outputs);
    }
    private static bool Static(ConfigurationValue value) => value switch
    {
        ConfigurationString s => !s.Value.Contains("{{", StringComparison.Ordinal) && !s.Value.Contains("{%", StringComparison.Ordinal),
        ConfigurationArray a => a.Items.All(Static),
        ConfigurationObject o => o.Members.Values.All(Static),
        ConfigurationSecretReference or ConfigurationDeferredValue => false,
        _ => true
    };
}
