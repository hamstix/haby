using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Haby.Configuration.Client;

public sealed record RuntimeConnectionIdentity(string User, string Database);

/// <summary>One RPC, no retries/cache. Read the retained credential file anew for each application start.</summary>
public static class RuntimeConfigurationClient
{
    public static async Task<RuntimeConnectionIdentity> ConnectAsync(Uri endpoint, string revision, string tokenFile, CancellationToken token, RuntimeSettingExpectations? expectations = null)
    {
        var reply = await ExactConfigurationClient.GetAsync(endpoint, revision, tokenFile, token);
        var bytes = reply.Json.ToByteArray();
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            var configuration = new ConfigurationBuilder().AddJsonStream(source).Build();
            using var configurationLifetime = configuration as IDisposable;
            var connectionString = configuration["PostgreSQL:DefaultConnection:ConnectionString"] ?? throw new InvalidDataException();
            var received = new NpgsqlConnectionStringBuilder(connectionString);
            expectations?.AssertMatches(received);
            received.Pooling = false;
            await using var connection = new NpgsqlConnection(received.ConnectionString);
            await connection.OpenAsync(token);
            await using (var operation = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS public.haby_runtime_probe (id integer PRIMARY KEY); INSERT INTO public.haby_runtime_probe VALUES (1) ON CONFLICT DO NOTHING", connection))
                await operation.ExecuteNonQueryAsync(token);
            await using var command = new NpgsqlCommand("SELECT current_user, current_database(), (SELECT count(*) FROM public.haby_runtime_probe)", connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.GetInt64(2) != 1) throw new InvalidDataException();
            return new(reader.GetString(0), reader.GetString(1));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static Task<string> ReadCredentialAsync(string path, CancellationToken token) => ExactConfigurationClient.ReadCredentialAsync(path, token);
}
