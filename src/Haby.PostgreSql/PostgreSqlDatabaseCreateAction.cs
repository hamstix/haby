using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

/// <summary>One exact database admission, explicitly composing standalone and pair planning.</summary>
public sealed class PostgreSqlDatabaseCreateAdmission : DurableActionAdmissionHandler<PostgreSqlDatabaseActionInput>, IDurableResourcePlanComposer, IDurableResourceReusePolicy, IDurableResourceAdditionPolicy, IDurableResourceRetentionPolicy, IDurableResourceNamingCompatibility, IResourceNamingPolicyHandler
{
    public bool SupportsNaming(ResourceNamingPolicy policy) => PostgreSqlResourceNaming.Supports(policy);

    public bool SameNames(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs original,
        DurablePlanningInputs proposed, PreparedProviderConfiguration configuration) =>
        PostgreSqlResourceNaming.SameNames(spec, resource, original.Naming.Policy, proposed.Naming.Policy);

    public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target,
        ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        (Standalone(accepted) && Standalone(target) ||
            PostgreSqlPairResourceAdmission.SupportsShape(accepted, inputs, configuration) &&
            PostgreSqlPairResourceAdmission.SupportsShape(target, inputs, configuration)) &&
        accepted.Resources.TryGetValue(resource, out var before) && target.Resources.TryGetValue(resource, out var after) &&
        before.Type == after.Type && before.ProviderRef == after.ProviderRef && before.Properties.Equals(after.Properties) &&
        before.DeletionPolicy == after.DeletionPolicy &&
        PostgreSqlPairResourceAdmission.IsIndependentDatabase(accepted, resource) == PostgreSqlPairResourceAdmission.IsIndependentDatabase(target, resource) &&
        (Standalone(accepted) || PostgreSqlPairResourceAdmission.IsIndependentDatabase(accepted, resource) ||
            pair.CanReuse(accepted, target, resource, inputs, configuration));

    public bool CanAdd(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        PostgreSqlPairResourceAdmission.CanAddPair(accepted, target, resource, inputs, configuration) ||
        target.Resources.Count == accepted.Resources.Count + 1 &&
        PostgreSqlPairResourceAdmission.SupportsShape(accepted, inputs, configuration) &&
        PostgreSqlPairResourceAdmission.SupportsShape(target, inputs, configuration) &&
        PostgreSqlPairResourceAdmission.IsIndependentDatabase(target, resource);

    public bool CanRetain(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        PostgreSqlPairResourceAdmission.CanRetainPair(original, target, resource, inputs, configuration) ||
        PostgreSqlPairResourceAdmission.SupportsShape(original, inputs, configuration) &&
        PostgreSqlPairResourceAdmission.SupportsShape(target, inputs, configuration) &&
        PostgreSqlPairResourceAdmission.IsIndependentDatabase(original, resource) &&
        original.Resources[resource].DeletionPolicy == ResourceDeletionPolicy.Retain &&
        !target.Resources.ContainsKey(resource) &&
        target.Resources.Values.Where(r => r.Type == PostgreSqlDatabaseContract.Id).All(r =>
            PostgreSqlDatabaseCreateValidation.Name(r) != PostgreSqlDatabaseCreateValidation.Name(original.Resources[resource]));

    public override DurableActionExecutionKey Key => PostgreSqlDatabaseCreateAction.RegistrationKey;
    public override PostgreSqlDatabaseActionInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
        PostgreSqlDatabaseCreateValidation.Name(resource) is { } name && PostgreSqlResourceNaming.Database(name, inputs.Naming.Policy) is { } physical ? new(physical) : null;
    public override IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
    {
        var references = new DurableActionInputReferences(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, inputs.ImplementationId);
        var input = Prepare(resource, references with { Naming = inputs.Naming }) ?? throw new InvalidDataException("Invalid database input.");
        return [new(OperationId.Create(resourceId.Value + "-create"), resourceId, Key, references,
            new(PostgreSqlDatabaseCreateAction.Format, JsonSerializer.Serialize(input, PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput)), [])];
    }

    private readonly PostgreSqlPairResourceAdmission pair = new(PostgreSqlDatabaseContract.Id);
    private static bool Standalone(NormalizedManifestSpec spec) => CheckpointDatabaseShape.IsSupported(spec);
    private static bool StandaloneResource(NormalizedManifestSpec spec, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => Standalone(spec) ||
        PostgreSqlPairResourceAdmission.SupportsShape(spec, inputs, configuration) &&
        PostgreSqlPairResourceAdmission.IsIndependentDatabase(spec, resource);
    public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration) => StandaloneResource(spec, resource, inputs, configuration)
            ? spec.Resources.ContainsKey(resource) && Validate(spec.Resources[resource], inputs)
            : pair.Validate(spec, resource, inputs, configuration);
    public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => StandaloneResource(spec, resource, inputs, configuration)
            ? Plan(resource, spec.Resources[resource], inputs) : pair.Plan(spec, resource, inputs, configuration);
    public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration, DurableOperationGraph graph) => StandaloneResource(spec, resource, inputs, configuration)
            ? new DurableOperationGraph(Plan(resource, spec.Resources[resource], inputs)).Equals(new DurableOperationGraph(graph.Nodes.Where(n => n.ResourceId == resource)))
            : pair.ValidateGraph(spec, resource, inputs, configuration, graph);
}

public sealed class PostgreSqlDatabaseCreateAction(PostgreSqlDurableOperationExecutor executor)
    : DurableActionHandler<PostgreSqlDatabaseActionInput>
{
    public static OperationContractId Contract { get; } = OperationContractId.Create("postgresql.database.create/v1alpha1");
    public static DurableActionExecutionKey RegistrationKey { get; } = new(
        new(PostgreSqlDatabaseContract.Id, new("postgresql/v1", "haby.postgresql.connection/v1",
            PostgreSqlDatabaseCheckpointCapability.ImplementationId)), Contract);
    public override bool RequiresExternalOutput => true;
    public override DurableActionExecutionKey Key => RegistrationKey;

    public override IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) =>
        [PrepareNode(OperationId.Create(resourceId.Value + "-create"), resourceId, resource, inputs)];
    public const string Format = "haby.postgresql.database-create/v1";
    public override string InputFormat => Format;
    public override JsonTypeInfo<PostgreSqlDatabaseActionInput> InputTypeInfo => PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput;
    public override PostgreSqlDatabaseActionInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
        PostgreSqlDatabaseCreateValidation.Name(resource) is { } name && PostgreSqlResourceNaming.Database(name, inputs.Naming.Policy) is { } physical ? new(physical) : null;

    public override ValueTask<DurableExecutionResult> ExecuteTypedAsync(DurableOperation operation,
        PostgreSqlDatabaseActionInput? input, CancellationToken cancellationToken) =>
        executor.ExecuteUnderLockAsync(operation.Key, input is null ? null : new(operation.Environment,
            operation.ProviderInstance, operation.ConfigurationRevision, operation.ImplementationId, input.DatabaseName), cancellationToken);
}

internal static class PostgreSqlDatabaseCreateValidation
{
    internal static PostgreSqlDatabaseCreateInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
        Name(resource) is { } name
            ? new(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, inputs.ImplementationId, name)
            : null;

    private static readonly System.Text.RegularExpressions.Regex SafeDatabase = new(@"\A[a-z][a-z0-9_]{0,62}\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking);
    internal static string? Name(NormalizedResource resource) =>
        resource.Type == PostgreSqlDatabaseContract.Id && resource.Properties.Members.Count == 1 &&
        resource.Properties.Members.TryGetValue("name", out var value) && value is ConfigurationString name &&
        SafeDatabase.IsMatch(name.Value) ? name.Value : null;
}

public static class DurableActionServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDatabaseCreateAction(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableActionAdmissionHandler, PostgreSqlDatabaseCreateAdmission>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalOperationOutputContract, PostgreSqlDatabaseOutputContract>());
        services.TryAddSingleton<DurableActionRegistry>();
        services.TryAddSingleton<DurableActionAdmission>();
        services.TryAddSingleton<DurableOperationSerialization>();
        return services;
    }
}

public sealed record PostgreSqlDatabaseActionInput(string DatabaseName);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PostgreSqlDatabaseActionInput))]
internal partial class PostgreSqlActionJsonContext : JsonSerializerContext;
