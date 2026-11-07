using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Operator.Contracts;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Haby.Configuration.Client;

public sealed record RuntimeConnectionIdentity(string User, string Database);

/// <summary>One RPC, no retries/cache. Read the retained credential file anew for each application start.</summary>
public static class RuntimeConfigurationClient
{
    public static async Task<RuntimeConnectionIdentity> ConnectAsync(Uri endpoint, string revision, string tokenFile, CancellationToken token)
    {
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { MaxReceiveMessageSize = 8 * 1024 * 1024 });
        var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var credential = await ReadCredentialAsync(tokenFile, token);
        var reply = await client.DeliverAsync(new() { RevisionId = revision }, new Metadata { { "authorization", "Bearer " + credential } }, cancellationToken: token);
        if (reply.RevisionId != revision || reply.Json.Length > 4 * 1024 * 1024) throw new InvalidDataException();
        var bytes = reply.Json.ToByteArray();
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            var configuration = new ConfigurationBuilder().AddJsonStream(source).Build();
            using var configurationLifetime = configuration as IDisposable;
            var connectionString = configuration["PostgreSQL:DefaultConnection:ConnectionString"] ?? throw new InvalidDataException();
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
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

    public static async Task<string> ReadCredentialAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        const int maximum = 512;
        var bytes = new byte[maximum + 1];
        try
        {
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), token);
                if (read == 0) break;
                count += read;
            }
            if (count > maximum) throw new InvalidDataException();
            var credential = new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimEnd('\r', '\n');
            if (credential.Length != 114 || !credential.StartsWith("haby_delivery_v1.", StringComparison.Ordinal) ||
                !Guid.TryParseExact(credential.AsSpan(17, 32), "N", out _) || credential[49] != '.' ||
                credential.AsSpan(50).IndexOfAnyExcept("0123456789abcdef") >= 0) throw new InvalidDataException();
            return credential;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
