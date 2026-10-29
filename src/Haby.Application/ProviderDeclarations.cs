using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

public sealed record ProviderAdapterKey(string ProviderKind, string ConfigurationContract, string ImplementationId);

/// <summary>Explicit build-time intake and compatibility boundary; never performs external I/O.</summary>
public interface IProviderDeclarationAdapter
{
    ProviderAdapterKey Key { get; }
    IProviderConfigurationCodec Codec { get; }
    IReadOnlyList<string> RequiredSecretSlots { get; }
    IReadOnlyList<string> AllowedSecretSlots => RequiredSecretSlots;
    object ParseProperties(JsonElement properties);
    object Bind(object properties, IReadOnlyDictionary<string, SecretRef> secrets);
    bool CanReplace(ProviderAdapterKey previous);
    bool Equivalent(object previous, object candidate);
    bool SameEndpoint(object previous, object candidate);
}

public sealed class ProviderDeclarationRegistry
{
    private readonly IProviderDeclarationAdapter[] _adapters;
    public ProviderDeclarationRegistry(IEnumerable<IProviderDeclarationAdapter> adapters)
    {
        _adapters = adapters.ToArray();
        if (_adapters.GroupBy(x => x.Key).Any(g => g.Count() != 1) || _adapters.Any(x =>
                !ValidId(x.Key.ProviderKind) || !ValidId(x.Key.ConfigurationContract) || !ValidId(x.Key.ImplementationId) ||
                x.Codec.ProviderKindId != x.Key.ProviderKind || x.Codec.ShapeId != x.Key.ConfigurationContract ||
                x.Codec.PayloadFormatVersion <= 0 ||
                x.AllowedSecretSlots.Count > 64 || x.AllowedSecretSlots.Any(s => !ValidId(s)) ||
                x.AllowedSecretSlots.Distinct(StringComparer.Ordinal).Count() != x.AllowedSecretSlots.Count ||
                x.RequiredSecretSlots.Distinct(StringComparer.Ordinal).Count() != x.RequiredSecretSlots.Count ||
                x.RequiredSecretSlots.Any(s => !x.AllowedSecretSlots.Contains(s, StringComparer.Ordinal))))
            throw new InvalidOperationException("Provider declaration registration is invalid or ambiguous.");
    }
    public IProviderDeclarationAdapter? Find(ProviderAdapterKey key) => _adapters.SingleOrDefault(x => x.Key == key);
    internal static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderDeclaration
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public required ProviderDeclarationMetadata Metadata { get; init; }
    public required ProviderDeclarationSpec Spec { get; init; }
    public override string ToString() => nameof(ProviderDeclaration);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderDeclarationMetadata
{
    public required string Name { get; init; }
    public required string Environment { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderDeclarationSpec
{
    [JsonConverter(typeof(ProviderInteger64Converter))]
    public required long Revision { get; init; }
    public required string ProviderKind { get; init; }
    public required string ConfigurationContract { get; init; }
    public required string ImplementationId { get; init; }
    public required JsonElement Properties { get; init; }
    public required Dictionary<string, ProviderNamedSecretReference> SecretRefs { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderNamedSecretReference
{
    public required string SecretName { get; init; }
    [JsonConverter(typeof(ProviderInteger32Converter))]
    public required int Version { get; init; }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProviderDeclaration))]
internal partial class ProviderDeclarationJsonContext : JsonSerializerContext;

/// <summary>Accept JSON Schema mathematical integers without rounding fractional input.</summary>
public sealed class ProviderInteger64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var number) ||
            number < long.MinValue || number > long.MaxValue || decimal.Truncate(number) != number) throw new JsonException();
        var value = (long)number;
        if (!ExactJsonNumber.Parse(element.GetRawText()).Equals(ExactJsonNumber.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            throw new JsonException();
        return value;
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
public sealed class ProviderInteger32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = new ProviderInteger64Converter().Read(ref reader, typeof(long), options);
        if (value is < int.MinValue or > int.MaxValue) throw new JsonException();
        return (int)value;
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public static class ProviderDeclarationInput
{
    public const int MaxBytes = 1024 * 1024 - 1024;
    public static ProviderDeclaration? Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!UniqueMembers(document.RootElement)) return null;
            var value = document.RootElement.Deserialize(ProviderDeclarationJsonContext.Default.ProviderDeclaration);
            if (value is null || value.ApiVersion != "haby.dev/v1alpha1" || value.Kind != "Provider" ||
                value.Metadata is null || !ValidName(value.Metadata.Name) || !ValidName(value.Metadata.Environment) ||
                value.Spec is null || value.Spec.Revision <= 0 ||
                !ProviderDeclarationRegistry.ValidId(value.Spec.ProviderKind) ||
                !ProviderDeclarationRegistry.ValidId(value.Spec.ConfigurationContract) ||
                !ProviderDeclarationRegistry.ValidId(value.Spec.ImplementationId) ||
                value.Spec.Properties.ValueKind != JsonValueKind.Object || value.Spec.SecretRefs is null ||
                value.Spec.SecretRefs.Count > 64 || value.Spec.SecretRefs.Any(x =>
                    !ProviderDeclarationRegistry.ValidId(x.Key) || x.Value is null ||
                    !ValidName(x.Value.SecretName) || x.Value.Version <= 0)) return null;
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException) { return null; }
    }
    public static bool ValidName(string? value)
    {
        if (value is null) return false;
        try { _ = EnvironmentId.Create(value); return true; }
        catch (ArgumentException) { return false; }
    }
    private static bool UniqueMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in element.EnumerateObject())
                if (!names.Add(member.Name) || !UniqueMembers(member.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!UniqueMembers(item)) return false;
        return true;
    }
}

public enum ProviderOutcome
{
    Created, ExistingEquivalent, InvalidRequest, EnvironmentMismatch, EnvironmentNotFound,
    UnsupportedConfiguration, ConfigurationUnavailable, SecretReferenceUnavailable, RevisionContentConflict,
    RevisionNotIncreasing, IdentityConfirmationRequired, StaleBaseline, ProviderKindConflict,
    StorageUnavailable, Found, NotFound, Listed, InvalidCursor, InvalidPageSize
}
public sealed record ProviderRevisionMetadata(long Revision, string ConfigurationContract, string ImplementationId,
    IReadOnlyList<ProviderConfigurationSecretReference> SecretReferences);
public sealed record ProviderMetadata(EnvironmentRecordId Environment, ProviderInstanceId InstanceId, string? Name,
    string ProviderKind, ProviderRevisionMetadata? Revision);
public sealed record ProviderResult(ProviderOutcome Outcome, ProviderMetadata? Provider = null, long? PreviousRevision = null);
public sealed record ProviderListResult(ProviderOutcome Outcome, IReadOnlyList<ProviderMetadata> Items, string? NextCursor = null);

/// <summary>Atomic declaration acceptance and payload-free metadata queries.</summary>
public interface IProviderDeclarationCatalog
{
    ValueTask<ProviderResult> ApplyAsync(EnvironmentRecordId environment, ProviderDeclaration declaration,
        object properties, IProviderDeclarationAdapter adapter, bool confirmIdentity, long? expectedPreviousRevision,
        string actor, CancellationToken cancellationToken);
    ValueTask<ProviderMetadata?> GetAsync(EnvironmentRecordId environment, string name, long? revision, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ProviderMetadata>> ListAsync(EnvironmentRecordId environment, string? after, int limit, CancellationToken cancellationToken);
}

public sealed class ProviderAdministration(IEnvironmentQuery environments, ProviderDeclarationRegistry registry,
    IProviderDeclarationCatalog catalog)
{
    public async ValueTask<ProviderResult> ApplyAsync(ReadOnlyMemory<byte> bytes, string? explicitEnvironment,
        bool confirmIdentity, long? expectedPreviousRevision, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var declaration = ProviderDeclarationInput.Parse(bytes);
        if (declaration is null || string.IsNullOrWhiteSpace(actor) || actor.Length > 256 ||
            (confirmIdentity ? expectedPreviousRevision is null or <= 0 : expectedPreviousRevision is not null))
            return new(ProviderOutcome.InvalidRequest);
        if (explicitEnvironment is not null && explicitEnvironment != declaration.Metadata.Environment)
            return new(ProviderOutcome.EnvironmentMismatch);
        var spec = declaration.Spec;
        var adapter = registry.Find(new(spec.ProviderKind, spec.ConfigurationContract, spec.ImplementationId));
        if (adapter is null) return new(ProviderOutcome.UnsupportedConfiguration);
        if (spec.SecretRefs.Keys.Any(s => !adapter.AllowedSecretSlots.Contains(s, StringComparer.Ordinal)) ||
            adapter.RequiredSecretSlots.Any(s => !spec.SecretRefs.ContainsKey(s)))
            return new(ProviderOutcome.InvalidRequest);
        object properties;
        try { properties = adapter.ParseProperties(spec.Properties); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or InvalidOperationException or FormatException)
        { return new(ProviderOutcome.InvalidRequest); }
        var environment = await environments.FindAsync(EnvironmentId.Create(declaration.Metadata.Environment), cancellationToken);
        return environment is null ? new(ProviderOutcome.EnvironmentNotFound) :
            await catalog.ApplyAsync(environment, declaration, properties, adapter, confirmIdentity, expectedPreviousRevision, actor, cancellationToken);
    }

    public async ValueTask<ProviderResult> GetAsync(string? environment, string? name, long? revision, CancellationToken cancellationToken)
    {
        if (!ProviderDeclarationInput.ValidName(environment) || !ProviderDeclarationInput.ValidName(name) || revision is <= 0)
            return new(ProviderOutcome.InvalidRequest);
        var record = await environments.FindAsync(EnvironmentId.Create(environment!), cancellationToken);
        if (record is null) return new(ProviderOutcome.EnvironmentNotFound);
        var value = await catalog.GetAsync(record, name!, revision, cancellationToken);
        return value is null ? new(ProviderOutcome.NotFound) : new(ProviderOutcome.Found, value);
    }

    public async ValueTask<ProviderListResult> ListAsync(string? environment, int size, string? cursor, CancellationToken cancellationToken)
    {
        if (!ProviderDeclarationInput.ValidName(environment)) return new(ProviderOutcome.InvalidRequest, []);
        if (size == 0) size = 50;
        if (size is < 1 or > 100) return new(ProviderOutcome.InvalidPageSize, []);
        var record = await environments.FindAsync(EnvironmentId.Create(environment!), cancellationToken);
        if (record is null) return new(ProviderOutcome.EnvironmentNotFound, []);
        var prefix = "provider1:" + record + ":";
        string? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (cursor.Length > prefix.Length + 64 || !cursor.StartsWith(prefix, StringComparison.Ordinal))
                return new(ProviderOutcome.InvalidCursor, []);
            after = cursor[prefix.Length..];
            if (!ProviderDeclarationInput.ValidName(after) && !(after.Length == 37 && after[0] == '~' &&
                Guid.TryParseExact(after[1..], "D", out var id) && id.Version == 7 && after[1..] == id.ToString() &&
                after[20] is ('8' or '9' or 'a' or 'b'))) return new(ProviderOutcome.InvalidCursor, []);
        }
        var rows = await catalog.ListAsync(record, after, size + 1, cancellationToken);
        var items = rows.Take(size).ToArray();
        return new(ProviderOutcome.Listed, items, rows.Count > size ? prefix + (items[^1].Name ?? "~" + items[^1].InstanceId) : null);
    }
}
