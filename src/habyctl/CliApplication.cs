using Grpc.Core;
using Haby.Operator.Contracts;
using System.Text.Json;

namespace Haby.Cli;

internal static class CliApplication
{
    // The server's finite gRPC message limit includes protobuf framing.
    public const int MaxEnvelopeBytes = 1024 * 1024 - 1024;
    public const int MaxPreviewContextBytes = 16 * 1024;

    internal static async Task<PreviewDatabaseRequest> ReadPreviewContextAsync(string path,
        RevisionReference reference)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaxPreviewContextBytes) throw new InvalidDataException("Preview context exceeds the client size limit.");
        var bytes = new byte[MaxPreviewContextBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count));
            if (read == 0) break;
            count += read;
        }
        if (count > MaxPreviewContextBytes) throw new InvalidDataException("Preview context exceeds the client size limit.");
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
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

    internal static async Task<byte[]> ReadEnvelopeAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaxEnvelopeBytes) throw new InvalidDataException("Manifest exceeds the client size limit.");
        var bytes = new byte[MaxEnvelopeBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count));
            if (read == 0) break;
            count += read;
        }
        if (count > MaxEnvelopeBytes) throw new InvalidDataException("Manifest exceeds the client size limit.");
        return bytes.AsSpan(0, count).ToArray();
    }

    internal static Uri? ParseEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var address) &&
        (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp) &&
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

    internal static int Usage()
    {
        Console.Error.WriteLine("Set HABY_OPERATOR_URL to a loopback HTTP or HTTPS endpoint.");
        Console.Error.WriteLine("Usage: habyctl manifest validate|import <file>");
        Console.Error.WriteLine("       habyctl manifest revision get <application-record-id> <number>");
        Console.Error.WriteLine("       habyctl manifest revision preview <application-record-id> <number> <preview-context.json>");
        return 2;
    }

    internal static int Print(ValidateReply reply)
    {
        switch (reply.OutcomeCase)
        {
            case ValidateReply.OutcomeOneofCase.Valid:
                Console.WriteLine($"Valid {reply.Valid.ApplicationId} {reply.Valid.ApplicationVersion}");
                Print(reply.Valid.Warnings);
                return 0;
            case ValidateReply.OutcomeOneofCase.InvalidRequest:
                Console.WriteLine("InvalidRequest"); Print(reply.InvalidRequest.Report); return 1;
            case ValidateReply.OutcomeOneofCase.InvalidManifest:
                Console.WriteLine("InvalidManifest"); Print(reply.InvalidManifest.Report); return 1;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal static int Print(ImportReply reply)
    {
        switch (reply.OutcomeCase)
        {
            case ImportReply.OutcomeOneofCase.Created:
                Console.WriteLine("Created"); Print(reply.Created.Revision); return 0;
            case ImportReply.OutcomeOneofCase.ExistingEquivalent:
                Console.WriteLine("ExistingEquivalent"); Print(reply.ExistingEquivalent.Revision); return 0;
            case ImportReply.OutcomeOneofCase.InvalidRequest:
                Console.WriteLine("InvalidRequest"); Print(reply.InvalidRequest.Report); return 1;
            case ImportReply.OutcomeOneofCase.InvalidManifest:
                Console.WriteLine("InvalidManifest"); Print(reply.InvalidManifest.Report); return 1;
            case ImportReply.OutcomeOneofCase.VersionContentConflict:
                var conflict = reply.VersionContentConflict;
                Console.WriteLine($"VersionContentConflict {conflict.ApplicationId} {conflict.ApplicationVersion}");
                Console.WriteLine($"Existing revision: {conflict.ExistingRevision.ApplicationRecordId} {conflict.ExistingRevision.RevisionNumber}");
                foreach (var path in conflict.ChangedPaths) Console.WriteLine($"Changed: {path}");
                if (conflict.ChangedPathsTruncated) Console.WriteLine("Changed paths truncated");
                return 1;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal static int Print(GetRevisionReply reply)
    {
        switch (reply.OutcomeCase)
        {
            case GetRevisionReply.OutcomeOneofCase.Found:
                Console.WriteLine("Found"); Print(reply.Found); return 0;
            case GetRevisionReply.OutcomeOneofCase.NotFound:
                Console.WriteLine("NotFound"); return 1;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    internal static int Print(PreviewDatabaseReply reply)
    {
        switch (reply.OutcomeCase)
        {
            case PreviewDatabaseReply.OutcomeOneofCase.WouldCreate:
                var value = reply.WouldCreate;
                Console.WriteLine("Hypothetical preview only; cannot be applied.");
                Console.WriteLine($"Would create one database: {value.DatabaseName}");
                Console.WriteLine($"Environment: {value.EnvironmentId}; resource: {value.ResourceId}");
                Console.WriteLine($"Provider: {value.ProviderAlias} -> {value.InstanceId} revision {value.ConfigurationRevision}; implementation: {value.ImplementationId}");
                return 0;
            case PreviewDatabaseReply.OutcomeOneofCase.RevisionNotFound: Console.WriteLine("RevisionNotFound"); return 1;
            case PreviewDatabaseReply.OutcomeOneofCase.InvalidContext: Console.WriteLine("InvalidPreviewContext"); return 1;
            case PreviewDatabaseReply.OutcomeOneofCase.AliasNotResolved:
                Console.WriteLine($"ProviderAliasNotResolved: {reply.AliasNotResolved.Alias}"); return 1;
            case PreviewDatabaseReply.OutcomeOneofCase.CapabilityMismatch:
                Console.WriteLine($"ProviderCapabilityMismatch: {reply.CapabilityMismatch.Alias}"); return 1;
            case PreviewDatabaseReply.OutcomeOneofCase.UnsupportedShape: Console.WriteLine("UnsupportedManifestShape"); return 1;
            default: throw new InvalidDataException("Unknown server outcome.");
        }
    }

    private static void Print(RevisionSummary revision)
    {
        Console.WriteLine($"Application: {revision.ApplicationId} {revision.ApplicationVersion}");
        Console.WriteLine($"Revision: {revision.Reference.ApplicationRecordId} {revision.Reference.RevisionNumber}");
        Console.WriteLine($"Revision ID: {revision.RevisionId}");
        Console.WriteLine($"Input API: {revision.InputApiVersion}; model: {revision.NormalizedModelVersion}");
        Console.WriteLine($"Technical actor: {revision.TechnicalActorId}; imported UTC: {revision.ImportedAtUtc}");
        Print(revision.HistoricalWarnings);
    }

    private static void Print(DiagnosticReport report)
    {
        foreach (var diagnostic in report.Diagnostics)
            Console.WriteLine($"{diagnostic.Severity} {diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}");
        if (report.Truncated) Console.WriteLine("Diagnostics truncated");
    }
}
