using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using SampleConfiguration = Haby.Operator.Composition.Sample.ReferenceAdapter.Configuration;
using Codec = Haby.Operator.Composition.Sample.ReferenceAdapter.Codec;

namespace Haby.Operator.Composition.Sample;

// Closed verification adapter. Observation is deliberately synthetic, never a production service client.
internal static class ImportAdapter
{
    internal static readonly ResourceTypeId Resource = ResourceTypeId.Create("sample.import/v1");
    internal static readonly ProviderAdapterKey Provider = new("sample.service/v1", "sample.service.configuration/v1", "sample.service.impl/v1");
    internal static readonly DurableActionExecutionKey Action = new(new(Resource, Provider) { Mode = ResourceAcquisitionMode.ImportUseAndRetain }, OperationContractId.Create("sample.import-verify/v1"));
    internal static readonly ExternalOperationOutputIdentity Output = new("sample.import-access/v1", "access");
    internal static readonly ConfigurationRendererId Renderer = ConfigurationRendererId.Create("sample.import-json/v1");
    internal static readonly ConfigurationFormatterId Formatter = ConfigurationFormatterId.Create("sample.import-format/v1");
    internal static readonly ManifestLocalId Implementation = ManifestLocalId.Create("sample-import-v1");
    internal const string OptionsSchema = """{"type":"object","properties":{"label":{"type":"string","minLength":1,"maxLength":32}},"additionalProperties":false}""";

    internal static void Register(IServiceCollection services)
    {
        services.AddSingleton(new ResourceTypeDefinition(Resource, """{"type":"object","additionalProperties":false}""", OptionsSchema));
        services.AddSingleton(new OutputContractDefinition(OutputContractId.Create(Output.Contract), OptionsSchema));
        services.AddSingleton<IResourceImportPreparer, Preparer>();
        services.AddSingleton<Handler>();
        services.AddSingleton<IDurableActionAdmissionHandler>(p => p.GetRequiredService<Handler>());
        services.AddSingleton<IDurableActionHandler>(p => p.GetRequiredService<Handler>());
        services.AddSingleton<IExternalOperationOutputContract, Evidence>();
        services.AddSingleton<ConfigurationRendererContract>(new RenderContract());
        services.AddSingleton<ConfigurationFormatterContract>(new FormatContract());
        services.AddSingleton(new ConfigurationFormatterImplementation(Implementation, Formatter));
        services.AddSingleton<Rendering>();
        services.AddSingleton<IConfigurationRenderer>(p => p.GetRequiredService<Rendering>());
        services.AddSingleton<IConfigurationFormatter>(p => p.GetRequiredService<Rendering>());
        services.AddSingleton<IConfigurationResolutionBinding>(p => p.GetRequiredService<Rendering>());
    }

    internal static Input? Read(DurableActionPayload payload)
    {
        try { return payload.Format == "sample.import-input/v1" && JsonSerializer.Deserialize(payload.Json, ImportJson.Default.Input) is { Identity: "sample-service" } value ? value : null; }
        catch (JsonException) { return null; }
    }
    private static ProtectedCredentialReference? Credential(DurablePlanningInputs inputs, ManifestLocalId resource)
    {
        var request = inputs.Acquisitions.Resource(resource).ImportRequestId;
        return inputs.Acquisitions.Resources.SingleOrDefault(x => x.ImportRequestId == request && x.Credential is not null)?.Credential;
    }
    private static Access AccessFor(AcceptedOperation own, SampleConfiguration config) => new(own.Inputs.Environment.ToString(),
        own.Inputs.Revision.ApplicationRecordId.ToString(), own.Node.ResourceId.Value, own.Provider.Instance.ToString(), own.Provider.Configuration.Value,
        config.Endpoint, Credential(own.Inputs, own.Node.ResourceId)!.Reference.SecretId.ToString(), Credential(own.Inputs, own.Node.ResourceId)!.Reference.SecretVersionId.ToString());
    private static DurableActionPayload Observation(AcceptedOperation own) => new("sample.import-observation/v1", JsonSerializer.Serialize(
        new Observed(own.Inputs.Environment.ToString(), own.Inputs.Revision.ApplicationRecordId.ToString(), own.Provider.Instance.ToString(),
            own.Provider.Configuration.Value, Read(own.Node.Input)!.Identity, own.Graph.ObservationGroups.Single(x => x.Operations.Contains(own.Node.Id)).Operations.Select(x => x.Value).ToArray()), ImportJson.Default.Observed));
    private static Access? Decode(string json)
    {
        try { return JsonSerializer.Deserialize(json, ImportJson.Default.Access); }
        catch (JsonException) { return null; }
    }

    private static ManifestLocalId? Local(string value)
    {
        try { return ManifestLocalId.Create(value); }
        catch (ArgumentException) { return null; }
    }
    internal sealed record Input(string Identity, string Resource, string Environment, string Provider, long Revision, string Implementation, string CredentialResource);
    internal sealed record Names(string Resource, string Identity, string? Companion = null);
    internal sealed record Access(string Environment, string Application, string Resource, string Provider, long Revision, string Endpoint, string Secret, string Version);
    internal sealed record Observed(string Environment, string Application, string Provider, long Revision, string Identity, string[] Operations);

    private sealed class Preparer(IProviderAliasResolver aliases) : IResourceImportPreparer
    {
        public string InputFormat => "sample.import-names/v1";
        public ImportCredentialSlot CredentialSlot => new("application-password", SecretPurpose.ApplicationLogin);
        public bool MatchesPreparedRequest(ResourceImportPreparationRequest request, OperatorImportRequestBinding binding)
        {
            try
            {
                var names = JsonSerializer.Deserialize(request.Input.Json, ImportJson.Default.Names);
                return names is { Identity: "sample-service" } && binding.CredentialResource.Value == names.Resource &&
                    binding.Input.Resources.Select(x => x.Resource.Value).ToHashSet(StringComparer.Ordinal).SetEquals(names.Companion is null ? [names.Resource] : [names.Resource, names.Companion]) &&
                    binding.Input.Resources.All(member => Read(member.Input) is { } input && input.Resource == member.Resource.Value &&
                        input.CredentialResource == names.Resource && input.Environment == binding.Environment.ToString());
            }
            catch (JsonException) { return false; }
        }
        public async ValueTask<ResourceImportPreparationResult> PrepareAsync(ResourceImportPreparationRequest request, NormalizedManifestSpec spec, CancellationToken token)
        {
            Names? names;
            try { names = JsonSerializer.Deserialize(request.Input.Json, ImportJson.Default.Names); }
            catch (Exception error) when (error is JsonException or ArgumentException) { return new(ResourceImportPreparationStatus.InvalidInput); }
            if (names is not { Identity: "sample-service" } || Local(names.Resource) is not { } owner || !spec.Resources.TryGetValue(owner, out var resource) || resource.Type != Resource)
                return new(ResourceImportPreparationStatus.ImportNotSupported);
            var selected = await aliases.ResolveAsync(request.Environment, resource.ProviderRef, token);
            if (selected is null || selected.ImplementationId != Provider.ImplementationId) return new(ResourceImportPreparationStatus.ProviderUnavailable);
            var ids = (names.Companion is null ? new[] { names.Resource } : new[] { names.Resource, names.Companion }).Select(Local).ToArray();
            if (ids.Any(id => id is null)) return new(ResourceImportPreparationStatus.InvalidInput);
            var group = ids.OfType<ManifestLocalId>().ToArray();
            if (group.Distinct().Count() != group.Length || group.Any(id => !spec.Resources.TryGetValue(id, out var member) || member.Type != Resource || member.ProviderRef != resource.ProviderRef))
                return new(ResourceImportPreparationStatus.ImportNotSupported);
            return new(ResourceImportPreparationStatus.Prepared, new(request.RequestId, request.Environment, request.Revision, ManifestLocalId.Create(names.Resource),
                new(group.Select(id => new ResourceImportRequest(id, new("sample.import-input/v1", JsonSerializer.Serialize(new Input(names.Identity, id.Value,
                    request.Environment.ToString(), selected.Binding.InstanceId.ToString(), selected.Binding.RevisionNumber.Value, selected.ImplementationId, names.Resource), ImportJson.Default.Input)))))));
        }
    }

    private sealed class Evidence(Codec codec) : IResourceObservationOutputContract
    {
        public DurableActionExecutionKey Producer => Action;
        public ExternalOperationOutputIdentity Identity => Output;
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => node.Action == Action && output.Contract == Output.Contract &&
            output.Key == Output.Key && Decode(output.Payload) is { } value && value.Resource == node.ResourceId.Value &&
            value.Environment == node.References.Environment.ToString() && value.Provider == node.References.ProviderInstance.ToString() &&
            value.Revision == node.References.ConfigurationRevision.Value;
        public bool ValidatePublicationMetadata(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
            IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration configuration, IReadOnlyCollection<ProviderResourceReservation> reservations) =>
            Validate(accepted.Node, output) && output.Payload == JsonSerializer.Serialize(AccessFor(accepted, codec.DecodeTyped(configuration.PayloadJson)), ImportJson.Default.Access) &&
            reservations.Count(x => x.ProviderInstanceId == accepted.Provider.Instance && x.EnvironmentRecordId == accepted.Inputs.Environment && x.ApplicationRecordId == accepted.Inputs.Revision.ApplicationRecordId &&
                x.ResourceLocalId == accepted.Node.ResourceId && x.Namespace == "sample.import" && x.Value == Read(accepted.Node.Input)!.Identity + "." + accepted.Node.ResourceId.Value) == 1;
        public bool ValidateReuse(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
            IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration configuration, IReadOnlyCollection<ProviderResourceReservation> reservations) =>
            ValidatePublicationMetadata(key, accepted, output, sources, configuration, reservations);
        public bool ValidateObservationEvidence(AcceptedOperation accepted, DurableActionPayload evidence) => evidence == Observation(accepted);

    }

    private sealed class Handler(IDurableOperationJournal journal, IExternalOperationOutputs outputs, IResourceObservationAcceptance acceptance,
        IProviderConfigurationStore configurations, IProtectedApplicationCredentialIntake credentials) :
        IDurableActionAdmissionHandler, IDurableResourcePlanComposer, IReadOnlyResourceImportHandler, IDurableResourceReusePolicy, IDurableResourceAdditionPolicy
    {
        public DurableActionExecutionKey Key => Action;
        public bool RequiresExternalOutput => true;
        public ExternalOperationOutputIdentity OutputIdentity => Output;
        public bool Validate(ManifestLocalId resource, NormalizedResource declaration, DurablePlanningInputs inputs) => Read(inputs.Acquisitions.Resource(resource).Input!) is { } input && input.Resource == resource.Value && input.Environment == inputs.Environment.ToString() &&
            input.Provider == inputs.Providers.Resource(resource).Instance.ToString() && input.Revision == inputs.Providers.Resource(resource).Configuration.Value &&
            input.Implementation == inputs.Providers.Resource(resource).Implementation.Value && Credential(inputs, resource) is not null;
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resource, NormalizedResource declaration, DurablePlanningInputs inputs)
        {
            var p = inputs.Providers.Resource(resource);
            return [new(OperationId.Create(resource.Value + "-import"), resource, Action, new(p.Environment, p.Instance, p.Configuration, p.Implementation.Value),
                inputs.Acquisitions.Resource(resource).Input!, []) { RequiresExternalOutput = true, RequiredOutput = Output }];
        }
        public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => Validate(resource, spec.Resources[resource], inputs);
        public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => Plan(resource, spec.Resources[resource], inputs);
        public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, DurableOperationGraph graph)
        {
            var expected = Plan(resource, spec.Resources[resource], inputs).Single();
            return graph.Nodes.Count(x => x.ResourceId == resource) == 1 && graph.Nodes.Any(x => x.Id == expected.Id && x.Action == expected.Action &&
                x.Input == expected.Input && x.References == expected.References && x.Dependencies.Count == 0 && x.RequiredOutput == Output);
        }
        public bool Supports(DurableOperationNode node) => node.Action == Action && Read(node.Input) is not null;
        public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            inputs.Acquisitions.Resource(resource).Mode == ResourceAcquisitionMode.ImportUseAndRetain && accepted.Resources.TryGetValue(resource, out var old) &&
            target.Resources.TryGetValue(resource, out var current) && old.Type == current.Type && old.ProviderRef == current.ProviderRef &&
            old.DeletionPolicy == current.DeletionPolicy && old.Properties.Equals(current.Properties);
        public bool CanAdd(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
            DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            new ResourceDelta(accepted, target).Entries.Any(x => x.Resource == resource && x.Kind == ResourceDeltaKind.Added) &&
            inputs.Acquisitions.Resource(resource).Mode == ResourceAcquisitionMode.ImportUseAndRetain && Validate(target, resource, inputs, configuration);
        public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken token)
        {
            var own = await outputs.FindAcceptedAsync(operation.Key, token);
            if (own is null) return new(DurableExecutionOutcome.InputUnavailable, null);
            var revision = await configurations.FindRevisionAsync<SampleConfiguration>(own.Inputs.Environment, own.Provider.Instance, own.Provider.Configuration, token);
            if (revision is null) return new(DurableExecutionOutcome.InputUnavailable, null);
            var staged = Credential(own.Inputs, own.Node.ResourceId)!;
            var credentialResource = ManifestLocalId.Create(Read(own.Node.Input)!.CredentialResource);
            using var material = await credentials.ResolveStagedAsync(staged, new(own.Inputs.Environment, own.Inputs.Revision.ApplicationRecordId, credentialResource), token);
            if (material is null) return new(DurableExecutionOutcome.InputUnavailable, null);
            var bytes = material.CopyBytes();
            try { if (!bytes.AsSpan().SequenceEqual("synthetic-import-material"u8)) return new(DurableExecutionOutcome.Failed, null); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            // Synthetic external verification is read-only; accepted material is confirmed atomically below.
            var confirmations = new List<ObservationOperationConfirmation>();
            var identities = new List<ProviderResourceReservation>();
            foreach (var id in own.Graph.ObservationGroups.Single(x => x.Operations.Contains(own.Node.Id)).Operations)
            {
                var key = operation.Key with { OperationId = id };
                var current = await journal.FindAsync(key, token);
                if (current is null) return new(DurableExecutionOutcome.InputUnavailable, null);
                Guid attempt;
                if (current.State == DurableOperationState.Dispatching) attempt = current.AttemptId!.Value;
                else
                {
                    if (await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, token) is not DurableBeginAttemptResult.Started started)
                        return new(DurableExecutionOutcome.InputUnavailable, null);
                    attempt = started.AttemptId;
                    if (await journal.TryCommitDispatchMarkerAsync(key, attempt, DateTimeOffset.UtcNow, token) is not DurableMarkerResult.Committed)
                        return new(DurableExecutionOutcome.InputUnavailable, null);
                }
                var accepted = await outputs.FindAcceptedAsync(key, token);
                if (accepted is null) return new(DurableExecutionOutcome.InputUnavailable, null);
                confirmations.Add(new(key, attempt, new(Output.Contract, Output.Key, JsonSerializer.Serialize(AccessFor(accepted, revision.Configuration), ImportJson.Default.Access))));
                identities.Add(new(accepted.Provider.Instance, accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId, accepted.Node.ResourceId, "sample.import", Read(accepted.Node.Input)!.Identity + "." + accepted.Node.ResourceId.Value));
            }
            var result = await acceptance.CompleteAsync(new(confirmations, [new(new(own.Inputs.Environment, own.Inputs.Revision.ApplicationRecordId, credentialResource), staged)], identities, Observation(own)), DateTimeOffset.UtcNow, token);
            return new(result.IsSuccess ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.InputUnavailable, await journal.FindAsync(operation.Key, token));
        }
    }

    private sealed record RenderContract() : ConfigurationRendererContract(Renderer, ImportAdapter.Resource, Output.Contract, Output.Key, Action, ImportAdapter.Formatter)
    {
        public override ConfigurationObject Defaults => new(new Dictionary<string, ConfigurationValue> { ["label"] = new ConfigurationString("default") });
        public override bool ValidateOptions(ConfigurationObject options, bool complete) => (!complete || options.Members.Count == 1) &&
            options.Members.All(x => x.Key == "label" && x.Value is ConfigurationString { Value.Length: > 0 and <= 32 });
        public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => options;
    }
    private sealed record FormatContract() : ConfigurationFormatterContract(Formatter)
    {
        public override bool ValidateArguments(ConfigurationObject args) => args.Members.Count == 3 && args.Members.GetValueOrDefault("output") is ConfigurationString output &&
            Decode(output.Value) is not null && args.Members.GetValueOrDefault("options") is ConfigurationObject options && new RenderContract().ValidateOptions(options, true) &&
            args.Members.GetValueOrDefault("credential") is ConfigurationSecretReference;
    }
    private sealed class Rendering : IConfigurationRenderer, IConfigurationFormatter, IConfigurationResolutionBinding
    {
        ConfigurationRendererId IConfigurationRenderer.Contract => Renderer;
        ConfigurationFormatterId IConfigurationFormatter.Contract => Formatter;
        public ManifestLocalId Identity => Implementation;
        public ConfigurationFormatterId Formatter => ImportAdapter.Formatter;
        public ManifestLocalId Implementation => ImportAdapter.Implementation;
        public ConfigurationFragment? Render(ExternalOperationOutput output, ConfigurationObject options)
        {
            var value = Decode(output.Payload);
            return value is null ? null : new(new ConfigurationDeferredValue(Formatter, new(new Dictionary<string, ConfigurationValue>
            {
                ["output"] = new ConfigurationString(output.Payload),
                ["options"] = options,
                ["credential"] = new ConfigurationSecretReference(new(SecretId.Parse(value.Secret), SecretVersionId.Parse(value.Version)))
            })));
        }
        public SecretResolutionReference? Bind(ConfigurationRenderPin pin, ExternalOperationOutput output, ConfigurationDeferredValue deferred)
        {
            var value = Decode(output.Payload);
            if (value is null || pin.ProducerAction != Action || pin.ResourceLocalId.Value != value.Resource || output.Contract != Output.Contract || output.Key != Output.Key ||
                deferred.Formatter != Formatter || !new FormatContract().ValidateArguments(deferred.Arguments) ||
                deferred.Arguments.Members["output"] is not ConfigurationString text || text.Value != output.Payload ||
                !deferred.Arguments.Members["options"].Equals(pin.Options) || deferred.Arguments.Members["credential"] is not ConfigurationSecretReference secret ||
                secret.Reference.SecretId.ToString() != value.Secret || secret.Reference.SecretVersionId.ToString() != value.Version) return null;
            return new(secret.Reference, SecretPurpose.ApplicationLogin);
        }
        public bool ValidateMaterial(ReadOnlySpan<char> material) => !material.IsEmpty;
        public ConfidentialConfigurationValue? Format(ConfigurationObject arguments, ReadOnlySpan<char> material)
        {
            var output = Decode(((ConfigurationString)arguments.Members["output"]).Value)!;
            return new(new ConfigurationObject(new Dictionary<string, ConfigurationValue>
            {
                ["endpoint"] = new ConfigurationString(output.Endpoint),
                ["credential"] = new ConfigurationString(material.ToString()),
                ["label"] = ((ConfigurationObject)arguments.Members["options"]).Members["label"]
            }));
        }
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ImportAdapter.Input))]
[JsonSerializable(typeof(ImportAdapter.Access))]
[JsonSerializable(typeof(ImportAdapter.Observed))]
[JsonSerializable(typeof(ImportAdapter.Names))]
internal partial class ImportJson : JsonSerializerContext;
