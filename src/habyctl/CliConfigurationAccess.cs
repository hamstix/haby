using System.Globalization;
using System.Text;
using System.Text.Json;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed partial class CliCommandContext
{
    internal async Task<int> CreateConfigurationAccessAsync(ConfigurationDocumentRequest selector, string? environment, long? generation,
        string? expiry, string? file, bool stdout, CancellationToken token)
    {
        if ((file is null) == !stdout || generation <= 0) return InvalidAccess();
        var request = new CreateConfigurationAccessRequest { Selector = selector };
        if (generation.HasValue) selector.Generation = generation.Value;
        if (expiry is not null)
        {
            if (!(expiry.EndsWith('Z') || expiry.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                parsed.Offset != TimeSpan.Zero || parsed <= DateTimeOffset.UtcNow) return InvalidAccess();
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
            return await ExecuteSecretAsync(environment, async (client, env, ct) =>
            {
                selector.Environment = env;
                string? grantId = null;
                var received = false;
                try
                {
                    var reply = await client.CreateConfigurationAccessAsync(request, ct);
                    received = true;
                    if (Guid.TryParseExact(reply.Grant?.GrantId, "D", out var known)) grantId = known.ToString();
                    CheckAccess(reply, "create", selector);
                    if (reply.Status != ConfigurationAccessStatus.Created) return PrintAccess("create", reply, false);
                    if (destination is not null)
                    {
                        var bytes = Encoding.ASCII.GetBytes(reply.Credential + "\n");
                        try { await destination.WriteAsync(bytes, ct); await destination.FlushAsync(ct); destination.Flush(flushToDisk: true); }
                        finally { Array.Clear(bytes); }
                        return PrintAccess("create", reply, false);
                    }
                    if (jsonOutput()) return PrintAccess("create", reply, true);
                    error.WriteLine($"configuration access created; grant={grantId}; revision={reply.Grant!.Document.RevisionId}. Retain the credential durably.");
                    output.WriteLine(reply.Credential);
                    return 0;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or Grpc.Core.RpcException or OperationCanceledException or HttpRequestException)
                {
                    if (grantId is not null)
                        error.WriteLine($"Credential transfer was not confirmed. Grant {grantId} may be active. Inspect: habyctl configuration access get {grantId}; revoke: habyctl configuration access revoke {grantId}. No token recovery or rollback is available.");
                    else error.WriteLine(received ? "Creation response was invalid; a grant may be active. No credential recovery is available." : "Creation outcome is uncertain. No automatic retry or credential recovery is available; a new creation issues a separate grant.");
                    return CliApplication.TransportError;
                }
            }, token);
        }
        finally { if (destination is not null) await destination.DisposeAsync(); }
    }

    internal Task<int> ConfigurationAccessAsync(string command, string id, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty) return Task.FromResult(InvalidAccess());
        return ExecuteAsync(async (client, ct) =>
        {
            var request = new ConfigurationAccessRequest { GrantId = parsed.ToString() };
            var reply = command == "get" ? await client.GetConfigurationAccessAsync(request, ct) : await client.RevokeConfigurationAccessAsync(request, ct);
            CheckAccess(reply, command, null);
            if (reply.Grant is { } grant && grant.GrantId != request.GrantId) throw new InvalidDataException();
            return PrintAccess(command, reply, false);
        }, token);
    }
    private int InvalidAccess()
    {
        error.WriteLine("Use exactly one --token-output-file <new-path> or --token-stdout; generation must be positive and optional expiry a future UTC instant.");
        return CliApplication.UsageError;
    }
    private static void CheckAccess(ConfigurationAccessReply reply, string command, ConfigurationDocumentRequest? selector)
    {
        var success = reply.Status == (command == "create" ? ConfigurationAccessStatus.Created : command == "get" ? ConfigurationAccessStatus.Found : ConfigurationAccessStatus.Revoked);
        if (!success)
        {
            if (reply.Status is not (ConfigurationAccessStatus.NotFound or ConfigurationAccessStatus.InvalidRequest or ConfigurationAccessStatus.StorageUnavailable) ||
                reply.Grant is not null || reply.HasCredential) throw new InvalidDataException();
            return;
        }
        var grant = reply.Grant ?? throw new InvalidDataException();
        if (!Guid.TryParseExact(grant.GrantId, "D", out var id) || id == Guid.Empty || grant.Actor.Length is 0 or > 256 || grant.Actor.Any(char.IsControl) ||
            !DateTimeOffset.TryParseExact(grant.CreatedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var created) || created.Offset != TimeSpan.Zero ||
            grant.HasRevokedAtUtc != grant.HasRevokedBy || command == "revoke" && !grant.HasRevokedAtUtc ||
            command != "create" && reply.HasCredential) throw new InvalidDataException();
        var document = grant.Document ?? throw new InvalidDataException();
        selector ??= new() { Environment = document.Environment, ApplicationId = document.ApplicationId, ComponentId = document.Target?.ComponentId ?? "", DocumentId = document.Target?.DocumentId ?? "" };
        _ = ConfigurationDocumentPresentation.Check("get", selector, new ConfigurationDocumentReply { Status = ConfigurationDocumentStatus.Found, Selector = selector.Clone(), Target = document.Target, Artifact = document });
        if (command == "create" && (!reply.HasCredential || !ValidCredential(reply.Credential, id) || grant.HasRevokedAtUtc)) throw new InvalidDataException();
        if (grant.HasExpiresAtUtc && (!DateTimeOffset.TryParseExact(grant.ExpiresAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) || expiry.Offset != TimeSpan.Zero || expiry <= created)) throw new InvalidDataException();
        if (grant.HasRevokedAtUtc && (!DateTimeOffset.TryParseExact(grant.RevokedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var revoked) || revoked.Offset != TimeSpan.Zero || grant.RevokedBy.Length is 0 or > 256 || grant.RevokedBy.Any(char.IsControl))) throw new InvalidDataException();
    }
    private static bool ValidCredential(string value, Guid id) => value.Length == 114 && value.StartsWith("haby_delivery_v1." + id.ToString("N") + ".", StringComparison.Ordinal) &&
        value.AsSpan(50).IndexOfAnyExcept("0123456789abcdef") < 0;

    private int PrintAccess(string command, ConfigurationAccessReply reply, bool includeCredential)
    {
        var outcome = reply.Status switch
        {
            ConfigurationAccessStatus.NotFound => "notFound",
            ConfigurationAccessStatus.InvalidRequest => "invalidRequest",
            ConfigurationAccessStatus.StorageUnavailable => "storageUnavailable",
            _ => reply.Status.ToString().ToLowerInvariant()
        };
        if (jsonOutput())
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject(); w.WriteNumber("formatVersion", 1); w.WriteString("command", "configuration access " + command); w.WriteString("outcome", outcome);
                if (reply.Grant is { } g)
                {
                    w.WriteStartObject("data"); w.WriteString("grantId", g.GrantId); w.WriteString("revisionId", g.Document.RevisionId);
                    w.WriteString("environment", g.Document.Environment); w.WriteString("applicationId", g.Document.ApplicationId);
                    w.WriteString("componentId", g.Document.Target.ComponentId); w.WriteString("documentId", g.Document.Target.DocumentId);
                    w.WriteNumber("generation", g.Document.Target.Generation); w.WriteString("actor", g.Actor); w.WriteString("createdAtUtc", g.CreatedAtUtc);
                    w.WriteString("deploymentId", g.Document.Target.DeploymentId); w.WriteString("planId", g.Document.PlanId);
                    w.WriteString("sourceApplicationRecordId", g.Document.SourceRevision.ApplicationRecordId); w.WriteNumber("sourceRevision", g.Document.SourceRevision.RevisionNumber);
                    w.WriteString("fileName", g.Document.FileName); w.WriteString("format", g.Document.Format); w.WriteString("serializer", g.Document.Serializer);
                    w.WriteString("fingerprint", g.Document.Fingerprint); w.WriteString("frozenInputFingerprint", g.Document.FrozenInputFingerprint);
                    w.WriteString("artifactActor", g.Document.TechnicalActorId); w.WriteString("artifactCreatedAtUtc", g.Document.CreatedAtUtc);
                    if (g.HasExpiresAtUtc) w.WriteString("expiresAtUtc", g.ExpiresAtUtc);
                    if (g.HasRevokedAtUtc) { w.WriteString("revokedAtUtc", g.RevokedAtUtc); w.WriteString("revokedBy", g.RevokedBy); }
                    if (includeCredential) w.WriteString("credential", reply.Credential);
                    w.WriteEndObject();
                }
                else w.WriteNull("data");
                w.WriteStartArray("diagnostics"); w.WriteEndArray();
                w.WriteEndObject();
            }
            output.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
        else
        {
            output.WriteLine($"configuration access {command}: {outcome}; grant={reply.Grant?.GrantId}; revision={reply.Grant?.Document.RevisionId}");
            if (reply.Grant is { } g) output.WriteLine($"scope={g.Document.Environment}/{g.Document.ApplicationId}/{g.Document.Target.ComponentId}/{g.Document.Target.DocumentId}; generation={g.Document.Target.Generation}; actor={g.Actor}; created={g.CreatedAtUtc}; expires={(g.HasExpiresAtUtc ? g.ExpiresAtUtc : "never")}; revoked={(g.HasRevokedAtUtc ? g.RevokedAtUtc : "no")}; revokedBy={g.RevokedBy}");
        }
        return reply.Status is ConfigurationAccessStatus.Created or ConfigurationAccessStatus.Found or ConfigurationAccessStatus.Revoked ? 0 :
            reply.Status == ConfigurationAccessStatus.StorageUnavailable ? CliApplication.TransportError : CliApplication.Rejected;
    }
}
