using Grpc.Core;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal static class CliApplication
{
    // The server's finite gRPC message limit includes protobuf framing.
    public const int MaxEnvelopeBytes = 1024 * 1024 - 1024;

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
