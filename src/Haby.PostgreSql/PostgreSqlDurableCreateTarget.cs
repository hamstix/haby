using System.IO;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Haby.PostgreSql;

/// <summary>
/// The external effect boundary for one durable Create. Every call re-resolves exactly the inputs
/// pinned by the accepted generation — Provider instance, configuration revision and implementation
/// — and never follows the current alias. Secret material is held only for one dedicated,
/// non-pooled connection and is cleared before returning.
/// </summary>
public sealed class PostgreSqlDurableCreateTarget(IProviderConfigurationStore revisions, ISecretStore secrets,
    ICheckpointProviderCapability capability, IDurableCreateDispatchFaultHooks hooks) : IDurableCreateTarget
{
    public async ValueTask<DurableCreateAbsenceOutcome> CheckDatabaseAbsentAsync(DurableCreateTargetInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var resolution = await ResolveAsync(input, cancellationToken);
        try
        {
            if (resolution.Outcome is not DurableCreateAbsenceOutcome.Absent) return resolution.Outcome;
            return await PostgreSqlDatabaseGateway.CheckAbsentAsync(resolution.Builder!, input.DatabaseName,
                cancellationToken) switch
            {
                DatabaseAbsenceOutcome.Absent => DurableCreateAbsenceOutcome.Absent,
                DatabaseAbsenceOutcome.Present => DurableCreateAbsenceOutcome.Present,
                _ => DurableCreateAbsenceOutcome.Connectivity
            };
        }
        finally { Clear(resolution.Builder); }
    }

    public async ValueTask<DurableCreateDispatchOutcome> DispatchCreateAsync(DurableCreateTargetInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var resolution = await ResolveAsync(input, cancellationToken);
        if (resolution.Outcome is not DurableCreateAbsenceOutcome.Absent)
        {
            // The barrier was already committed. Anything that cannot re-establish the exact pinned
            // target now is an outcome the journal must treat as uncertain, never as "not dispatched".
            return DurableCreateDispatchOutcome.Uncertain;
        }

        try
        {
            return await PostgreSqlDatabaseGateway.CreateDatabaseAsync(resolution.Builder!, input.DatabaseName,
                    () => hooks.DuringCreateCommandAsync(cancellationToken), cancellationToken) switch
            {
                DatabaseCreateOutcome.Created => DurableCreateDispatchOutcome.Created,
                DatabaseCreateOutcome.Conflict => DurableCreateDispatchOutcome.Conflict,
                DatabaseCreateOutcome.ServerRejected => DurableCreateDispatchOutcome.ServerRejected,
                _ => DurableCreateDispatchOutcome.Uncertain
            };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Past the dispatch marker an unexpected failure can never be claimed as "not sent".
            return DurableCreateDispatchOutcome.Uncertain;
        }
        finally { Clear(resolution.Builder); }
    }

    private async ValueTask<Resolved> ResolveAsync(DurableCreateTargetInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(input.DatabaseName))
            return new Resolved(DurableCreateAbsenceOutcome.DatabaseNameInvalid, null);
        if (!capability.Supports(new ProviderResolution(input.ProviderInstance.ToString(),
                input.ConfigurationRevision.Value, input.ImplementationId), PostgreSqlDatabaseContract.Id))
            return new Resolved(DurableCreateAbsenceOutcome.ProviderInputUnavailable, null);

        PostgreSqlProviderConfiguration? configuration;
        try
        {
            configuration = (await revisions.FindRevisionAsync<PostgreSqlProviderConfiguration>(input.Environment,
                input.ProviderInstance, input.ConfigurationRevision, cancellationToken))?.Configuration;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) { return new Resolved(DurableCreateAbsenceOutcome.ProviderInputUnavailable, null); }
        if (configuration is null ||
            !string.Equals(configuration.ImplementationId, input.ImplementationId, StringComparison.Ordinal))
            return new Resolved(DurableCreateAbsenceOutcome.ProviderInputUnavailable, null);

        SecretMaterial? material;
        try
        {
            material = await secrets.ResolveAsync(input.Environment, configuration.AdministratorPassword,
                cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) { return new Resolved(DurableCreateAbsenceOutcome.SecretUnavailable, null); }
        using var ownedMaterial = material;
        if (material is null) return new Resolved(DurableCreateAbsenceOutcome.SecretUnavailable, null);

        byte[]? bytes = null;
        try
        {
            bytes = material.CopyBytes();
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = configuration.Host,
                Port = configuration.Port,
                Database = configuration.MaintenanceDatabase,
                Username = configuration.AdministratorUserName,
                Pooling = false,
                Timeout = 5,
                CommandTimeout = 15,
                // Npgsql requires a managed string; clearing byte buffers and disabling pooling limits its lifetime.
                Password = Encoding.UTF8.GetString(bytes)
            };
            return new Resolved(DurableCreateAbsenceOutcome.Absent, builder);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new Resolved(DurableCreateAbsenceOutcome.ProviderInputUnavailable, null);
        }
        finally { if (bytes is not null) Array.Clear(bytes); }
    }

    private static void Clear(NpgsqlConnectionStringBuilder? builder)
    {
        if (builder is not null) builder.Password = null;
    }

    private readonly record struct Resolved(DurableCreateAbsenceOutcome Outcome,
        NpgsqlConnectionStringBuilder? Builder);
}

public static class PostgreSqlDurableCreateTargetServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDurableCreateTarget(this IServiceCollection services)
    {
        services.TryAddSingleton<IDurableCreateTarget, PostgreSqlDurableCreateTarget>();
        return services;
    }
}
