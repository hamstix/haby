using System.Data.Common;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

/// <summary>Closed test-only Reference adapter through the real catalog/kernel/Profile/Binding/delivery pipeline.</summary>
public sealed partial class SharedReferenceDeliveryTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly ResourceTypeId Type = ResourceTypeId.Create("sample.shared-access/v1");
    private static readonly ProviderAdapterKey Provider = new("postgresql/v1", "haby.postgresql.connection/v1", "haby.postgresql.database/v1alpha1");
    private static readonly DurableActionExecutionKey Action = new(new(Type, Provider) { Mode = ResourceAcquisitionMode.Reference }, OperationContractId.Create("sample.reference/v1"));
    private static readonly ExternalOperationOutputIdentity Output = new("sample.shared-access-output/v1", "access");
    private const string OptionsSchema = """
        {"type":"object","additionalProperties":false,"properties":{
         "consumer":{"type":"integer","minimum":0,"maximum":1023},
         "connectTimeout":{"type":"integer","minimum":1,"maximum":60000},
         "client":{"type":"object","additionalProperties":false,"required":["timeout"],"properties":{
          "timeout":{"type":"integer","minimum":1,"maximum":60000},"label":{"type":"string","minLength":1,"maxLength":32}}}}}
        """;
    private static readonly ConfigurationRendererId RendererId = ConfigurationRendererId.Create("sample.shared-access/v1");
    private static readonly ConfigurationFormatterId FormatterId = ConfigurationFormatterId.Create("sample.shared-access-json/v1");
    private static readonly ManifestLocalId Implementation = ManifestLocalId.Create("sample-shared-access-v1");
    internal sealed record ReferenceInput(string Identity);
    internal sealed record AccessOutput(string Environment, string Application, string Resource, string Provider, long Revision,
        string Slot, string Secret, string Version, string Host, int Port);
    internal sealed record Observation(string Environment, string Application, string Provider, long Revision, string Identity, string[] Resources);
    internal sealed record Sharing(string Identity);
    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(ReferenceInput))]
    [JsonSerializable(typeof(AccessOutput))]
    [JsonSerializable(typeof(Observation))]
    [JsonSerializable(typeof(Sharing))]
    internal partial class AdapterJson : JsonSerializerContext;

    private sealed class Boundary : DbCommandInterceptor
    {
        internal int MaterialReads, PayloadQueries, Observations, FormatterCalls;
        internal void ResetMaterial() { MaterialReads = 0; PayloadQueries = 0; }
        internal bool ThrowFormatter { get; set; }
        internal int ThrowFormatterAtCall { get; set; }
        internal string? BindingFault { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("secret_payloads", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref PayloadQueries);
                if (!command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref MaterialReads);
            }
            return ValueTask.FromResult(result);
        }
    }
    private static ReferenceInput? Input(DurableActionPayload input)
    {
        try { return input.Format == "sample.reference-input/v1" ? JsonSerializer.Deserialize(input.Json, AdapterJson.Default.ReferenceInput) : null; }
        catch (JsonException) { return null; }
    }
    private static AccessOutput? Decode(string json)
    {
        try
        {
            var x = JsonSerializer.Deserialize(json, AdapterJson.Default.AccessOutput);
            return x is not null && Guid.TryParse(x.Environment, out _) && Guid.TryParse(x.Application, out _) && Guid.TryParse(x.Provider, out _) &&
                Guid.TryParse(x.Secret, out _) && Guid.TryParse(x.Version, out _) && x.Revision > 0 && x.Slot == PostgreSqlProviderConfigurationCodec.ApplicationCredentialSlot &&
                x.Host.Length > 0 && x.Port is > 0 and <= 65535 ? x : null;
        }
        catch (JsonException) { return null; }
    }
    private static AccessOutput OutputFor(AcceptedOperation accepted, PostgreSqlProviderConfiguration configuration) => new(
        accepted.Inputs.Environment.ToString(), accepted.Inputs.Revision.ApplicationRecordId.ToString(), accepted.Node.ResourceId.Value,
        accepted.Provider.Instance.ToString(), accepted.Provider.Configuration.Value, PostgreSqlProviderConfigurationCodec.ApplicationCredentialSlot,
        configuration.ApplicationCredential!.SecretId.ToString(), configuration.ApplicationCredential.SecretVersionId.ToString(), configuration.Host, configuration.Port);
    private static DurableActionPayload ObservationFor(AcceptedOperation accepted)
    {
        var group = accepted.Graph.ObservationGroups.Single(x => x.Operations.Contains(accepted.Node.Id));
        return new("sample.reference-observation/v1", JsonSerializer.Serialize(new Observation(accepted.Inputs.Environment.ToString(),
            accepted.Inputs.Revision.ApplicationRecordId.ToString(), accepted.Provider.Instance.ToString(), accepted.Provider.Configuration.Value,
            Input(accepted.Node.Input)!.Identity, accepted.Graph.Nodes.Where(x => group.Operations.Contains(x.Id)).Select(x => x.ResourceId.Value).Distinct().Order(StringComparer.Ordinal).ToArray()),
            AdapterJson.Default.Observation));
    }
    private sealed class Evidence(ICheckpointProviderCapability capabilities) : IResourceReferenceSharingContract
    {
        public DurableActionExecutionKey Producer => Action;
        public ExternalOperationOutputIdentity Identity => Output;
        public string SharingContract => "sample.shared-access-sharing/v1";
        public DurableActionPayload? AuthorizeSharing(DurableOperationNode node, DurableActionPayload request) =>
            request.Format == "sample.sharing/v1" && request.Json == JsonSerializer.Serialize(new Sharing(Input(node.Input)!.Identity), AdapterJson.Default.Sharing)
                ? request : null;
        public bool AreCompatible(ReferenceSharingParticipant own, ReferenceSharingParticipant peer) =>
            own.Authorization.Decision == peer.Authorization.Decision && own.Identity.Namespace == "sample.service" && peer.Identity.Namespace == "sample.service" &&
            own.Identity.Value == peer.Identity.Value;
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => node.Action == Action && output.Contract == Output.Contract &&
            output.Key == Output.Key && Decode(output.Payload) is { } value && value.Resource == node.ResourceId.Value &&
            value.Environment == node.References.Environment.ToString() && value.Provider == node.References.ProviderInstance.ToString() &&
            value.Revision == node.References.ConfigurationRevision.Value;
        public bool ValidatePublicationMetadata(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
            IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration configuration, IReadOnlyCollection<ProviderResourceReservation> reservations)
        {
            try
            {
                var value = new PostgreSqlProviderConfigurationCodec(capabilities).DecodeTyped(configuration.PayloadJson);
                return value.ApplicationCredential is not null && Validate(accepted.Node, output) &&
                    output.Payload == JsonSerializer.Serialize(OutputFor(accepted, value), AdapterJson.Default.AccessOutput);
            }
            catch (InvalidDataException) { return false; }
        }
        public bool ValidateReuse(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
            IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration configuration, IReadOnlyCollection<ProviderResourceReservation> reservations) =>
            ValidatePublicationMetadata(key, accepted, output, sources, configuration, reservations);
        public bool ValidateObservationEvidence(AcceptedOperation accepted, DurableActionPayload evidence) => evidence == ObservationFor(accepted);
        public bool ValidateReferenceIdentities(AcceptedOperation accepted, IReadOnlyList<ProviderResourceReservation> identities) => identities.Count == 1 &&
            identities[0] == new ProviderResourceReservation(accepted.Provider.Instance, accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId,
                accepted.Node.ResourceId, "sample.service", Input(accepted.Node.Input)!.Identity);
    }
    private sealed class Handler(IDurableOperationJournal journal, IExternalOperationOutputs outputs, IResourceObservationAcceptance acceptance,
        IProviderConfigurationStore configurations, ISharedCredentialSystemAccess system, Boundary boundary) :
        IDurableActionAdmissionHandler, IDurableResourcePlanComposer, IReadOnlyResourceObservationHandler, IDurableResourceReusePolicy
    {
        public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
            DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            inputs.Acquisitions.Resource(resource).Mode == ResourceAcquisitionMode.Reference &&
            accepted.Resources.TryGetValue(resource, out var original) && target.Resources.TryGetValue(resource, out var current) &&
            original.Type == current.Type && original.ProviderRef == current.ProviderRef && original.DeletionPolicy == current.DeletionPolicy &&
            original.Properties.Equals(current.Properties);
        public DurableActionExecutionKey Key => Action;
        public bool RequiresExternalOutput => true;
        public ExternalOperationOutputIdentity OutputIdentity => Output;
        public bool Validate(ManifestLocalId resource, NormalizedResource declaration, DurablePlanningInputs inputs) =>
            Input(inputs.Acquisitions.Resource(resource).Input!) is { Identity: "verified-service" };
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resource, NormalizedResource declaration, DurablePlanningInputs inputs)
        {
            var provider = inputs.Providers.Resource(resource);
            return [new(OperationId.Create(resource.Value + "-reference"), resource, Action,
                new(provider.Environment, provider.Instance, provider.Configuration, provider.Implementation.Value), inputs.Acquisitions.Resource(resource).Input!, [])
            { RequiresExternalOutput = true, RequiredOutput = Output }];
        }
        public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            Validate(resource, spec.Resources[resource], inputs);
        public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            Plan(resource, spec.Resources[resource], inputs);
        public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, DurableOperationGraph graph)
        {
            var expected = Plan(resource, spec.Resources[resource], inputs).Single();
            return graph.Nodes.Count(x => x.ResourceId == resource) == 1 && graph.Nodes.Any(x => x.Id == expected.Id && x.Action == expected.Action &&
                x.Input == expected.Input && x.References == expected.References && x.Dependencies.Count == 0 && x.RequiredOutput == expected.RequiredOutput);
        }
        public bool Supports(DurableOperationNode node) => node.Action == Action && Input(node.Input) is { Identity: "verified-service" };
        public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken token)
        {
            var accepted = await outputs.FindAcceptedAsync(operation.Key, token);
            if (accepted is null) return new(DurableExecutionOutcome.InputUnavailable, null);
            var configuration = await configurations.FindRevisionAsync<PostgreSqlProviderConfiguration>(accepted.Inputs.Environment,
                accepted.Provider.Instance, accepted.Provider.Configuration, token);
            if (configuration?.Configuration is not { SystemAccessCredential: { } systemReference, ApplicationCredential: not null } pinned)
                return new(DurableExecutionOutcome.InputUnavailable, null);
            using var material = await system.ResolveAsync(new(accepted.Inputs.Environment, accepted.Provider.Instance, accepted.Provider.Configuration,
                PostgreSqlProviderConfigurationCodec.SystemAccessCredentialSlot, SharedCredentialUse.SystemAccess, systemReference), 65536, token);
            if (material is null) return new(DurableExecutionOutcome.InputUnavailable, null);
            var group = accepted.Graph.ObservationGroups.Single(x => x.Operations.Contains(accepted.Node.Id));
            var confirmations = new List<ObservationOperationConfirmation>();
            var identities = new List<ProviderResourceReservation>();
            foreach (var id in group.Operations)
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
                var own = await outputs.FindAcceptedAsync(key, token);
                if (own is null) return new(DurableExecutionOutcome.InputUnavailable, null);
                confirmations.Add(new(key, attempt, new(Output.Contract, Output.Key, JsonSerializer.Serialize(OutputFor(own, pinned), AdapterJson.Default.AccessOutput))));
                identities.Add(new(own.Provider.Instance, own.Inputs.Environment, own.Inputs.Revision.ApplicationRecordId, own.Node.ResourceId,
                    "sample.service", Input(own.Node.Input)!.Identity));
            }
            Interlocked.Increment(ref boundary.Observations);
            var result = await acceptance.CompleteAsync(new(confirmations, [], identities, ObservationFor(accepted)), DateTimeOffset.UtcNow, token);
            return new(result.IsSuccess ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.InputUnavailable, await journal.FindAsync(operation.Key, token));
        }
    }

    private sealed record Definition(ConfigurationObject? SuppliedDefaults = null) : ConfigurationRendererContract(RendererId, Type, Output.Contract, Output.Key, Action, FormatterId)
    {
        public override ConfigurationObject Defaults => SuppliedDefaults ?? Options("1", "5000", "10", "default");
        public override bool ValidateOptions(ConfigurationObject options, bool complete) => options.Members.All(x => x.Key switch
        {
            "consumer" => Integer(x.Value, 0, 1023),
            "connectTimeout" => Integer(x.Value, 1, 60000),
            "client" => x.Value is ConfigurationObject client && client.Members.ContainsKey("timeout") &&
                client.Members.All(p => p.Key == "timeout" ? Integer(p.Value, 1, 60000) : p.Key == "label" && p.Value is ConfigurationString { Value.Length: > 0 and <= 32 }),
            _ => false
        }) && (!complete || options.Members.Count == 3);
        public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => options;
    }
    private static bool Integer(ConfigurationValue value, int minimum, int maximum)
    {
        if (value is not ConfigurationNumber number) return false;
        var parts = number.CanonicalText.Split('e');
        if (!BigInteger.TryParse(parts[0], out var coefficient) || !BigInteger.TryParse(parts[1], out var exponent) || exponent < 0 || exponent > 5) return false;
        var exact = coefficient * BigInteger.Pow(10, (int)exponent);
        return exact >= minimum && exact <= maximum;
    }
    private static ConfigurationObject Options(string consumer, string timeout, string clientTimeout, string? label = null) => new(new Dictionary<string, ConfigurationValue>
    {
        ["consumer"] = ConfigurationNumber.Parse(consumer),
        ["connectTimeout"] = ConfigurationNumber.Parse(timeout),
        ["client"] = new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["timeout"] = ConfigurationNumber.Parse(clientTimeout) }
            .Concat(label is null ? [] : new Dictionary<string, ConfigurationValue> { ["label"] = new ConfigurationString(label) }))
    });
    private sealed record FormatDefinition() : ConfigurationFormatterContract(FormatterId)
    {
        public override bool ValidateArguments(ConfigurationObject args) => args.Members.Count == 3 &&
            args.Members.GetValueOrDefault("output") is ConfigurationString text && Decode(text.Value) is not null &&
            args.Members.GetValueOrDefault("options") is ConfigurationObject options && new Definition().ValidateOptions(options, true) &&
            args.Members.GetValueOrDefault("credential") is ConfigurationSecretReference;
    }
    private sealed class Rendering(Boundary boundary) : IConfigurationRenderer, IConfigurationFormatter, IConfigurationResolutionBinding
    {
        ConfigurationRendererId IConfigurationRenderer.Contract => RendererId;
        ConfigurationFormatterId IConfigurationFormatter.Contract => FormatterId;
        public ManifestLocalId Identity => Implementation;
        public ConfigurationFormatterId Formatter => FormatterId;
        ManifestLocalId IConfigurationResolutionBinding.Implementation => SharedReferenceDeliveryTests.Implementation;
        public ConfigurationFragment? Render(ExternalOperationOutput output, ConfigurationObject settings)
        {
            var value = Decode(output.Payload);
            return value is null ? null : new(new ConfigurationDeferredValue(FormatterId, new(new Dictionary<string, ConfigurationValue>
            {
                ["output"] = new ConfigurationString(output.Payload),
                ["options"] = settings,
                ["credential"] = new ConfigurationSecretReference(new(SecretId.Parse(value.Secret), SecretVersionId.Parse(value.Version)))
            })));
        }
        public SecretResolutionReference? Bind(ConfigurationRenderPin pin, ExternalOperationOutput output, ConfigurationDeferredValue deferred)
        {
            var value = Decode(output.Payload);
            if (value is null || output.Contract != Output.Contract || output.Key != Output.Key || value.Resource != pin.ResourceLocalId.Value ||
                deferred.Formatter != FormatterId || !new FormatDefinition().ValidateArguments(deferred.Arguments) ||
                deferred.Arguments.Members["output"] is not ConfigurationString text || text.Value != output.Payload ||
                !deferred.Arguments.Members["options"].Equals(pin.Options) || deferred.Arguments.Members["credential"] is not ConfigurationSecretReference secret ||
                secret.Reference.SecretId.ToString() != value.Secret || secret.Reference.SecretVersionId.ToString() != value.Version) return null;
            var scope = new SharedCredentialScope(EnvironmentRecordId.Parse(value.Environment), ProviderInstanceId.Parse(value.Provider),
                ProviderConfigurationRevisionNumber.Create(value.Revision), value.Slot, SharedCredentialUse.ApplicationDelivery, secret.Reference);
            scope = boundary.BindingFault switch
            {
                "use" => scope with { Use = SharedCredentialUse.SystemAccess },
                "environment" => scope with { Environment = EnvironmentRecordId.New() },
                "provider" => scope with { Provider = ProviderInstanceId.New() },
                "revision" => scope with { Revision = ProviderConfigurationRevisionNumber.Create(value.Revision + 1) },
                "slot" => scope with { Slot = PostgreSqlProviderConfigurationCodec.SystemAccessCredentialSlot },
                "version" => scope with { Reference = new(secret.Reference.SecretId, SecretVersionId.New()) },
                _ => scope
            };
            return new(scope.Reference, boundary.BindingFault == "purpose" ? SecretPurpose.Configuration : SecretPurpose.SharedApplicationCredential, scope);
        }
        public bool ValidateMaterial(ReadOnlySpan<char> material) => !material.IsEmpty;
        public ConfidentialConfigurationValue? Format(ConfigurationObject args, ReadOnlySpan<char> material)
        {
            Interlocked.Increment(ref boundary.FormatterCalls);
            if (boundary.ThrowFormatter || boundary.ThrowFormatterAtCall == boundary.FormatterCalls) throw new InvalidDataException("Test formatter refused.");
            if (!new FormatDefinition().ValidateArguments(args)) return null;
            var value = Decode(((ConfigurationString)args.Members["output"]).Value)!;
            var options = (ConfigurationObject)args.Members["options"];
            return new(new ConfigurationObject(new Dictionary<string, ConfigurationValue>
            {
                ["EndPoints"] = new ConfigurationArray([new ConfigurationObject(new Dictionary<string, ConfigurationValue>
                    { ["Host"] = new ConfigurationString(value.Host), ["Port"] = ConfigurationNumber.Parse(value.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) })]),
                ["Consumer"] = options.Members["consumer"],
                ["ConnectTimeout"] = options.Members["connectTimeout"],
                ["Client"] = options.Members["client"],
                ["Password"] = new ConfigurationString(material.ToString())
            }));
        }
    }

    private static ServiceProvider Services(string connection, Boundary boundary, string? registrationFault = null)
    {
        var rendering = new Rendering(boundary);
        var formatters = new ConfigurationFormatterCatalog([new FormatDefinition()], [new(Implementation, FormatterId)]);
        var services = new ServiceCollection().AddSingleton(boundary).AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec().AddPostgreSqlProviderAliasStore().AddSingleton<IProviderRevisionPreflight, Ready>()
            .AddPostgreSqlDurableDesiredStateCommitter().AddPostgreSqlDurableOperationJournal().AddPostgreSqlResourceObservationAcceptance()
            .AddPostgreSqlProviderResourceReservations().AddSingleton<DurableOperationSerialization>().AddSingleton<ExactDurableOperationExecutor>()
            .AddSingleton<IDurableOperationExecutor>(p => p.GetRequiredService<ExactDurableOperationExecutor>()).AddSingleton<DurableGenerationRunner>()
            .AddSingleton<Handler>().AddSingleton<IDurableActionAdmissionHandler>(p => p.GetRequiredService<Handler>())
            .AddSingleton<IDurableActionHandler>(p => p.GetRequiredService<Handler>()).AddSingleton<IExternalOperationOutputContract, Evidence>()
            .AddSingleton(formatters).AddSingleton(new ConfigurationRenderingCatalog([new Definition(registrationFault == "defaults" ? Options("9", "6000", "90", "new-default") : null)], registrationFault == "renderer" ? [] : [rendering], formatters, registrationFault == "formatter" ? [] : [rendering]))
            .AddSingleton<IConfigurationResolutionBinding>(rendering).AddSingleton<ComposeConfigurationDocument>()
            .AddSingleton<PostgreSqlConfigurationApplicationAuthorization>().AddSingleton<IConfigurationApplicationAuthorization>(p => p.GetRequiredService<PostgreSqlConfigurationApplicationAuthorization>())
            .AddSingleton<PostgreSqlConfigurationDocumentStore>().AddSingleton<IConfigurationDocumentStore>(p => p.GetRequiredService<PostgreSqlConfigurationDocumentStore>())
            .AddSingleton<PostgreSqlConfigurationAccessStore>().AddSingleton<IConfigurationAccessStore>(p => p.GetRequiredService<PostgreSqlConfigurationAccessStore>())
            .AddSingleton<IConfigurationDeliveryDocumentStore>(p => p.GetRequiredService<PostgreSqlConfigurationAccessStore>()).AddSingleton<DeliverConfiguration>();
        if (registrationFault == "binding") services.RemoveAll<IConfigurationResolutionBinding>();
        if (registrationFault == "evidence") services.RemoveAll<IExternalOperationOutputContract>();
        services.AddSingleton<IInternalActionHandler, Haby.Kubernetes.InternalDeploymentAction>()
            .AddSingleton<Haby.Kubernetes.KubernetesProviderConfigurationCodec>()
            .AddSingleton<IProviderConfigurationCodec>(p => p.GetRequiredService<Haby.Kubernetes.KubernetesProviderConfigurationCodec>());
        services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.UseNpgsql(connection).AddInterceptors(boundary));
        return services.BuildServiceProvider();
    }
    private sealed class Ready : IProviderRevisionPreflight
    {
        public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, ProviderAliasBinding binding, CancellationToken token) =>
            ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
    }
    private sealed record Scenario(string Connection, Boundary Boundary, EnvironmentRecordId Environment, ProviderInstanceId Provider, SecretRef Secret)
    {
        public override string ToString() => "[Reference delivery scenario]";
    }
    private async Task<Scenario> Database()
    {
        var name = "haby_reference_delivery_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString); await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin).ExecuteNonQueryAsync();
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = true, MaxPoolSize = 16 }.ConnectionString;
        var boundary = new Boundary();
        await using var services = Services(connection, boundary);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environment = (await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("pilot"), default)).Environment;
        using var material = new SecretMaterial("shared-application-test-material"u8.ToArray());
        foreach (var item in new[] { ("admin", SecretPurpose.ProviderAdministrator), ("shared", SecretPurpose.SharedApplicationCredential) })
            Assert.Equal(SecretOutcome.Created, (await services.GetRequiredService<SecretAdministration>().CreateAsync("pilot", item.Item1, Guid.NewGuid(), material, "creator", default, item.Item2)).Outcome);
        var declaration = ProviderDeclarationTests.Declaration.Replace("\"secretRefs\":{", "\"secretRefs\":{\"system-access-credential\":{\"secretName\":\"shared\",\"version\":1},\"application-credential\":{\"secretName\":\"shared\",\"version\":1},", StringComparison.Ordinal);
        var provider = await services.GetRequiredService<ProviderAdministration>().ApplyAsync(Encoding.UTF8.GetBytes(declaration), null, false, null, "issuer", default);
        Assert.Equal(ProviderOutcome.Created, provider.Outcome);
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment.RecordId,
            ProviderAlias.Create("shared.primary"), null, new(provider.Provider!.InstanceId, ProviderConfigurationRevisionNumber.Create(1)), "operator", default)).Outcome);
        var profile = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
            services.GetRequiredService<IConfigurationProfileImportStore>(), TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"shared-layout","revision":1},"spec":{
             "documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{"Mode":"frozen"}}},
             "bindingRules":{"shared":{"slot":"service","rendererRef":"sample.shared-access/v1","options":{"consumer":2,"client":{"timeout":20}},
              "documentProjections":[{"documentRef":"settings","path":"/Access"}]}}}}
            """), "profile-author"));
        Assert.Equal(AssignmentOutcome.Created, (await services.GetRequiredService<IProfileAssignmentStore>().ApplyAsync(environment,
            new(new ProfileAssignmentTarget.Environment(), [new ProfileDecision.Use(profile.Revision.Reference)]), new AssignmentPrecondition.ExpectedAbsence(),
            new("operator", DateTimeOffset.UtcNow, null), default)).Outcome);
        var secret = await services.GetRequiredService<SecretAdministration>().GetAsync("pilot", "shared", null, default);
        return new(connection, boundary, environment.RecordId, provider.Provider.InstanceId, new(secret.Secret!.SecretId, secret.Secret.Versions[0].VersionId));
    }
    private static JsonNode Declaration(string application)
    {
        var declaration = JsonNode.Parse("""
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"reference-consumer"},"spec":{
             "resources":{"left":{"type":"sample.shared-access/v1","providerRef":"shared.primary","properties":{}},
                          "right":{"type":"sample.shared-access/v1","providerRef":"shared.primary","properties":{}}},
             "components":{"api":{"bindings":{"service":{"target":{"kind":"local-resource","resourceRef":"left"},"configurationSlot":"service","options":{"consumer":0}}}},
                           "worker":{"bindings":{"service":{"target":{"kind":"local-resource","resourceRef":"right"},"configurationSlot":"service","options":{"consumer":3,"client":{"timeout":30,"label":"worker"}}}}}}}}}
            """u8)!;
        declaration["manifest"]!["metadata"]!["id"] = application;
        return declaration;
    }
    private static async Task<DurableChangeSetCommit> Confirm(Scenario scenario, string application = "reference-consumer")
    {
        scenario.Boundary.ResetMaterial();
        await using var services = Services(scenario.Connection, scenario.Boundary);
        var declaration = Declaration(application);
        var catalog = new ManifestContractCatalog([new(Type, "{\"type\":\"object\",\"additionalProperties\":false}", OptionsSchema)], [], [], [new(RendererId)]);
        var imported = await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(),
            catalog, TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes(declaration.ToJsonString()), "manifest-author");
        Assert.False(imported is ImportApplicationManifestResult.InvalidManifest,
            imported is ImportApplicationManifestResult.InvalidManifest invalid ? string.Join(";", invalid.ValidationReport.Diagnostics.Select(x => x.Code)) : "");
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(imported).ManifestRevisionRef;
        var input = new DurableActionPayload("sample.reference-input/v1", JsonSerializer.Serialize(new ReferenceInput("verified-service"), AdapterJson.Default.ReferenceInput));
        var sharing = new DurableActionPayload("sample.sharing/v1", JsonSerializer.Serialize(new Sharing("verified-service"), AdapterJson.Default.Sharing));
        var requests = new[] { "left", "right" }.Select(r => new ResourceAcquisitionRequest(ManifestLocalId.Create(r), ResourceAcquisitionMode.Reference, input) { SharingRequest = sharing }).ToArray();
        var planner = Assert.IsType<PostgreSqlDurableDeploymentPlanner>(services.GetRequiredService<IDurableDeploymentPlanner>());
        var change = (await planner.CreateAcquisitionChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision,
            new(requests), "operator", default)).ChangeSet!;
        var result = await planner.CreatePlanAsync(change.Id, "operator", default);
        Assert.Equal(DurablePlanStatus.Planned, result.Status);
        Assert.Equal(0, scenario.Boundary.MaterialReads);
        Assert.Equal(0, scenario.Boundary.PayloadQueries);
        var plan = result.Plan!;
        Assert.Equal(ConfigurationNumber.Parse("0"), plan.Inputs.Configuration!.RenderPins.Single(x => x.Component.Value == "api").Options.Members["consumer"]);
        var api = plan.Inputs.Configuration.RenderPins.Single(x => x.Component.Value == "api");
        Assert.Equal(ProfileOptionScope.Binding, api.OptionOrigins["consumer"]);
        Assert.Equal(ProfileOptionScope.Profile, api.OptionOrigins["client"]);
        Assert.False(((ConfigurationObject)api.Options.Members["client"]).Members.ContainsKey("label"));
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "operator", default)).Commit;
        foreach (var node in plan.Graph.Nodes)
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await services.GetRequiredService<IDurableOperationExecutor>()
                .ExecuteAsync(new(commit.DeploymentId, commit.Generation, node.Id), default)).Outcome);
        Assert.True(await services.GetRequiredService<IDurableOperationJournal>().TryCompleteGenerationAsync(commit.DeploymentId, commit.Generation,
            services.GetRequiredService<DurableActionAdmission>(), default));
        return commit;
    }

    [Fact]
    public async Task Confirmed_two_Resource_Component_bindings_materialize_and_deliver_one_shared_version_as_complete_JSON()
    {
        var scenario = await Database();
        var commit = await Confirm(scenario);
        scenario.Boundary.ResetMaterial();
        await using var services = Services(scenario.Connection, scenario.Boundary);
        var documents = services.GetRequiredService<IConfigurationDocumentStore>();
        var access = services.GetRequiredService<IConfigurationAccessStore>();
        var authority = services.GetRequiredService<ISharedCredentialAdministration>();
        Assert.All(await authority.InspectAsync(scenario.Environment, scenario.Provider, ProviderConfigurationRevisionNumber.Create(1), default),
            binding => Assert.Empty(binding.Dependencies));
        var created = new List<ConfigurationDocumentRevisionId>();
        foreach (var component in new[] { "api", "worker" })
        {
            var target = new ConfigurationDocumentTarget(commit.DeploymentId, commit.Generation, ManifestLocalId.Create(component), ManifestLocalId.Create("settings"));
            var materialized = await documents.MaterializeAsync(target, "operator", default);
            Assert.Equal(ConfigurationDocumentStatus.Created, materialized.Status);
            Assert.Equal(0, scenario.Boundary.MaterialReads);
            Assert.Equal(0, scenario.Boundary.PayloadQueries);
            created.Add(materialized.Revision!.Metadata.Id);
            var dependencies = (await authority.InspectAsync(scenario.Environment, scenario.Provider, ProviderConfigurationRevisionNumber.Create(1), default))
                .Single(x => x.Scope.Use == SharedCredentialUse.ApplicationDelivery).Dependencies;
            Assert.Equal(created.Select(x => x.ToString()).Order(StringComparer.Ordinal), dependencies.Select(x => x.Document.ToString()).Order(StringComparer.Ordinal));
            Assert.All(dependencies, x => Assert.True(x.ActiveAssociation));
            Assert.Equal(0, scenario.Boundary.PayloadQueries);
            var stored = ConfigurationDocumentCodec.Encode(materialized.Revision!);
            Assert.DoesNotContain("shared-application-test-material", stored);
            var preview = ConfigurationJson.Preview(materialized.Revision!.Tree);
            Assert.Equal(new[] { "/Access" }, preview.RedactedPaths);
            Assert.DoesNotContain("shared-application-test-material", preview.Json);
            var grant = await access.CreateAsync(new(EnvironmentId.Create("pilot"), Haby.Domain.ApplicationId.Create("reference-consumer"),
                ManifestLocalId.Create(component), ManifestLocalId.Create("settings"), null), "operator", null, default);
            Assert.NotNull(grant);
            using var delivered = await services.GetRequiredService<DeliverConfiguration>().ExecuteAsync(grant.Credential, grant.Grant.Document.Id, default);
            Assert.Equal(ConfigurationDeliveryStatus.Delivered, delivered.Status);
            Assert.Equal(1, scenario.Boundary.MaterialReads);
            Assert.Equal(2, scenario.Boundary.PayloadQueries);
            using var json = JsonDocument.Parse(delivered.Json);
            var value = json.RootElement.GetProperty("Access"u8);
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
            Assert.Equal("frozen", json.RootElement.GetProperty("Mode"u8).GetString());
            Assert.Equal("shared-application-test-material", value.GetProperty("Password"u8).GetString());
            Assert.Equal(ConfigurationNumber.Parse(component == "api" ? "0" : "3"), ConfigurationNumber.Parse(value.GetProperty("Consumer"u8).GetRawText()));
            Assert.Equal(component == "worker", value.GetProperty("Client"u8).TryGetProperty("label"u8, out _));
            Assert.Equal(stored, ConfigurationDocumentCodec.Encode((await documents.ReadAsync(target, default)).Revision!));
            scenario.Boundary.ResetMaterial();
        }
        await using var check = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await check.ConfigurationSharedUses.CountAsync());
        Assert.Equal(2, await check.ResourceManagement.CountAsync());
        Assert.Equal(1, await (from secret in check.Secrets
                               join version in check.SecretVersions on secret.Id equals version.SecretId
                               join payload in check.SecretPayloads on version.Id equals payload.SecretVersionId
                               where secret.Purpose == "SharedApplicationCredential"
                               select payload.SecretVersionId).CountAsync());
        Assert.Empty(await check.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
    }
}
