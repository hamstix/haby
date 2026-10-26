using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Haby.PostgreSql;

/// <summary>Read-only PostgreSQL connection and CREATE DATABASE eligibility check.</summary>
public sealed class PostgreSqlProviderRevisionPreflight(IProviderConfigurationStore revisions, ISecretStore secrets)
    : IProviderRevisionPreflight
{
    public async ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment,
        ProviderAliasBinding target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        PostgreSqlProviderConfiguration? configuration;
        try
        {
            configuration = (await revisions.FindRevisionAsync<PostgreSqlProviderConfiguration>(environment,
                target.InstanceId, target.RevisionNumber, deadline.Token))?.Configuration;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested)
        { return new(ProviderPreflightOutcome.DeadlineExceeded); }
        catch (Exception) { return new(ProviderPreflightOutcome.ConfigurationUnavailable); }
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested) return new(ProviderPreflightOutcome.DeadlineExceeded);
        if (configuration is null) return new(ProviderPreflightOutcome.RevisionUnavailable);

        SecretMaterial? material;
        try { material = await secrets.ResolveAsync(environment, configuration.AdministratorPassword, deadline.Token); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested)
        { return new(ProviderPreflightOutcome.DeadlineExceeded); }
        catch (Exception) { return new(ProviderPreflightOutcome.SecretUnavailable); }
        using var ownedMaterial = material;
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested) return new(ProviderPreflightOutcome.DeadlineExceeded);
        if (material is null) return new(ProviderPreflightOutcome.SecretUnavailable);
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
                CommandTimeout = 5,
                // Npgsql requires a managed string; clearing byte buffers and disabling pooling limits its lifetime.
                Password = Encoding.UTF8.GetString(bytes)
            };
            try
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(deadline.Token);
                await using var command = new NpgsqlCommand(
                    "SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user", connection)
                { CommandTimeout = 5 };
                var eligible = await command.ExecuteScalarAsync(deadline.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) return new(ProviderPreflightOutcome.DeadlineExceeded);
                return new(eligible is true ? ProviderPreflightOutcome.Ready :
                    ProviderPreflightOutcome.InsufficientPrivilege);
            }
            finally { builder.Password = null; }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested)
        { return new(ProviderPreflightOutcome.DeadlineExceeded); }
        catch (PostgresException error) when (error.SqlState.StartsWith("28", StringComparison.Ordinal))
        { return new(ProviderPreflightOutcome.AuthenticationFailed); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        { return new(ProviderPreflightOutcome.InsufficientPrivilege); }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { return new(ProviderPreflightOutcome.ConfigurationUnavailable); }
        catch (Exception) { return new(ProviderPreflightOutcome.ServerUnavailable); }
        finally { if (bytes is not null) Array.Clear(bytes); }
    }
}

public static class PostgreSqlProviderRevisionPreflightServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlProviderRevisionPreflight(this IServiceCollection services)
    {
        services.AddSingleton<IProviderRevisionPreflight, PostgreSqlProviderRevisionPreflight>();
        return services;
    }
}
