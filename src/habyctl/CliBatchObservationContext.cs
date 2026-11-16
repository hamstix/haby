using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record BatchFileMember(string DeploymentId, long Generation);
internal sealed record BatchFileSettings(int? Concurrency = null, int? MaximumReads = null, int? RequestMilliseconds = null, int? StartsPerSecond = null);
internal sealed record BatchFileMemberSettings(int? MaximumCalls = null, int? MaximumRounds = null, int? ReadTimeoutMilliseconds = null,
    int? PollMilliseconds = null, int? RequestMilliseconds = null);
internal sealed record BatchFile(BatchFileMember[] Members, BatchFileSettings? Settings = null, BatchFileMemberSettings? MemberSettings = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(BatchFile))]
internal partial class BatchFileJsonContext : JsonSerializerContext;

internal static class BatchFileReader
{
    internal static async Task<GenerationBatchWaitRequest> ReadAsync(string path, string key, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var bytes = new byte[BatchObservationWire.RequestBytes + 1]; var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > BatchObservationWire.RequestBytes) throw new InvalidDataException("Batch file exceeds 128KiB.");
        var input = bytes.AsMemory(0, count);
        using (var document = JsonDocument.Parse(input)) Unique(document.RootElement);
        var file = JsonSerializer.Deserialize(input.Span, BatchFileJsonContext.Default.BatchFile)
            ?? throw new InvalidDataException("Batch file required.");
        if (file.Members is not { Length: > 0 and <= 200 } || file.Members.Any(x => x is null)) throw new InvalidDataException("Invalid batch members.");
        var wire = new GenerationBatchWaitRequest { RequestId = key };
        foreach (var member in file.Members)
        {
            var deployment = BatchObservationKeys.CanonicalDeployment(member.DeploymentId);
            wire.Members.Add(new GenerationBatchMember { DeploymentId = deployment, Generation = member.Generation, RequestId = BatchObservationKeys.Member(key, deployment, member.Generation) });
        }
        if (file.Settings is { } s)
        {
            var settings = new GenerationBatchSettings();
            if (s.Concurrency is { } c) { if (c is < 1 or > 8) throw Invalid(); settings.Concurrency = c; }
            if (s.MaximumReads is { } r) { if (r is < 1 or > 1024) throw Invalid(); settings.MaximumReads = r; }
            if (s.RequestMilliseconds is { } t) { if (t is < 1 or > 300000) throw Invalid(); settings.RequestMilliseconds = t; }
            if (s.StartsPerSecond is { } p) { if (p is < 1 or > 100) throw Invalid(); settings.StartsPerSecond = p; }
            wire.Settings = settings;
        }
        if (file.MemberSettings is { } m)
        {
            var settings = new GenerationObservationSettings();
            if (m.MaximumCalls is { } c) { if (c is < 1 or > 512) throw Invalid(); settings.MaximumCalls = c; }
            if (m.MaximumRounds is { } r) { if (r is < 1 or > 512) throw Invalid(); settings.MaximumRounds = r; }
            if (m.ReadTimeoutMilliseconds is { } t) { if (t is < 100 or > 10000) throw Invalid(); settings.ReadTimeoutMilliseconds = t; }
            if (m.PollMilliseconds is { } p) { if (p is < 100 or > 10000) throw Invalid(); settings.PollMilliseconds = p; }
            if (m.RequestMilliseconds is { } b) { if (b is < 1 or > 300000) throw Invalid(); settings.RequestMilliseconds = b; }
            wire.MemberSettings = settings;
        }
        BatchObservationWire.Validate(wire);
        return wire;
        static InvalidDataException Invalid() => new("Invalid batch settings.");
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
}

internal sealed partial class CliCommandContext
{
    internal async Task<int> ObserveBatchAsync(string path, string? suppliedRequest, CancellationToken token)
    {
        var id = Guid.NewGuid();
        if (suppliedRequest is not null && (!Guid.TryParse(suppliedRequest, out id) || id == Guid.Empty))
        { error.WriteLine("Invalid batch request UUID."); return CliApplication.UsageError; }
        GenerationBatchWaitRequest request;
        try { request = await BatchFileReader.ReadAsync(path, id.ToString("D"), token); }
        catch (OperationCanceledException) { return CliApplication.TransportError; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        { error.WriteLine("Invalid batch JSON file. Use unique exact deploymentId/generation members and bounded settings."); return CliApplication.UsageError; }
        var received = false;
        var exit = await ExecuteAsync(async (client, ct) =>
        {
            var reply = await client.WaitGenerationBatchAsync(request, ct);
            int result;
            try { result = BatchObservationPresentation.Exit(request, reply); }
            catch (InvalidDataException) { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "Invalid batch response.")); }
            if (jsonOutput()) jsonPresenter.PrintBatch(reply); else presenter.PrintBatch(reply);
            received = true; return result;
        }, token);
        if (!received)
        {
            error.WriteLine($"Batch response unavailable. RequestId: {request.RequestId}. No completion is confirmed. Repeat identical inputs with --request-id {request.RequestId} to inspect/replay; interrupted members are not guaranteed to resume.");
            foreach (var member in request.Members) error.WriteLine($"{member.DeploymentId}/{member.Generation}: {member.RequestId}");
        }
        return exit;
    }
}
