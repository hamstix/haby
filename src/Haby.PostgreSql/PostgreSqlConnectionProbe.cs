using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Npgsql;

namespace Haby.PostgreSql;

/// <summary>Opens and disposes a login only. Never queries management privileges or writes a receipt.</summary>
public sealed class PostgreSqlConnectionProbe(PostgreSqlProviderConfigurationCodec codec, ISecretStore secrets,
    ISharedCredentialSystemAccess? systemAccess = null, PostgreSqlProviderTransport? transport = null) : ProviderConnectionProbe<PostgreSqlProviderConfiguration>(codec)
{
    public override IReadOnlyList<ProviderAdapterKey> Keys { get; } =
        [new(PostgreSqlProviderConfigurationCodec.Kind, PostgreSqlProviderConfigurationCodec.Shape, PostgreSqlDatabaseProvisioner.ImplementationId)];
    public override async ValueTask<ProviderConnectionResult> ProbeTypedAsync(ProviderConnectionContext context,
        PostgreSqlProviderConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SecretMaterial? material;
        try
        {
            material = configuration.SystemAccessCredential is { } system
                ? systemAccess is null ? null : await systemAccess.ResolveAsync(new(context.Environment, context.Provider, context.Revision,
                    PostgreSqlProviderConfigurationCodec.SystemAccessCredentialSlot, SharedCredentialUse.SystemAccess, system), 1_048_576, token)
                : await secrets.ResolveBoundedAsync(new(new SecretOwner.Environment(context.Environment), SecretPurpose.ProviderAdministrator),
                    configuration.AdministratorPassword, 1_048_576, token);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception) { return new(ProviderConnectionOutcome.SecretUnavailable); }
        using var owned = material;
        if (material is null) return new(ProviderConnectionOutcome.SecretUnavailable);
        var bytes = material.CopyBytes();
        var builder = new NpgsqlConnectionStringBuilder();
        try
        {
            builder.Host = configuration.Host; builder.Port = configuration.Port;
            builder.Database = configuration.MaintenanceDatabase; builder.Username = configuration.AdministratorUserName;
            builder.Pooling = false; builder.Timeout = 5; builder.CommandTimeout = 5;
            builder.Password = Encoding.UTF8.GetString(bytes);
            (transport ?? new()).Apply(builder, configuration);
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            token.ThrowIfCancellationRequested();
            return new(ProviderConnectionOutcome.Connected) { ServerCertificateValidationDisabled = configuration.TransportMode == PostgreSqlTransportMode.Disable };
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (PostgresException error) when (error.SqlState.StartsWith("28", StringComparison.Ordinal))
        { return new(ProviderConnectionOutcome.AuthenticationFailed); }
        catch (Exception error) when (Has<TimeoutException>(error)) { return new(ProviderConnectionOutcome.DeadlineExceeded); }
        catch (Exception error) when (Has<AuthenticationException>(error)) { return new(ProviderConnectionOutcome.TlsFailed); }
        catch (Exception error) when (Has<SocketException>(error)) { return new(ProviderConnectionOutcome.ConnectionUnavailable); }
        catch (Exception error) when (error is ArgumentException or FormatException) { return new(ProviderConnectionOutcome.ConfigurationUnavailable); }
        catch (Exception) { return new(ProviderConnectionOutcome.ConnectionUnavailable); }
        finally { builder.Password = null; Array.Clear(bytes); }
    }
    private static bool Has<T>(Exception error) where T : Exception
    {
        for (var depth = 0; depth < 16; depth++, error = error.InnerException!)
        { if (error is T) return true; if (error.InnerException is null) return false; }
        return false;
    }
}
