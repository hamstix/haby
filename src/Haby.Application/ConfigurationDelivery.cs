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
    bool ValidateMaterial(ConfigurationRenderPin pin, ReadOnlySpan<char> material) => ValidateMaterial(material);
}

public sealed record SecretResolutionReference(SecretRef Reference, SecretPurpose Purpose, SharedCredentialScope? SharedUse = null);

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
    ISecretStore secrets, ConfigurationRenderingCatalog rendering, IEnumerable<IConfigurationResolutionBinding> registrations,
    IConfigurationApplicationAuthorization? applicationAuthorization = null)
{
    public const int MaxSecretBytes = ConfigurationDeliveryMaterializer.MaxSecretBytes;
    private readonly ConfigurationDeliveryMaterializer materializer = new(secrets, rendering, registrations, applicationAuthorization);
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
    IEnumerable<IConfigurationResolutionBinding> registrations, IConfigurationApplicationAuthorization? applicationAuthorization = null)
{
    internal const int MaxSecretBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IConfigurationResolutionBinding[] bindings = registrations.ToArray();
    internal async ValueTask<ConfigurationDeliveryResult> ExecuteAsync(ConfigurationDeliveryArtifact artifact, CancellationToken token)
    {
        try
        {
            var revision = artifact.Revision;
            var plan = ConfigurationResolutionPlan.Build(artifact, rendering, bindings, token);
            if (plan.SharedUses.Length != 0 && (applicationAuthorization is null ||
                !await applicationAuthorization.AuthorizeAsync(artifact, plan.SharedUses, token)))
                return new(ConfigurationDeliveryStatus.Denied);
            var staticSecrets = plan.StaticSecrets;
            var authorized = plan.Authorized;
            long expansionBudget = ConfigurationDocumentCodec.MaxBytes - ConfigurationJson.MeasureUnresolvedDelivery(revision.Tree, token);
            var resolved = new Dictionary<string, ConfidentialConfigurationValue>(StringComparer.Ordinal);
            var projected = new Dictionary<string, ConfigurationValue>(StringComparer.Ordinal);
            foreach (var group in staticSecrets.GroupBy(x => (x.Value.Definition, x.Value.Node.Reference)))
            {
                token.ThrowIfCancellationRequested();
                using var secret = await secrets.ResolveBoundedAsync(group.Key.Definition, group.Key.Reference, MaxSecretBytes, token)
                    ?? throw new InvalidDataException();
                var bytes = secret.CopyBytes();
                try
                {
                    if (bytes.Length is 0 or > MaxSecretBytes) throw new ConfigurationDocumentLimitException();
                    var values = new Dictionary<ProfileSecretFormat, ConfigurationValue>();
                    foreach (var (path, binding) in group)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!values.TryGetValue(binding.Node.Format, out var value))
                        {
                            value = ParseConfigurationSecret(bytes, binding.Node.Format);
                            values.Add(binding.Node.Format, value);
                        }
                        expansionBudget -= ConfigurationJson.MeasureResolvedValue(value, token);
                        if (expansionBudget < 0) throw new ConfigurationDocumentLimitException();
                        projected.Add(path, value);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
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
                        if (!binding.Adapter.ValidateMaterial(binding.Pin, chars)) throw new InvalidDataException();
                        var value = rendering.FormatDelivery(binding.Pin, binding.Value, chars) ?? throw new InvalidDataException();
                        token.ThrowIfCancellationRequested();
                        expansionBudget -= ConfigurationJson.MeasureResolvedValue(value.Reveal(), token);
                        if (expansionBudget < 0) throw new ConfigurationDocumentLimitException();
                        resolved.Add(path, value);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); if (chars is not null) Array.Clear(chars); }
            }
            token.ThrowIfCancellationRequested();
            var json = ConfigurationJson.SerializeDelivery(ReplaceProjected(revision.Tree, ""), token);
            if (token.IsCancellationRequested) { CryptographicOperations.ZeroMemory(json); token.ThrowIfCancellationRequested(); }
            return new(ConfigurationDeliveryStatus.Delivered, json, revision.Metadata.Fingerprint);

            ConfigurationValue ReplaceProjected(ConfigurationValue node, string path) => node switch
            {
                ConfigurationProjectedSecret => projected.TryGetValue(path, out var value) ? value : throw new InvalidDataException(),
                ConfigurationDeferredValue => resolved.TryGetValue(path, out var confidential) ? confidential.Reveal() : throw new InvalidDataException(),
                ConfigurationObject o => new ConfigurationObject(o.Members.Select(x => KeyValuePair.Create(x.Key,
                    ReplaceProjected(x.Value, path + "/" + ConfigurationComposer.Escape(x.Key))))),
                ConfigurationArray a => new ConfigurationArray(a.Items.Select((x, i) => ReplaceProjected(x,
                    path + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                _ => node
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (ConfigurationDocumentLimitException) { return new(ConfigurationDeliveryStatus.LimitExceeded); }
        catch (ConfigurationDeliveryStorageException) { return new(ConfigurationDeliveryStatus.StorageUnavailable); }
        catch (Exception) { return new(ConfigurationDeliveryStatus.InputUnavailable); }
    }

    private static ConfigurationValue ParseConfigurationSecret(byte[] bytes, ProfileSecretFormat format)
    {
        var text = StrictUtf8.GetString(bytes);
        if (format == ProfileSecretFormat.String) return new ConfigurationString(text);
        if (format != ProfileSecretFormat.Json) throw new InvalidDataException();
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = ConfigurationDocumentCodec.MaxContainers,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        return Convert(parsed.RootElement);

        static ConfigurationValue Convert(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => ConfigurationNull.Instance,
            JsonValueKind.True => new ConfigurationBoolean(true),
            JsonValueKind.False => new ConfigurationBoolean(false),
            JsonValueKind.String => new ConfigurationString(value.GetString()!),
            JsonValueKind.Number => ConfigurationNumber.Parse(value.GetRawText()),
            JsonValueKind.Array => new ConfigurationArray(value.EnumerateArray().Select(Convert)),
            JsonValueKind.Object => Object(value),
            _ => throw new InvalidDataException()
        };
        static ConfigurationObject Object(JsonElement value)
        {
            var members = value.EnumerateObject().ToArray();
            if (members.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != members.Length) throw new InvalidDataException();
            return new ConfigurationObject(members.Select(x => KeyValuePair.Create(x.Name, Convert(x.Value))));
        }
    }

}
