using System.Security.Cryptography;
using System.Text;

namespace Haby.Persistence.PostgreSql;

internal sealed record RuntimePublicationPreparation(IReadOnlyList<RuntimePublicationResult> Completed,
    RuntimePublicationRequest? Failed = null, string? Reason = null, bool Cancelled = false, bool RequiresInspection = false);

/// <summary>Sequential exact accepted-generation publication; no identity/materialization repair.</summary>
internal sealed class InternalRuntimePublicationPreparation(InternalRuntimePublication publication)
{
    internal static string Key(RuntimePublicationOrigin origin, Guid revision)
    {
        // Local IDs cannot contain '|'; UUIDs and the generation have canonical encodings.
        var text = FormattableString.Invariant($"v1|{origin.Deployment:D}|{origin.Generation}|{origin.Component}|{origin.Workload}|{origin.Document}|{revision:D}");
        return "publication-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    internal async Task<RuntimePublicationPreparation> PrepareAsync(Haby.Application.DurableChangeSetCommit accepted, string actor, CancellationToken token)
    {
        RuntimePublicationRequest[] requests;
        try { requests = await publication.PreparationRequestsAsync(accepted, actor, token); } // Validate all before the first write.
        catch (RuntimePublicationBlockedException blocked) { return new([], blocked.Request, "runtime-binding-revoked"); }
        catch (IOException) { return new([], Reason: "storage-unavailable"); }
        var completed = new List<RuntimePublicationResult>();
        foreach (var request in requests)
        {
            try
            {
                var result = await publication.ApplyAsync(request, token);
                if (result.CurrentBinding.Status != "Prepared" || result.CurrentPermission?.Status != "Published")
                    return new(completed.ToArray(), request, "runtime-authority-unavailable");
                completed.Add(result);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(completed.ToArray(), request, "cancelled", true); }
            catch (InvalidDataException) { return new(completed.ToArray(), request, "runtime-authority-invalid"); }
            // A driver failure can occur while committing or after commit. Preserve the
            // known prefix and exact unconfirmed intent; never claim that it rolled back.
            catch (IOException) { return new(completed.ToArray(), request, "storage-unavailable", RequiresInspection: true); }
        }
        return new(completed.ToArray());
    }
}
