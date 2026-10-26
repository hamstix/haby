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
        PostgreSqlProviderConfiguration? configuration;
        try
        {
            configuration = (await revisions.FindRevisionAsync<PostgreSqlProviderConfiguration>(environment,
                target.InstanceId, target.RevisionNumber, cancellationToken))?.Configuration;
        }
        catch (InvalidDataException) { return new(ProviderPreflightOutcome.ConfigurationUnavailable); }
        catch (InvalidOperationException) { return new(ProviderPreflightOutcome.ConfigurationUnavailable); }
        catch (NpgsqlException) { return new(ProviderPreflightOutcome.ConfigurationUnavailable); }
        if (configuration is null) return new(ProviderPreflightOutcome.RevisionUnavailable);

        SecretMaterial? material;
        try { material = await secrets.ResolveAsync(environment, configuration.AdministratorPassword, cancellationToken); }
        catch (InvalidDataException) { return new(ProviderPreflightOutcome.SecretUnavailable); }
        catch (NpgsqlException) { return new(ProviderPreflightOutcome.SecretUnavailable); }
        if (material is null) return new(ProviderPreflightOutcome.SecretUnavailable);

        using var ownedMaterial = material;
        var bytes = material.CopyBytes();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = configuration.Host,
                Port = configuration.Port,
                Database = configuration.MaintenanceDatabase,
                Username = configuration.AdministratorUserName,
                Password = Encoding.UTF8.GetString(bytes),
                Pooling = false,
                Timeout = 5,
                CommandTimeout = 5
            };
            try
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(deadline.Token);
                await using var command = new NpgsqlCommand(
                    "SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user", connection)
                { CommandTimeout = 5 };
                var eligible = await command.ExecuteScalarAsync(deadline.Token);
                return new(eligible is true ? ProviderPreflightOutcome.Ready :
                    ProviderPreflightOutcome.InsufficientPrivilege);
            }
            finally { builder.Password = null; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PostgresException error) when (error.SqlState.StartsWith("28", StringComparison.Ordinal))
        { return new(ProviderPreflightOutcome.AuthenticationFailed); }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or OperationCanceledException or
            System.IO.IOException)
        { return new(ProviderPreflightOutcome.ServerUnavailable); }
        finally { Array.Clear(bytes); }
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
