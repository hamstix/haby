using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Configuration.Contracts;

namespace Haby.Configuration.Client;

/// <summary>One generated delivery RPC, no retries/cache or operator dependencies.</summary>
public static class ExactConfigurationClient
{
    public static async Task<DeliverConfigurationReply> GetAsync(Uri endpoint, string revision, string tokenFile, CancellationToken token)
    {
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { MaxReceiveMessageSize = 8 * 1024 * 1024 });
        var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var credential = await ReadCredentialAsync(tokenFile, token);
        var reply = await client.DeliverAsync(new() { RevisionId = revision }, new Metadata { { "authorization", "Bearer " + credential } }, cancellationToken: token);
        if (reply.RevisionId != revision || reply.Json.Length > 4 * 1024 * 1024) throw new InvalidDataException();
        return reply;
    }

    /// <summary>Read the retained credential anew for each application start, with bounded owned storage.</summary>
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
