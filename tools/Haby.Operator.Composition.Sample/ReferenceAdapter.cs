using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Operator.Composition.Sample;

// Closed verification adapter. Observation is deliberately synthetic, never a production service client.
internal static class ReferenceAdapter
{
    internal static readonly ResourceTypeId Resource = ResourceTypeId.Create("sample.reference/v1");
    internal static readonly ProviderAdapterKey Provider = new("sample.service/v1", "sample.service.configuration/v1", "sample.service.impl/v1");
    internal static readonly DurableActionExecutionKey Action = new(new(Resource, Provider) { Mode = ResourceAcquisitionMode.Reference }, OperationContractId.Create("sample.verify/v1"));
    internal static readonly ExternalOperationOutputIdentity Output = new("sample.access/v1", "access");
    internal static readonly ConfigurationRendererId Renderer = ConfigurationRendererId.Create("sample.access-json/v1");
    internal static readonly ConfigurationFormatterId Formatter = ConfigurationFormatterId.Create("sample.access-format/v1");
    internal static readonly ManifestLocalId Implementation = ManifestLocalId.Create("sample-access-v1");
    internal const string OptionsSchema = """{"type":"object","properties":{"label":{"type":"string","minLength":1,"maxLength":32}},"additionalProperties":false}""";

    internal static void Register(IServiceCollection services)
    {
        services.AddSingleton(new ResourceTypeDefinition(Resource, """{"type":"object","additionalProperties":false}""", OptionsSchema));
        services.AddSingleton(new OutputContractDefinition(OutputContractId.Create(Output.Contract), OptionsSchema));
        services.AddSingleton<Codec>();
        services.AddSingleton<IProviderConfigurationCodec>(p => p.GetRequiredService<Codec>());
        services.AddSingleton<IProviderDeclarationAdapter, Declaration>();
        services.AddSingleton<IProviderPreflightHandler, Preflight>();
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
        try { return payload.Format == "sample.reference-input/v1" && JsonSerializer.Deserialize(payload.Json, SampleJson.Default.Input) is { Identity: "sample-service" } value ? value : null; }
        catch (JsonException) { return null; }
    }
    private static Access AccessFor(AcceptedOperation own, Configuration config) => new(own.Inputs.Environment.ToString(),
        own.Inputs.Revision.ApplicationRecordId.ToString(), own.Node.ResourceId.Value, own.Provider.Instance.ToString(), own.Provider.Configuration.Value,
        config.Endpoint, config.Application.SecretId.ToString(), config.Application.SecretVersionId.ToString());
    private static DurableActionPayload Observation(AcceptedOperation own) => new("sample.observation/v1", JsonSerializer.Serialize(
        new Observed(own.Inputs.Environment.ToString(), own.Inputs.Revision.ApplicationRecordId.ToString(), own.Provider.Instance.ToString(),
            own.Provider.Configuration.Value, Read(own.Node.Input)!.Identity, own.Graph.ObservationGroups.Single(x => x.Operations.Contains(own.Node.Id)).Operations.Select(x => x.Value).ToArray()), SampleJson.Default.Observed));
    private static Access? Decode(string json)
    {
        try { return JsonSerializer.Deserialize(json, SampleJson.Default.Access); }
        catch (JsonException) { return null; }
    }

    internal sealed record Input(string Identity);
    internal sealed record Properties(string Endpoint);
    internal sealed record Configuration(string Endpoint, SecretRef System, SecretRef Application);
    internal sealed record Stored(string Endpoint, string SystemSecret, string SystemVersion, string ApplicationSecret, string ApplicationVersion);
    internal sealed record Access(string Environment, string Application, string Resource, string Provider, long Revision, string Endpoint, string Secret, string Version);
    internal sealed record Observed(string Environment, string Application, string Provider, long Revision, string Identity, string[] Operations);
    internal sealed record Sharing(string Identity);

    internal sealed class Codec : ProviderConfigurationCodec<Configuration>
    {
        public override string ProviderKindId => Provider.ProviderKind;
        public override string ShapeId => Provider.ConfigurationContract;
        public override int PayloadFormatVersion => 1;
        public override PreparedProviderConfiguration Encode(Configuration value)
        {
            if (value.Endpoint != "https://service.example.invalid/") throw new ArgumentException("Invalid sample endpoint.");
            return new(ProviderKindId, new(1, value.Endpoint), Provider.ImplementationId, ShapeId, 1,
                JsonSerializer.Serialize(new Stored(value.Endpoint, value.System.SecretId.ToString(), value.System.SecretVersionId.ToString(),
                    value.Application.SecretId.ToString(), value.Application.SecretVersionId.ToString()), SampleJson.Default.Stored),
                [new("system", value.System, SharedCredentialUse.SystemAccess), new("application", value.Application, SharedCredentialUse.ApplicationDelivery)]);
        }
        public override Configuration DecodeTyped(string json)
        {
            var value = JsonSerializer.Deserialize(json, SampleJson.Default.Stored) ?? throw new JsonException();
            return new(value.Endpoint, new(SecretId.Parse(value.SystemSecret), SecretVersionId.Parse(value.SystemVersion)),
                new(SecretId.Parse(value.ApplicationSecret), SecretVersionId.Parse(value.ApplicationVersion)));
        }
    }

    private sealed class Declaration(Codec codec) : IProviderDeclarationAdapter
    {
        public ProviderAdapterKey Key => Provider;
        public IProviderConfigurationCodec Codec => codec;
        public IReadOnlyList<string> RequiredSecretSlots => ["system", "application"];
        public IReadOnlyDictionary<string, SharedCredentialUse> SharedCredentialSlots => new Dictionary<string, SharedCredentialUse>
        { ["system"] = SharedCredentialUse.SystemAccess, ["application"] = SharedCredentialUse.ApplicationDelivery };
        public object ParseProperties(JsonElement properties) => properties.Deserialize(SampleJson.Default.Properties) is { Endpoint: "https://service.example.invalid/" } value ? value : throw new JsonException();
        public object Bind(object properties, IReadOnlyDictionary<string, SecretRef> secrets) => new Configuration(((Properties)properties).Endpoint, secrets["system"], secrets["application"]);
        public bool CanReplace(ProviderAdapterKey previous) => previous == Provider;
        public bool Equivalent(object previous, object candidate) => previous.Equals(candidate);
        public bool SameEndpoint(object previous, object candidate) => ((Configuration)previous).Endpoint == ((Configuration)candidate).Endpoint;
    }

    private sealed class Preflight(Codec codec) : ProviderPreflightHandler<Configuration>(codec)
    {
        public override IReadOnlyList<ProviderAdapterKey> Keys => [Provider];
        public override ValueTask<ProviderPreflightResult> CheckTypedAsync(ProviderPreflightContext context, Configuration configuration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready)); }
    }

    private sealed class Evidence(Codec codec) : IResourceReferenceSharingContract
    {
        public DurableActionExecutionKey Producer => Action;
        public ExternalOperationOutputIdentity Identity => Output;
        public string SharingContract => "sample.sharing-decision/v1";
        public DurableActionPayload? AuthorizeSharing(DurableOperationNode node, DurableActionPayload request) =>
            request.Format == "sample.sharing/v1" && Read(node.Input) is { } input &&
            request.Json == JsonSerializer.Serialize(new Sharing(input.Identity), SampleJson.Default.Sharing) ? request : null;
        public bool AreCompatible(ReferenceSharingParticipant own, ReferenceSharingParticipant peer) =>
            own.Authorization.Decision == peer.Authorization.Decision && own.Identity.Namespace == "sample.service" &&
            peer.Identity.Namespace == "sample.service" && own.Identity.Value == peer.Identity.Value;
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => node.Action == Action && output.Contract == Output.Contract &&
            output.Key == Output.Key && Decode(output.Payload) is { } value && value.Resource == node.ResourceId.Value &&
            value.Environment == node.References.Environment.ToString() && value.Provider == node.References.ProviderInstance.ToString() &&
            value.Revision == node.References.ConfigurationRevision.Value;
        public bool ValidatePublicationMetadata(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
            IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration configuration, IReadOnlyCollection<ProviderResourceReservation> reservations) =>
            Validate(accepted.Node, output) && output.Payload == JsonSerializer.Serialize(AccessFor(accepted, codec.DecodeTyped(configuration.PayloadJson)), SampleJson.Default.Access);
        public bool ValidateReuse(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
            IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration configuration, IReadOnlyCollection<ProviderResourceReservation> reservations) =>
            ValidatePublicationMetadata(key, accepted, output, sources, configuration, reservations);
        public bool ValidateObservationEvidence(AcceptedOperation accepted, DurableActionPayload evidence) => evidence == Observation(accepted);
        public bool ValidateReferenceIdentities(AcceptedOperation accepted, IReadOnlyList<ProviderResourceReservation> identities) => identities.Count == 1 &&
            identities[0] == new ProviderResourceReservation(accepted.Provider.Instance, accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId,
                accepted.Node.ResourceId, "sample.service", Read(accepted.Node.Input)!.Identity);
    }

    private sealed class Handler(IDurableOperationJournal journal, IExternalOperationOutputs outputs, IResourceObservationAcceptance acceptance,
        IProviderConfigurationStore configurations, ISharedCredentialSystemAccess system) :
        IDurableActionAdmissionHandler, IDurableResourcePlanComposer, IReadOnlyResourceObservationHandler, IDurableResourceReusePolicy, IDurableResourceAdditionPolicy
    {
        public DurableActionExecutionKey Key => Action;
        public bool RequiresExternalOutput => true;
        public ExternalOperationOutputIdentity OutputIdentity => Output;
        public bool Validate(ManifestLocalId resource, NormalizedResource declaration, DurablePlanningInputs inputs) => Read(inputs.Acquisitions.Resource(resource).Input!) is not null;
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resource, NormalizedResource declaration, DurablePlanningInputs inputs)
        {
            var p = inputs.Providers.Resource(resource);
            return [new(OperationId.Create(resource.Value + "-reference"), resource, Action, new(p.Environment, p.Instance, p.Configuration, p.Implementation.Value),
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
            inputs.Acquisitions.Resource(resource).Mode == ResourceAcquisitionMode.Reference && accepted.Resources.TryGetValue(resource, out var old) &&
            target.Resources.TryGetValue(resource, out var current) && old.Type == current.Type && old.ProviderRef == current.ProviderRef &&
            old.DeletionPolicy == current.DeletionPolicy && old.Properties.Equals(current.Properties);
        public bool CanAdd(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
            DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            new ResourceDelta(accepted, target).Entries.Any(x => x.Resource == resource && x.Kind == ResourceDeltaKind.Added) &&
            inputs.Acquisitions.Resource(resource).Mode == ResourceAcquisitionMode.Reference && Validate(target, resource, inputs, configuration);
        public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken token)
        {
            var own = await outputs.FindAcceptedAsync(operation.Key, token);
            if (own is null) return new(DurableExecutionOutcome.InputUnavailable, null);
            var revision = await configurations.FindRevisionAsync<Configuration>(own.Inputs.Environment, own.Provider.Instance, own.Provider.Configuration, token);
            if (revision is null) return new(DurableExecutionOutcome.InputUnavailable, null);
            using var material = await system.ResolveAsync(new(own.Inputs.Environment, own.Provider.Instance, own.Provider.Configuration,
                "system", SharedCredentialUse.SystemAccess, revision.Configuration.System), 65536, token);
            if (material is null) return new(DurableExecutionOutcome.InputUnavailable, null);
            // Synthetic external read occurs only after exact SystemAccess authorization; never CREATE.
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
                confirmations.Add(new(key, attempt, new(Output.Contract, Output.Key, JsonSerializer.Serialize(AccessFor(accepted, revision.Configuration), SampleJson.Default.Access))));
                identities.Add(new(accepted.Provider.Instance, accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId, accepted.Node.ResourceId, "sample.service", Read(accepted.Node.Input)!.Identity));
            }
            var result = await acceptance.CompleteAsync(new(confirmations, [], identities, Observation(own)), DateTimeOffset.UtcNow, token);
            return new(result.IsSuccess ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.InputUnavailable, await journal.FindAsync(operation.Key, token));
        }
    }

    private sealed record RenderContract() : ConfigurationRendererContract(Renderer, ReferenceAdapter.Resource, Output.Contract, Output.Key, Action, ReferenceAdapter.Formatter)
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
        public ConfigurationFormatterId Formatter => ReferenceAdapter.Formatter;
        public ManifestLocalId Implementation => ReferenceAdapter.Implementation;
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
            return new(secret.Reference, SecretPurpose.SharedApplicationCredential, new(EnvironmentRecordId.Parse(value.Environment), ProviderInstanceId.Parse(value.Provider),
                ProviderConfigurationRevisionNumber.Create(value.Revision), "application", SharedCredentialUse.ApplicationDelivery, secret.Reference));
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
[JsonSerializable(typeof(ReferenceAdapter.Input))]
[JsonSerializable(typeof(ReferenceAdapter.Properties))]
[JsonSerializable(typeof(ReferenceAdapter.Stored))]
[JsonSerializable(typeof(ReferenceAdapter.Access))]
[JsonSerializable(typeof(ReferenceAdapter.Observed))]
[JsonSerializable(typeof(ReferenceAdapter.Sharing))]
internal partial class SampleJson : JsonSerializerContext;
