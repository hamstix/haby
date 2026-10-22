using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Operator.Contracts;

namespace Haby.Cli;

public static class Program
{
    // The server's finite gRPC message limit includes protobuf framing.
    public const int MaxEnvelopeBytes = 1024 * 1024 - 1024;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var address = ParseEndpoint(Environment.GetEnvironmentVariable("HABY_OPERATOR_URL"));
            if (args.Length < 2 || args[0] != "manifest" || address is null)
                return Usage();
            using var channel = GrpcChannel.ForAddress(address);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            if (args.Length == 3 && args[1] is "validate" or "import")
            {
                var bytes = await ReadEnvelopeAsync(args[2]);
                var request = new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(bytes) };
                if (args[1] == "validate") return Print(await client.ValidateAsync(request));
                return Print(await client.ImportAsync(request));
            }
            if (args.Length == 5 && args[1] == "revision" && args[2] == "get" &&
                long.TryParse(args[4], out var number) && number > 0)
                return Print(await client.GetRevisionAsync(new GetRevisionRequest
                {
                    Reference = new RevisionReference { ApplicationRecordId = args[3], RevisionNumber = number }
                }));
            return Usage();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or RpcException or
            HttpRequestException or InvalidDataException)
        {
            // Exception messages can contain paths, endpoint details, or TLS failure internals.
            Console.Error.WriteLine(SafeError(error));
            return 2;
        }
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
        Uri.TryCreate(value, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps &&
        IPAddressIsLoopback(address.Host) ? address : null;

    internal static string SafeError(Exception error) => error is RpcException rpc
        ? $"RPC failed: {rpc.StatusCode}"
        : "Operation failed; check local file, TLS trust and server availability.";

    private static bool IPAddressIsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

    private static int Usage()
    {
        Console.Error.WriteLine("Set HABY_OPERATOR_URL=https://localhost:<port> with a trusted server certificate.");
        Console.Error.WriteLine("Usage: habyctl manifest validate|import <file>");
        Console.Error.WriteLine("       habyctl manifest revision get <application-record-id> <number>");
        return 2;
    }

    private static int Print(ValidateReply reply)
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

    private static int Print(ImportReply reply)
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

    private static int Print(GetRevisionReply reply)
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
