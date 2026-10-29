using Haby.Domain;

namespace Haby.PostgreSql;

/// <summary>The exact pinned inputs for one PostgreSQL database Create; never resolved from a current alias.</summary>
public sealed record PostgreSqlDatabaseCreateInput(EnvironmentRecordId Environment, ProviderInstanceId ProviderInstance,
    ProviderConfigurationRevisionNumber ConfigurationRevision, string ImplementationId, string DatabaseName)
{
    public override string ToString() => nameof(PostgreSqlDatabaseCreateInput);
}

/// <summary>The bounded result of the read-only database-absence read.</summary>
public enum PostgreSqlDatabaseAbsenceOutcome
{
    Absent,
    Present,
    ProviderInputUnavailable,
    SecretUnavailable,
    Connectivity,
    /// <summary>Defensive revalidation: the identifier failed at the Provider trust boundary.</summary>
    DatabaseNameInvalid
}

/// <summary>The bounded result of the dispatched Create command after the marker was committed.</summary>
public enum PostgreSqlDatabaseCreateDispatchOutcome
{
    Created,
    /// <summary>PostgreSQL definitively rejected the Create with a duplicate database.</summary>
    Conflict,
    /// <summary>PostgreSQL definitively rejected the dispatched Create.</summary>
    ServerRejected,
    Uncertain
}

/// <summary>
/// The external effect boundary for exactly one PostgreSQL database Create. Absence reads and the command are separate
/// operations so the journal barrier can be committed between them. Implementations never read the
/// current alias and never leak driver or configuration text.
/// </summary>
public interface IPostgreSqlDatabaseCreateTarget
{
    ValueTask<PostgreSqlDatabaseAbsenceOutcome> CheckDatabaseAbsentAsync(PostgreSqlDatabaseCreateInput input,
        CancellationToken cancellationToken);
    ValueTask<PostgreSqlDatabaseCreateDispatchOutcome> DispatchCreateAsync(PostgreSqlDatabaseCreateInput input,
        CancellationToken cancellationToken);
}
