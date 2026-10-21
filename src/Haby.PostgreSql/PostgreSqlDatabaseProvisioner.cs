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

/// <summary>Build-time-composed implementation of the single checkpoint Resource.</summary>
public sealed class PostgreSqlDatabaseProvisioner(InMemoryPostgreSqlConfigurationCatalog configurations) :
    ICheckpointProviderCapability, ICheckpointDatabaseProvisioner
{
    public const string ImplementationId = "haby.postgresql.database/v1alpha1";
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public bool Supports(ProviderResolution selection, ResourceTypeId contract) =>
        contract == PostgreSqlDatabaseContract.Id && selection.ImplementationId == ImplementationId;

    public async ValueTask<CheckpointProvisionOutcome> CreateAsync(ProviderResolution exactProvider,
        string databaseName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactProvider);
        if (!Supports(exactProvider, PostgreSqlDatabaseContract.Id) || !ValidName(databaseName))
            return CheckpointProvisionOutcome.PreDispatchFailure;
        if (!configurations.TryGet(exactProvider, out var connectionString))
            return CheckpointProvisionOutcome.ConfigurationUnavailable;

        NpgsqlConnectionStringBuilder builder;
        var dispatched = false;
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
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", connection))
            {
                exists.Parameters.AddWithValue(databaseName);
                if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
                    return CheckpointProvisionOutcome.Conflict;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var quote = new NpgsqlCommandBuilder();
            var sql = "CREATE DATABASE " + quote.QuoteIdentifier(databaseName);
            // From this point, a cancellation or transport error cannot prove that creation did not occur.
            try
            {
                await using var create = new NpgsqlCommand(sql, connection);
                dispatched = true;
                await create.ExecuteNonQueryAsync(cancellationToken);
                return CheckpointProvisionOutcome.Created;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
            {
                return CheckpointProvisionOutcome.Conflict;
            }
            catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or System.IO.IOException)
            {
                return CheckpointProvisionOutcome.UncertainOutcome;
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or System.IO.IOException or InvalidOperationException)
        {
            return dispatched ? CheckpointProvisionOutcome.UncertainOutcome : CheckpointProvisionOutcome.PreDispatchFailure;
        }
    }

    private static bool ValidName(string? name) => name is { Length: >= 1 and <= 63 } && NamePattern.IsMatch(name);
}
