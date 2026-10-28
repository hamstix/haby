using Grpc.Core;
using Haby.Operator.Contracts;
using System.Text.Json;

namespace Haby.Cli;

internal sealed partial class CliApplication
{
    // The server's finite gRPC message limit includes protobuf framing.
    public const int MaxEnvelopeBytes = 1024 * 1024 - 1024;
    public const int MaxPreviewContextBytes = 16 * 1024;

    internal static async Task<PreviewDatabaseRequest> ReadPreviewContextAsync(string path,
        RevisionReference reference, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaxPreviewContextBytes) throw new InvalidDataException("Preview context exceeds the client size limit.");
        var bytes = new byte[MaxPreviewContextBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxPreviewContextBytes) throw new InvalidDataException("Preview context exceeds the client size limit.");
        try
        {
            var context = bytes.AsMemory(0, count);
            if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                context = context[3..];
            using var document = JsonDocument.Parse(context);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !ExactProperties(root, "preview", "environmentId", "providers") ||
                !root.GetProperty("preview").GetBoolean()) throw new InvalidDataException("Invalid preview context.");
            var request = new PreviewDatabaseRequest
            {
                Reference = reference,
                EnvironmentId = root.GetProperty("environmentId").GetString() ?? ""
            };
            var providers = root.GetProperty("providers");
            if (providers.ValueKind != JsonValueKind.Array || providers.GetArrayLength() is < 1 or > 16)
                throw new InvalidDataException("Invalid preview context.");
            foreach (var item in providers.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !ExactProperties(item, "alias", "instanceId", "configurationRevision", "implementationId"))
                    throw new InvalidDataException("Invalid preview context.");
                request.Providers.Add(new PreviewProviderSelection
                {
                    Alias = item.GetProperty("alias").GetString() ?? "",
                    InstanceId = item.GetProperty("instanceId").GetString() ?? "",
                    ConfigurationRevision = item.GetProperty("configurationRevision").GetInt64(),
                    ImplementationId = item.GetProperty("implementationId").GetString() ?? ""
                });
            }
            return request;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or
            FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("Invalid preview context.");
        }
    }

    private static bool ExactProperties(JsonElement element, params string[] expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
        return seen.Count == expected.Length;
    }

    internal static async Task<byte[]> ReadEnvelopeAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaxEnvelopeBytes) throw new InvalidDataException("Manifest exceeds the client size limit.");
        var bytes = new byte[MaxEnvelopeBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxEnvelopeBytes) throw new InvalidDataException("Manifest exceeds the client size limit.");
        return bytes.AsSpan(0, count).ToArray();
    }

    internal static Uri? ParseEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var address) &&
        (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp) &&
        address.UserInfo.Length == 0 && address.Query.Length == 0 && address.Fragment.Length == 0 &&
        address.AbsolutePath == "/" &&
        IPAddressIsLoopback(address.Host) ? address : null;

    internal static bool SkipTlsCertificateValidation(string? setting) =>
        string.Equals(setting, "true", StringComparison.OrdinalIgnoreCase);

    internal static HttpClientHandler CreateHttpHandler(bool skipTlsValidation) => new()
    {
        ServerCertificateCustomValidationCallback = skipTlsValidation
            ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            : null
    };

    internal static string SafeError(Exception error) => error is RpcException rpc
        ? $"RPC failed: {rpc.StatusCode}"
        : "Operation failed; check local file, TLS trust and server availability.";

    private static bool IPAddressIsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

}
