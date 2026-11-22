using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Grpc.Core;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliImportInput(string Format, JsonElement Input);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(CliImportInput))]
internal partial class CliImportInputJsonContext : JsonSerializerContext;

internal sealed partial class CliCommandContext
{
    private int PrintResourceImport(string command, ResourceImportRequestReply reply) =>
        jsonOutput() ? jsonPresenter.PrintResourceImport(command, reply) : presenter.PrintResourceImport(command, reply);
    private int PrintImportTicket(string command, Guid request, bool sent, int exit) =>
        jsonOutput() ? jsonPresenter.PrintImportTicket(command, request, sent, exit) : presenter.PrintImportTicket(command, request, sent, exit);

    internal async Task<int> GetResourceImportAsync(string supplied, CancellationToken token)
    {
        if (!Guid.TryParse(supplied, out var id) || id == Guid.Empty)
        { error.WriteLine("Request ID must be a nonempty UUID."); return CliApplication.UsageError; }
        var received = false;
        var exit = await ExecuteAsync(async (client, ct) =>
        {
            var reply = await client.GetResourceImportRequestAsync(new() { RequestId = id.ToString() }, ct);
            if (reply.RequestId != id.ToString()) throw new InvalidDataException("Import request identity mismatch.");
            var result = PrintResourceImport("changeset import get", reply); received = true; return result;
        }, token);
        return received ? exit : PrintImportTicket("changeset import get", id, true, exit);
    }

    internal async Task<int> CreateResourceImportAsync(string application, long revision, string inputPath,
        string? environment, string? environmentRecord, string? suppliedId, bool stdin, string? previous,
        string? reason, bool absence, string? deployment, long? generation, CancellationToken token)
    {
        var id = Guid.NewGuid();
        if (suppliedId is not null && (!Guid.TryParse(suppliedId, out id) || id == Guid.Empty))
        { error.WriteLine("Request ID must be a nonempty UUID."); return CliApplication.UsageError; }
        var received = false; var sent = false;
        var exit = await ExecuteResolvedAsync(async (client, selected, ct) =>
        {
            var record = environmentRecord;
            if (record is null)
            {
                var name = environment ?? selected;
                if (string.IsNullOrEmpty(name)) throw new InvalidDataException("Select an Environment using --environment, --environment-record-id or a context.");
                var found = await client.GetEnvironmentAsync(new() { Name = name }, ct);
                if (found.Status != EnvironmentStatus.Found || found.Environment is null)
                {
                    var result = PrintResourceImport("changeset import create", new() { RequestId = id.ToString(), Status = ResourceImportRequestStatus.EnvironmentNotFound });
                    received = true; return result;
                }
                record = found.Environment.RecordId;
            }
            var input = await ReadImportInput(inputPath, ct);
            var request = new CreateResourceImportChangeSetRequest
            {
                RequestId = id.ToString(),
                EnvironmentRecordId = record,
                Reference = new() { ApplicationRecordId = application, RevisionNumber = revision },
                InputFormat = input.Format,
                InputJson = ByteString.CopyFromUtf8(input.Input.GetRawText()),
                Correction = CreateImportCorrection(previous, reason, absence, deployment, generation)
            };
            if (!stdin && secretTerminal.IsInputRedirected) throw new InvalidDataException("Redirected password input requires --password-stdin.");
            var bytes = await SecretInput.ReadAsync(secretTerminal, stdin, error, ct);
            try
            {
                if (bytes.AsSpan().Contains((byte)0)) throw new InvalidDataException("Password input must be NUL-free strict UTF-8.");
                try { _ = new UTF8Encoding(false, true).GetCharCount(bytes); }
                catch (DecoderFallbackException) { throw new InvalidDataException("Password input must be NUL-free strict UTF-8."); }
                request.CredentialMaterial = ByteString.CopyFrom(bytes);
                sent = true;
                var reply = await client.CreateResourceImportChangeSetAsync(request, ct);
                if (reply.RequestId != id.ToString()) throw new InvalidDataException("Import request identity mismatch.");
                var result = PrintResourceImport("changeset import create", reply); received = true; return result;
            }
            finally { Array.Clear(bytes); request.CredentialMaterial = ByteString.Empty; }
        }, requireContext: false, token);
        return received ? exit : PrintImportTicket("changeset import create", id, sent, exit);
    }

    private static ResourceImportCorrection? CreateImportCorrection(string? previous, string? reason, bool absence, string? deployment, long? generation)
    {
        if (previous is null) return null;
        if (!Guid.TryParse(previous, out var id) || id == Guid.Empty) throw new InvalidDataException("Invalid previous request UUID.");
        var correction = new ResourceImportCorrection
        {
            PreviousRequestId = id.ToString(),
            Reason = reason switch
            {
                "credential-rejected" => ResourceImportCorrectionReason.CredentialRejected,
                "input-correction" => ResourceImportCorrectionReason.InputCorrection,
                "explicit-retry" => ResourceImportCorrectionReason.ExplicitRetry,
                _ => throw new InvalidDataException("Invalid closed correction reason.")
            }
        };
        if (absence) correction.ExpectedAbsence = true;
        else correction.Committed = new() { DeploymentId = deployment!, Generation = generation!.Value };
        return correction;
    }

    private static async Task<CliImportInput> ReadImportInput(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var bytes = new byte[65537];
        try
        {
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), token);
                if (read == 0) break;
                length += read;
            }
            if (length is < 1 or > 65536) throw new InvalidDataException("Import names document must contain 1..65,536 bytes.");
            CliImportInput? input;
            try { input = JsonSerializer.Deserialize(bytes.AsSpan(0, length), CliImportInputJsonContext.Default.CliImportInput); }
            catch (JsonException) { throw new InvalidDataException("Invalid closed import names document."); }
            if (input is null || string.IsNullOrWhiteSpace(input.Format) || input.Format.Length > 128 || input.Input.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid closed import names document.");
            return input;
        }
        finally { Array.Clear(bytes); }
    }
}
