using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

/// <summary>Pure planning of the current frozen database action.</summary>
public sealed class PostgreSqlDatabaseCreateAdmission : DurableActionAdmissionHandler<PostgreSqlDatabaseActionInput>
{
    public override DurableActionExecutionKey Key => PostgreSqlDatabaseCreateAction.RegistrationKey;
    public override PostgreSqlDatabaseActionInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
        PostgreSqlDatabaseCreateValidation.Name(resource) is { } name ? new(name) : null;
    public override IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
    {
        var references = new DurableActionInputReferences(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, inputs.ImplementationId);
        var input = Prepare(resource, references) ?? throw new InvalidDataException("Invalid database input.");
        return [new(OperationId.Create(resourceId.Value + "-create"), resourceId, Key, references,
            new(PostgreSqlDatabaseCreateAction.Format, JsonSerializer.Serialize(input, PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput)), [])];
    }
}

public sealed class PostgreSqlDatabaseCreateAction(PostgreSqlDurableOperationExecutor executor)
    : DurableActionHandler<PostgreSqlDatabaseActionInput>
{
    public static OperationContractId Contract { get; } = OperationContractId.Create("postgresql.database.create/v1alpha1");
    public static DurableActionExecutionKey RegistrationKey { get; } = new(
        new(PostgreSqlDatabaseContract.Id, new("postgresql/v1", "haby.postgresql.connection/v1",
            PostgreSqlDatabaseCheckpointCapability.ImplementationId)), Contract);
    public override DurableActionExecutionKey Key => RegistrationKey;

    public override IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) =>
        [PrepareNode(OperationId.Create(resourceId.Value + "-create"), resourceId, resource, inputs)];
    public const string Format = "haby.postgresql.database-create/v1";
    public override string InputFormat => Format;
    public override JsonTypeInfo<PostgreSqlDatabaseActionInput> InputTypeInfo => PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput;
    public override PostgreSqlDatabaseActionInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
        PostgreSqlDatabaseCreateValidation.Name(resource) is { } name ? new(name) : null;

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
