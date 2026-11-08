using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliOperatorGrant(string GrantId, string? DisplayName, string Actor, string CreatedAtUtc,
    string? ExpiresAtUtc, string? RevokedBy, string? RevokedAtUtc);
internal sealed record CliOperatorAccessData(CliOperatorGrant[] Grants, string? NextAfterGrantId, string? Credential);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliEnvelope<CliOperatorAccessData>))]
internal partial class OperatorOutputJsonContext : JsonSerializerContext;

internal sealed partial class CliCommandContext
{
    internal async Task<int> CreateOperatorAccessAsync(string? name, string? expiry, string? file, bool stdout, CancellationToken token)
    {
        if ((file is null) == !stdout || name is not null && (name.Length is < 1 or > 128 || name.Any(char.IsControl))) return InvalidOperatorInput();
        var request = new CreateOperatorAccessRequest();
        if (name is not null) request.DisplayName = name;
        if (expiry is not null)
        {
            if (!(expiry.EndsWith('Z') || expiry.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero || parsed <= DateTimeOffset.UtcNow)
                return InvalidOperatorInput();
            request.ExpiresAtUtc = parsed.ToString("O", CultureInfo.InvariantCulture);
        }
        FileStream? destination = null;
        try
        {
            if (file is not null)
            {
                var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                destination = new FileStream(file, settings);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { error.WriteLine("Cannot create a new credential file. No grant was requested."); return CliApplication.UsageError; }
        try
        {
            return await ExecuteAsync(async (client, ct) =>
            {
                string? id = null;
                try
                {
                    var reply = await client.CreateOperatorAccessAsync(request, ct);
                    if (Guid.TryParseExact(reply.Grant?.GrantId, "D", out var known) && known != Guid.Empty) id = known.ToString();
                    var grant = CheckOperatorGrant(reply.Grant ?? throw new InvalidDataException());
                    if (!reply.HasCredential || !CliOperatorCredential.Valid(reply.Credential) ||
                        !reply.Credential.StartsWith("haby_operator_v1." + Guid.Parse(grant.GrantId).ToString("N") + ".", StringComparison.Ordinal) ||
                        grant.RevokedAtUtc is not null) throw new InvalidDataException();
                    if (destination is not null)
                    {
                        var bytes = Encoding.ASCII.GetBytes(reply.Credential + "\n");
                        try { await destination.WriteAsync(bytes, ct); await destination.FlushAsync(ct); destination.Flush(true); }
                        finally { Array.Clear(bytes); }
                        await destination.DisposeAsync(); destination = null;
                        return PrintOperatorAccess("create", [grant], null);
                    }
                    if (jsonOutput()) return PrintOperatorAccess("create", [grant], null, reply.Credential);
                    error.WriteLine("Operator access created; grant=" + id + ". Retain the credential durably.");
                    output.WriteLine(reply.Credential);
                    return 0;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or Grpc.Core.RpcException or OperationCanceledException or HttpRequestException)
                {
                    error.WriteLine("Credential transfer was not confirmed. A grant may be active" + (id is null ? "." : ": " + id + ".") +
                        " Inspect operator access list/get and revoke unwanted grants. No rollback, token recovery or automatic retry. Use explicit local recovery if needed.");
                    return CliApplication.TransportError;
                }
            }, token);
        }
        finally
        {
            if (destination is not null)
            {
                try { await destination.DisposeAsync(); }
                catch (Exception) { error.WriteLine("Credential file close failed. Inspect issuance state before recovery; no rollback or automatic retry."); }
            }
        }
    }
    internal Task<int> OperatorAccessAsync(string command, string? id, int size, string? after, CancellationToken token)
    {
        if (command != "list" && (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty) ||
            size is < 1 or > 100 || after is not null && (!Guid.TryParseExact(after, "D", out var cursor) || cursor == Guid.Empty))
            return Task.FromResult(InvalidOperatorInput());
        return ExecuteAsync(async (client, ct) =>
        {
            if (command == "list")
            {
                var request = new ListOperatorAccessRequest { PageSize = size };
                if (after is not null) request.AfterGrantId = after;
                var reply = await client.ListOperatorAccessAsync(request, ct);
                if (reply.Grants.Count > size || reply.HasNextAfterGrantId && (reply.Grants.Count != size || reply.NextAfterGrantId != reply.Grants[^1].GrantId)) throw new InvalidDataException();
                return PrintOperatorAccess(command, reply.Grants.Select(CheckOperatorGrant).ToArray(), reply.HasNextAfterGrantId ? reply.NextAfterGrantId : null);
            }
            var selector = new OperatorAccessRequest { GrantId = id! };
            var result = command == "get" ? await client.GetOperatorAccessAsync(selector, ct) : await client.RevokeOperatorAccessAsync(selector, ct);
            if (result.HasCredential || result.Grant is { } g && (g.GrantId != id || command == "revoke" && !g.HasRevokedAtUtc)) throw new InvalidDataException();
            return PrintOperatorAccess(command, result.Grant is null ? [] : [CheckOperatorGrant(result.Grant)], null);
        }, token);
    }
    private int InvalidOperatorInput()
    {
        error.WriteLine("Invalid operator access input. Creation requires exactly one --token-output-file <new-path> or --token-stdout and optional future UTC expiry.");
        return CliApplication.UsageError;
    }
    private static CliOperatorGrant CheckOperatorGrant(OperatorAccessMetadata g)
    {
        static bool Instant(string value) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) && instant.Offset == TimeSpan.Zero;
        static bool Safe(string value, int max) => value.Length > 0 && value.Length <= max && !value.Any(char.IsControl);
        if (!Guid.TryParseExact(g.GrantId, "D", out var id) || id == Guid.Empty || !Safe(g.Actor, 256) || !Instant(g.CreatedAtUtc) ||
            g.HasDisplayName && !Safe(g.DisplayName, 128) || g.HasExpiresAtUtc && !Instant(g.ExpiresAtUtc) ||
            g.HasRevokedAtUtc != g.HasRevokedBy || g.HasRevokedAtUtc && (!Instant(g.RevokedAtUtc) || !Safe(g.RevokedBy, 256))) throw new InvalidDataException();
        return new(g.GrantId, g.HasDisplayName ? g.DisplayName : null, g.Actor, g.CreatedAtUtc, g.HasExpiresAtUtc ? g.ExpiresAtUtc : null,
            g.HasRevokedBy ? g.RevokedBy : null, g.HasRevokedAtUtc ? g.RevokedAtUtc : null);
    }
    private int PrintOperatorAccess(string command, CliOperatorGrant[] grants, string? after, string? credential = null)
    {
        var found = command == "list" || grants.Length > 0;
        if (jsonOutput()) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new CliEnvelope<CliOperatorAccessData>(1, "operator access " + command,
            found ? "ok" : "notFound", new(grants, after, credential), []), OperatorOutputJsonContext.Default.CliEnvelopeCliOperatorAccessData));
        else
        {
            output.WriteLine("operator access " + command + ": " + (found ? "ok" : "notFound"));
            foreach (var grant in grants) output.WriteLine($"grant={grant.GrantId}; name={grant.DisplayName}; actor={grant.Actor}; created={grant.CreatedAtUtc}; expires={grant.ExpiresAtUtc ?? "never"}; revoked={grant.RevokedAtUtc ?? "no"}; revokedBy={grant.RevokedBy}");
            if (after is not null) output.WriteLine("nextAfterGrantId=" + after);
        }
        return found ? 0 : CliApplication.Rejected;
    }
}
