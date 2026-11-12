using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>One current detached, canonical non-secret set format. Unknown versions have no fallback.</summary>
public static class ExactProviderResolutionSetCodec
{
    public const int Version = 1;
    public static string Encode(ExactProviderResolutionSet set)
    {
        var json = JsonSerializer.Serialize(new ProviderSetDocument(Version,
        set.Entries.Values.OrderBy(x => x.Input is ProviderInput.Resource ? 0 : 1)
            .ThenBy(x => x.Input is ProviderInput.Workload w ? w.Component.Value : "", StringComparer.Ordinal)
            .ThenBy(x => x.Input is ProviderInput.Resource r ? r.Id.Value : ((ProviderInput.Workload)x.Input).Id.Value, StringComparer.Ordinal)
            .Select(x => new ProviderEntryDocument(x.Input is ProviderInput.Resource ? "resource" : "workload",
                x.Input is ProviderInput.Workload w ? w.Component.Value : null,
                x.Input is ProviderInput.Resource r ? r.Id.Value : ((ProviderInput.Workload)x.Input).Id.Value,
                x.Environment.ToString(), x.Alias.Value, x.Instance.ToString(), x.Configuration.Value, x.Implementation.Value,
                Adapter(x.Naming), Adapter(x.Placement))).ToArray()), ProviderSetJsonContext.Default.ProviderSetDocument);
        if (Encoding.UTF8.GetByteCount(json) > ConfigurationDocumentCodec.MaxBytes)
            throw new InvalidDataException("Exact Provider input set exceeds the storage limit.");
        return json;
    }

    private static AdapterInputDocument? Adapter(ExactAdapterInput? input)
    {
        if (input is null) return null;
        ConfigurationDocumentCodec.ValidateTree(input.Value);
        return new(input.Contract.Value, ConfigurationJson.Serialize(input.Value).Json ?? throw new InvalidDataException("Provider input must be non-secret."));
    }

    public static ExactProviderResolutionSet Decode(string json, NormalizedApplicationManifest manifest, EnvironmentRecordId environment)
    {
        try
        {
            if (json.Length > ConfigurationDocumentCodec.MaxBytes || Encoding.UTF8.GetByteCount(json) > ConfigurationDocumentCodec.MaxBytes) throw new InvalidDataException();
            using (var tree = JsonDocument.Parse(json)) RejectDuplicateMembers(tree.RootElement);
            var document = JsonSerializer.Deserialize(json, ProviderSetJsonContext.Default.ProviderSetDocument);
            if (document is null || document.Version != Version || document.Entries is null) throw new InvalidDataException();
            return new(manifest, environment, document.Entries.Select(x => new ExactProviderResolution(x.Kind switch
            {
                "resource" when x.Component is null => new ProviderInput.Resource(ManifestLocalId.Create(x.Id)),
                "workload" when x.Component is not null => new ProviderInput.Workload(ManifestLocalId.Create(x.Component), ManifestLocalId.Create(x.Id)),
                _ => throw new InvalidDataException()
            }, EnvironmentRecordId.Parse(x.Environment), ProviderAlias.Create(x.Alias), ProviderInstanceId.Parse(x.Instance),
                ProviderConfigurationRevisionNumber.Create(x.Configuration), ProviderImplementationId.Create(x.Implementation), Read(x.Naming), Read(x.Placement))));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or NullReferenceException or InvalidOperationException)
        { throw new InvalidDataException("Exact Provider input set is unavailable."); }
    }

    private static ExactAdapterInput? Read(AdapterInputDocument? input)
    {
        if (input is null) return null;
        using var document = JsonDocument.Parse(input.Json);
        RejectDuplicateMembers(document.RootElement);
        var value = ConfigurationValueMapper.FromValidatedJson(document.RootElement) as ConfigurationObject ?? throw new InvalidDataException();
        ConfigurationDocumentCodec.ValidateTree(value);
        return new(AdapterInputContractId.Create(input.Contract), value);
    }

    private static void RejectDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = value.EnumerateObject().ToArray();
            if (fields.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) throw new InvalidDataException();
            foreach (var field in fields) RejectDuplicateMembers(field.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicateMembers(child);
    }
}

internal sealed record ProviderSetDocument(int Version, ProviderEntryDocument[] Entries);
internal sealed record ProviderEntryDocument(string Kind, string? Component, string Id, string Environment, string Alias,
    string Instance, long Configuration, string Implementation, AdapterInputDocument? Naming, AdapterInputDocument? Placement);
internal sealed record AdapterInputDocument(string Contract, string Json);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ProviderSetDocument))]
internal partial class ProviderSetJsonContext : JsonSerializerContext;
