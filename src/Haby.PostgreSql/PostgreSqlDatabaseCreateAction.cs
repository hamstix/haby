using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

/// <summary>Frozen interpretation of historical ResourceId + Create database operations.</summary>
public sealed class PostgreSqlDatabaseCreateAdmission : DurableActionAdmissionHandler<PostgreSqlDatabaseCreateInput>
{
    public override DurableActionExecutionKey Key => PostgreSqlDatabaseCreateAction.RegistrationKey;
    public override PostgreSqlDatabaseCreateInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
        PostgreSqlDatabaseCreateValidation.Prepare(resource, inputs);
}

public sealed class PostgreSqlDatabaseCreateAction(PostgreSqlDurableCreateOperationExecutor executor)
    : DurableActionHandler<PostgreSqlDatabaseCreateInput>
{
    public static OperationContractId Contract { get; } = OperationContractId.Create("postgresql.database.create/v1alpha1");
    public static DurableActionExecutionKey RegistrationKey { get; } = new(
        new(PostgreSqlDatabaseContract.Id, new("postgresql/v1", "haby.postgresql.connection/v1",
            PostgreSqlDatabaseCheckpointCapability.ImplementationId)), Contract);
    public override DurableActionExecutionKey Key => RegistrationKey;

    public override PostgreSqlDatabaseCreateInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
        PostgreSqlDatabaseCreateValidation.Prepare(resource, inputs);

    public override ValueTask<DurableCreateExecutionResult> ExecuteTypedAsync(DurableCreateOperation operation,
        PostgreSqlDatabaseCreateInput? input, CancellationToken cancellationToken) =>
        executor.ExecuteUnderLockAsync(operation.Key, input, cancellationToken);
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
        // Register separately so saved Create remains explicitly interpretable when an action is removed.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(DurableCreateActionMapping) &&
            descriptor.ImplementationInstance is DurableCreateActionMapping mapping &&
            mapping.ResourceContract == PostgreSqlDatabaseContract.Id))
            services.AddSingleton(new DurableCreateActionMapping(PostgreSqlDatabaseContract.Id, PostgreSqlDatabaseCreateAction.Contract));
        services.TryAddSingleton<DurableActionRegistry>();
        services.TryAddSingleton<DurableActionAdmission>();
        services.TryAddSingleton<DurableOperationSerialization>();
        return services;
    }
}
