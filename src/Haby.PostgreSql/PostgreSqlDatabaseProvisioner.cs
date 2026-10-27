using System.Text.RegularExpressions;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Npgsql;

namespace Haby.PostgreSql;

/// <summary>Exact, operator-owned checkpoint configuration held only in memory.</summary>
public sealed class InMemoryPostgreSqlConfigurationCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<(string InstanceId, long Revision), string> _connections = [];

    public void Add(string instanceId, long revision, string administrativeConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(administrativeConnectionString);
        lock (_gate)
        {
            if (!_connections.TryAdd((instanceId, revision), administrativeConnectionString))
                throw new ArgumentException("The configuration revision already exists.", nameof(revision));
        }
    }

    internal bool TryGet(ProviderResolution selection, out string connectionString)
    {
        lock (_gate) return _connections.TryGetValue((selection.ProviderInstanceId, selection.ConfigurationRevision),
            out connectionString!);
    }
}

/// <summary>Executes the checkpoint Resource with operator-owned configuration.</summary>
public sealed class PostgreSqlDatabaseProvisioner(InMemoryPostgreSqlConfigurationCatalog configurations) :
    ICheckpointProviderCapability, ICheckpointDatabaseProvisioner
{
    public const string ImplementationId = PostgreSqlDatabaseCheckpointCapability.ImplementationId;
    private static readonly PostgreSqlDatabaseCheckpointCapability Capability = new();
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public bool Supports(ProviderResolution selection, ResourceTypeId contract) =>
        Capability.Supports(selection, contract);

    public async ValueTask<CheckpointProvisionOutcome> CreateAsync(ProviderResolution exactProvider,
        string databaseName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactProvider);
        if (!Supports(exactProvider, PostgreSqlDatabaseContract.Id) || !ValidName(databaseName))
            return CheckpointProvisionOutcome.PreDispatchFailure;
        if (!configurations.TryGet(exactProvider, out var connectionString))
            return CheckpointProvisionOutcome.ConfigurationUnavailable;

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
            if (string.IsNullOrWhiteSpace(builder.Database)) builder.Database = "postgres";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return CheckpointProvisionOutcome.PreDispatchFailure;
        }

        try
        {
            // The absence read and the command are separate dedicated-connection operations;
            // presence before dispatch is a conflict and is never adopted.
            var absence = await PostgreSqlDatabaseGateway.CheckAbsentAsync(builder, databaseName, cancellationToken);
            if (absence == DatabaseAbsenceOutcome.Present) return CheckpointProvisionOutcome.Conflict;
            if (absence == DatabaseAbsenceOutcome.Connectivity) return CheckpointProvisionOutcome.PreDispatchFailure;

            cancellationToken.ThrowIfCancellationRequested();
            return await PostgreSqlDatabaseGateway.CreateDatabaseAsync(builder, databaseName, null, cancellationToken)
                switch
            {
                DatabaseCreateOutcome.Created => CheckpointProvisionOutcome.Created,
                DatabaseCreateOutcome.Conflict => CheckpointProvisionOutcome.Conflict,
                DatabaseCreateOutcome.ServerRejected => CheckpointProvisionOutcome.ServerRejected,
                // No session was established, so the command provably never reached the server.
                DatabaseCreateOutcome.OpenFailed => CheckpointProvisionOutcome.PreDispatchFailure,
                _ => CheckpointProvisionOutcome.UncertainOutcome
            };
        }
        catch (OperationCanceledException)
        {
            return CheckpointProvisionOutcome.PreDispatchFailure;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or InvalidOperationException)
        {
            return CheckpointProvisionOutcome.PreDispatchFailure;
        }
    }

    private static bool ValidName(string? name) => name is { Length: >= 1 and <= 63 } && NamePattern.IsMatch(name);
}
