using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Pure adapter metadata validates exact output/arguments; it cannot resolve material or render.</summary>
public interface IConfigurationResolutionBinding
{
    ConfigurationFormatterId Formatter { get; }
    ManifestLocalId Implementation { get; }
    SecretResolutionReference? Bind(ConfigurationRenderPin pin, ExternalOperationOutput output, ConfigurationDeferredValue value);
    bool ValidateMaterial(ReadOnlySpan<char> material);
}

public sealed record SecretResolutionReference(SecretRef Reference, SecretPurpose Purpose);

public enum ConfigurationDeliveryStatus { Delivered, Denied, InputUnavailable, LimitExceeded, StorageUnavailable }

/// <summary>Port-level safe unavailable signal, without driver text or inner exceptions.</summary>
public sealed class ConfigurationDeliveryStorageException() : IOException("Configuration delivery storage is unavailable.");

/// <summary>Owned transient bytes, with no material-bearing equality/hash/diagnostics.</summary>
public sealed class ConfigurationDeliveryResult(ConfigurationDeliveryStatus status, byte[]? json = null, string? sourceFingerprint = null) : IDisposable
{
    public ConfigurationDeliveryStatus Status { get; } = status;
    public string? SourceFingerprint { get; } = sourceFingerprint;
    public ReadOnlyMemory<byte> Json => json ?? ReadOnlyMemory<byte>.Empty;
    public void Dispose() { if (json is not null) CryptographicOperations.ZeroMemory(json); }
    public override string ToString() => "[Configuration delivery result]";
}

/// <summary>Static grant authorization remains independent of runtime identity.</summary>
public sealed class DeliverConfiguration(IConfigurationAccessStore access, IConfigurationDeliveryDocumentStore documents,
    ISecretStore secrets, ConfigurationRenderingCatalog rendering, IEnumerable<IConfigurationResolutionBinding> registrations)
{
    public const int MaxSecretBytes = ConfigurationDeliveryMaterializer.MaxSecretBytes;
    private readonly ConfigurationDeliveryMaterializer materializer = new(secrets, rendering, registrations);
    public async ValueTask<ConfigurationDeliveryResult> ExecuteAsync(string credential, ConfigurationDocumentRevisionId revisionId, CancellationToken token)
    {
        var grant = await access.AuthorizeAsync(credential, revisionId, token);
        if (grant is null) return new(ConfigurationDeliveryStatus.Denied);
        try
        {
            var artifact = await documents.ReadAsync(grant, token);
            if (artifact is null || artifact.Revision.Metadata != grant.Document) return new(ConfigurationDeliveryStatus.InputUnavailable);
            return await materializer.ExecuteAsync(artifact, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (ConfigurationDocumentLimitException) { return new(ConfigurationDeliveryStatus.LimitExceeded); }
        catch (ConfigurationDeliveryStorageException) { return new(ConfigurationDeliveryStatus.StorageUnavailable); }
        catch (Exception) { return new(ConfigurationDeliveryStatus.InputUnavailable); }
    }
}

/// <summary>Saved-artifact-only materialization. Callers establish their distinct authority before entry.</summary>
internal sealed class ConfigurationDeliveryMaterializer(ISecretStore secrets, ConfigurationRenderingCatalog rendering,
    IEnumerable<IConfigurationResolutionBinding> registrations)
{
    internal const int MaxSecretBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IConfigurationResolutionBinding[] bindings = registrations.ToArray();
    internal async ValueTask<ConfigurationDeliveryResult> ExecuteAsync(ConfigurationDeliveryArtifact artifact, CancellationToken token)
    {
        try
        {
            var revision = artifact.Revision;
            token.ThrowIfCancellationRequested();
            ConfigurationDocumentCodec.ValidateTree(revision.Tree);
            var frozen = ConfigurationPlanningSnapshotCodec.Decode(revision.FrozenInputs, revision.Metadata.Revision);
            var layout = frozen.Layouts.Single(x => x.Component == revision.Metadata.Target.Component);
            if (layout.Contributions.Any(x => x.Contribution.Document == revision.Metadata.Target.Document && x.Contribution.SecretProjections.Count != 0))
                throw new InvalidDataException();
            using var associations = JsonDocument.Parse(revision.OutputAssociations);
            if (associations.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            var outputs = associations.RootElement.EnumerateArray().Select(a =>
            {
                if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 5 || !Guid.TryParse(a[1].GetString(), out var attempt) || attempt == Guid.Empty) throw new InvalidDataException();
                return (Producer: a[0].GetString(), Attempt: attempt, Output: new ExternalOperationOutput(a[2].GetString()!, a[3].GetString()!, a[4].GetString()!));
            }).ToArray();
            var authorized = new Dictionary<string, (ConfigurationRenderPin Pin, ConfigurationDeferredValue Value, SecretDefinition Definition, SecretRef Reference, IConfigurationResolutionBinding Adapter)>(StringComparer.Ordinal);
            var nextOutput = 0;
            var confirmed = new Dictionary<(string Producer, string Contract, string Key), (Guid Attempt, ExternalOperationOutput Output)>();
            for (var i = 0; i < layout.BindingRequests.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var request = layout.BindingRequests[i];
                var projections = request.Projections.Where(x => x.Document == revision.Metadata.Target.Document).ToArray();
                if (projections.Length == 0) continue;
                var pin = frozen.RenderPins.Single(x => x.Component == layout.Component && x.Request == i);
                // Composition saves one occurrence per participating request, in frozen
                // request order. Reuse is valid only with the same confirmed evidence.
                if (nextOutput >= outputs.Length) throw new InvalidDataException();
                var output = outputs[nextOutput++];
                if (output.Producer != pin.Producer.Value || output.Output.Contract != pin.OutputContract || output.Output.Key != pin.OutputKey)
                    throw new InvalidDataException();
                var key = (pin.Producer.Value, pin.OutputContract, pin.OutputKey);
                if (confirmed.TryGetValue(key, out var prior) && (prior.Attempt != output.Attempt || prior.Output != output.Output))
                    throw new InvalidDataException();
                confirmed[key] = (output.Attempt, output.Output);
                var adapters = bindings.Where(x => x.Formatter == pin.Formatter && x.Implementation == pin.FormatterImplementation).ToArray();
                if (adapters.Length != 1 || !rendering.SupportsDeliveryFormatter(pin)) throw new InvalidDataException();
                foreach (var projection in projections)
                {
                    if (At(revision.Tree, projection.Path) is not ConfigurationDeferredValue deferred) throw new InvalidDataException();
                    var bound = adapters[0].Bind(pin, output.Output, deferred) ?? throw new InvalidDataException();
                    if (bound.Purpose != SecretPurpose.ApplicationLogin) throw new InvalidDataException();
                    var references = References(deferred.Arguments).ToArray();
                    if (references.Length != 1 || references[0] != bound.Reference) throw new InvalidDataException();
                    authorized.Add(projection.Path, (pin, deferred, new(new SecretOwner.Resource(new(artifact.Environment,
                        revision.Metadata.Revision.ApplicationRecordId, pin.ResourceLocalId)), bound.Purpose), bound.Reference, adapters[0]));
                }
            }
            if (nextOutput != outputs.Length) throw new InvalidDataException();
            // Validate every confidential node before resolving even the first SecretRef.
            Validate(revision.Tree, "");
            long expansionBudget = ConfigurationDocumentCodec.MaxBytes;
            var resolved = new Dictionary<string, ConfidentialConfigurationString>(StringComparer.Ordinal);
            // Group by exact trusted scope: resolve once, format every receiving node,
            // then dispose before loading the next secret. Material memory stays bounded
            // independently of the number of distinct references in the saved artifact.
            foreach (var group in authorized.GroupBy(x => (x.Value.Definition, x.Value.Reference)))
            {
                token.ThrowIfCancellationRequested();
                using var secret = await secrets.ResolveBoundedAsync(group.Key.Definition, group.Key.Reference, MaxSecretBytes, token) ?? throw new InvalidDataException();
                token.ThrowIfCancellationRequested();
                var bytes = secret.CopyBytes();
                char[]? chars = null;
                try
                {
                    if (bytes.Length is 0 or > MaxSecretBytes) throw new ConfigurationDocumentLimitException();
                    chars = StrictUtf8.GetChars(bytes);
                    if (chars.Contains('\0')) throw new InvalidDataException();
                    foreach (var (path, binding) in group)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!binding.Adapter.ValidateMaterial(chars)) throw new InvalidDataException();
                        var value = rendering.FormatDelivery(binding.Pin, binding.Value, chars) ?? throw new InvalidDataException();
                        token.ThrowIfCancellationRequested();
                        expansionBudget -= Encoding.UTF8.GetByteCount(value.Reveal());
                        if (expansionBudget < 0) throw new ConfigurationDocumentLimitException();
                        resolved.Add(path, value);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); if (chars is not null) Array.Clear(chars); }
            }
            token.ThrowIfCancellationRequested();
            var json = ConfigurationJson.SerializeDelivery(revision.Tree, resolved, token);
            if (token.IsCancellationRequested) { CryptographicOperations.ZeroMemory(json); token.ThrowIfCancellationRequested(); }
            return new(ConfigurationDeliveryStatus.Delivered, json, revision.Metadata.Fingerprint);

            void Validate(ConfigurationValue node, string path)
            {
                token.ThrowIfCancellationRequested();
                switch (node)
                {
                    case ConfigurationDeferredValue d:
                        if (!authorized.TryGetValue(path, out var binding) || binding.Value != d) throw new InvalidDataException();
                        break;
                    case ConfigurationSecretReference: throw new InvalidDataException();
                    case ConfigurationObject o: foreach (var (key, child) in o.Members) Validate(child, path + "/" + ConfigurationComposer.Escape(key)); break;
                    case ConfigurationArray a: for (var j = 0; j < a.Items.Count; j++) Validate(a.Items[j], path + "/" + j.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ConfigurationDocumentLimitException) { return new(ConfigurationDeliveryStatus.LimitExceeded); }
        catch (ConfigurationDeliveryStorageException) { return new(ConfigurationDeliveryStatus.StorageUnavailable); }
        catch (Exception) { return new(ConfigurationDeliveryStatus.InputUnavailable); }
    }

    private static ConfigurationValue At(ConfigurationValue tree, string path)
    {
        if (path.Length == 0) return tree;
        if (!path.StartsWith('/')) throw new InvalidDataException();
        foreach (var segment in path[1..].Split('/'))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (ConfigurationComposer.Escape(key) != segment || tree is not ConfigurationObject o || !o.Members.TryGetValue(key, out tree!)) throw new InvalidDataException();
        }
        return tree;
    }
    private static IEnumerable<SecretRef> References(ConfigurationValue value)
    {
        switch (value)
        {
            case ConfigurationSecretReference s: yield return s.Reference; break;
            case ConfigurationObject o: foreach (var child in o.Members.Values) foreach (var r in References(child)) yield return r; break;
            case ConfigurationArray a: foreach (var child in a.Items) foreach (var r in References(child)) yield return r; break;
            case ConfigurationDeferredValue: throw new InvalidDataException();
        }
    }
}
