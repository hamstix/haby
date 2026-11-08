using System.Collections.Immutable;
using Haby.Domain;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Application;

public sealed record ConfigurationApplicationSelection(EnvironmentId Environment, ApplicationId Application, DeploymentGeneration? Generation);
public sealed record ConfigurationApplicationTarget(ApplicationDeploymentId Deployment, DeploymentGeneration Generation);
public sealed record ConfigurationApplicationInventory(ConfigurationDocumentStatus Status, ConfigurationApplicationTarget? Target = null,
    IReadOnlyList<ConfigurationDocumentTarget>? Documents = null);

/// <summary>One consistent frozen-generation read, with no materialization or mutable Profile selection.</summary>
public interface IConfigurationApplicationInventory
{
    ValueTask<ConfigurationApplicationInventory> SelectApplicationAsync(ConfigurationApplicationSelection selection, CancellationToken token);
}

public enum ConfigurationApplicationStatus { Succeeded, Failed, InvalidRequest, NotFound, NotApplied, InputCorrupt, StorageUnavailable, LimitExceeded }
public sealed record ConfigurationApplicationItem(ConfigurationDocumentTarget Target, ConfigurationDocumentStatus Status, ConfigurationDocumentMetadata? Artifact = null);
public sealed record ConfigurationApplicationResult(ConfigurationApplicationStatus Status, ConfigurationApplicationTarget? Target,
    IReadOnlyList<ConfigurationApplicationItem> Items);

/// <summary>Finite unary metadata contract. Reservations include worst-case JSON escaping and envelope/newline overhead.</summary>
public static class ConfigurationApplicationLimits
{
    public const int MaxItems = 512;
    public const int MaxResponseBytes = 8 * 1024 * 1024;
    public const int FixedResponseBytes = 16 * 1024;
    public const int ItemResponseBytes = 12 * 1024;
    public static long ResponseReservation(int count) => FixedResponseBytes + (long)count * ItemResponseBytes;
    public static bool Fits(int count) => count is >= 0 and <= MaxItems && ResponseReservation(count) <= MaxResponseBytes;
    public static bool SafeText(string text, int maximum) => text.Length is > 0 && text.Length <= maximum && !text.Any(char.IsControl);
    public static bool Bounded(ConfigurationDocumentMetadata metadata) => SafeText(metadata.FileName, 255) &&
        metadata.FileName.IndexOfAny(['/', '\\']) < 0 && SafeText(metadata.Actor, 256) && metadata.Format == "json" &&
        metadata.Serializer == ConfigurationJson.Profile && metadata.Fingerprint.Length == 97 && metadata.Fingerprint.StartsWith("sha256:configuration-revision-v1:", StringComparison.Ordinal) &&
        Hex(metadata.Fingerprint[33..]) && metadata.FrozenInputFingerprint.Length == 64 && Hex(metadata.FrozenInputFingerprint) &&
        metadata.CreatedAt.Offset == TimeSpan.Zero;
    private static bool Hex(string text) => text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Sequential best-effort orchestration; each exact document retains its existing atomic acceptance.</summary>
public sealed class MaterializeApplicationConfiguration(IConfigurationApplicationInventory inventory, IConfigurationDocumentStore documents)
{
    public async ValueTask<ConfigurationApplicationResult> ExecuteAsync(ConfigurationApplicationSelection selection, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ConfigurationApplicationLimits.SafeText(actor, 256)) return new(ConfigurationApplicationStatus.InvalidRequest, null, []);
        ConfigurationApplicationInventory selected;
        try { selected = await inventory.SelectApplicationAsync(selection, token); }
        catch (ConfigurationDocumentLimitException) { return new(ConfigurationApplicationStatus.LimitExceeded, null, []); }
        catch (InvalidDataException) { return new(ConfigurationApplicationStatus.InputCorrupt, null, []); }
        catch (IOException) { return new(ConfigurationApplicationStatus.StorageUnavailable, null, []); }
        if (selected.Status != ConfigurationDocumentStatus.Found)
            return new(selected.Status switch
            {
                ConfigurationDocumentStatus.NotFound => ConfigurationApplicationStatus.NotFound,
                ConfigurationDocumentStatus.NotApplied => ConfigurationApplicationStatus.NotApplied,
                ConfigurationDocumentStatus.LimitExceeded => ConfigurationApplicationStatus.LimitExceeded,
                _ => ConfigurationApplicationStatus.InputCorrupt
            }, selected.Target, []);
        var target = selected.Target;
        // Copy the inventory before any asynchronous item work. Never resolve the selector again.
        if (selected.Documents is null) return new(ConfigurationApplicationStatus.InputCorrupt, target, []);
        if (!ConfigurationApplicationLimits.Fits(selected.Documents.Count)) return new(ConfigurationApplicationStatus.LimitExceeded, target, []);
        var pairs = selected.Documents.ToImmutableArray();
        if (target is null || selection.Generation is { } generation && target.Generation != generation ||
            pairs.Any(p => p.Deployment != target.Deployment || p.Generation != target.Generation) ||
            !pairs.SequenceEqual(pairs.Distinct().OrderBy(p => p.Component.Value, StringComparer.Ordinal).ThenBy(p => p.Document.Value, StringComparer.Ordinal)))
            return new(ConfigurationApplicationStatus.InputCorrupt, target, []);

        // Existing protected artifacts can contain legacy/corrupt metadata. Check all of them
        // before writes, so none can invalidate the reserved normal-response budget later.
        try
        {
            foreach (var pair in pairs)
            {
                token.ThrowIfCancellationRequested();
                var read = await documents.ReadAsync(pair, token);
                if (read.Status == ConfigurationDocumentStatus.NotFound && read.Revision is null) continue;
                if (read.Status != ConfigurationDocumentStatus.Found || read.Revision is not { } saved ||
                    saved.Metadata.Target != pair || saved.Metadata.Environment != selection.Environment || saved.Metadata.Application != selection.Application ||
                    !ConfigurationApplicationLimits.Bounded(saved.Metadata)) return new(ConfigurationApplicationStatus.InputCorrupt, target, []);
            }
        }
        catch (ConfigurationDocumentLimitException) { return new(ConfigurationApplicationStatus.LimitExceeded, target, []); }
        catch (InvalidDataException) { return new(ConfigurationApplicationStatus.InputCorrupt, target, []); }
        catch (IOException) { return new(ConfigurationApplicationStatus.StorageUnavailable, target, []); }

        token.ThrowIfCancellationRequested();
        var items = new List<ConfigurationApplicationItem>(pairs.Length);
        var failed = false;
        foreach (var pair in pairs)
        {
            token.ThrowIfCancellationRequested();
            if (failed) { items.Add(new(pair, ConfigurationDocumentStatus.NotAttempted)); continue; }
            ConfigurationDocumentResult result;
            try { result = await documents.MaterializeAsync(pair, actor, token); }
            catch (ConfigurationDocumentLimitException) { result = new(ConfigurationDocumentStatus.LimitExceeded); }
            catch (InvalidDataException) { result = new(ConfigurationDocumentStatus.InputCorrupt); }
            catch (IOException) { result = new(ConfigurationDocumentStatus.StorageUnavailable); }
            var success = result.Status is ConfigurationDocumentStatus.Created or ConfigurationDocumentStatus.Existing;
            if (success && (result.Revision is not { } revision || revision.Metadata.Target != pair ||
                revision.Metadata.Environment != selection.Environment || revision.Metadata.Application != selection.Application || !ConfigurationApplicationLimits.Bounded(revision.Metadata)) ||
                result.Status is not (ConfigurationDocumentStatus.Created or ConfigurationDocumentStatus.Existing or ConfigurationDocumentStatus.NotFound or
                    ConfigurationDocumentStatus.InvalidRequest or ConfigurationDocumentStatus.NotApplied or ConfigurationDocumentStatus.InputUnavailable or
                    ConfigurationDocumentStatus.IntegrityConflict or ConfigurationDocumentStatus.LimitExceeded or ConfigurationDocumentStatus.InputCorrupt or ConfigurationDocumentStatus.StorageUnavailable))
            { result = new(ConfigurationDocumentStatus.InputCorrupt); success = false; }
            items.Add(new(pair, result.Status, success ? result.Revision!.Metadata : null));
            failed = !success;
        }
        return new(failed ? ConfigurationApplicationStatus.Failed : ConfigurationApplicationStatus.Succeeded, target, items.ToImmutableArray());
    }
}
