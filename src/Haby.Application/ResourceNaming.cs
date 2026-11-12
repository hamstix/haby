using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Closed portable policy. Adapter validation determines whether a prefix is usable.</summary>
public sealed record ResourceNamingPolicy(string Mode, string? Prefix = null)
{
    public static ResourceNamingPolicy Exact { get; } = new("exact");
    public void Validate()
    {
        if (Mode is not ("exact" or "prefixed") || Mode == "exact" && Prefix is not null ||
            Mode == "prefixed" && (Prefix is not { Length: > 0 and <= 128 } || Prefix.Any(char.IsControl)))
            throw new InvalidDataException("Invalid naming policy.");
    }
}

public sealed record ResourceNamingReference(string Name, long Revision)
{
    public void Validate()
    {
        if (Name is null || Name.Length is 0 or > 64 || !System.Text.RegularExpressions.Regex.IsMatch(Name,
            @"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z") || Revision <= 0)
            throw new InvalidDataException("Invalid naming policy reference.");
    }
}

/// <summary>Canonical absence is exact; selection identity is significant even for equal policy values.</summary>
public sealed record ResourceNamingSnapshot(int Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ResourceNamingReference? Reference, ResourceNamingPolicy Policy)
{
    public string? SelectionToken { get; init; }
    public static ResourceNamingSnapshot Default { get; } = new(1, null, ResourceNamingPolicy.Exact);
    public void Validate()
    {
        if (Version != 1 || Policy is null) throw new InvalidDataException("Naming snapshot unavailable.");
        Policy.Validate();
        Reference?.Validate();
        if (SelectionToken is not null && (Reference is null || !Guid.TryParseExact(SelectionToken, "D", out var token) || token == Guid.Empty))
            throw new InvalidDataException("Invalid naming selection token.");
        if (Reference is null && Policy != ResourceNamingPolicy.Exact) throw new InvalidDataException("Invalid naming default.");
    }
}

public static class ResourceNamingCodec
{
    private static readonly AdapterInputContractId Contract = AdapterInputContractId.Create("haby.resource-naming/v1");

    public static ExactAdapterInput Exact(ResourceNamingSnapshot snapshot)
        => new(Contract, new ConfigurationObject([new("snapshot", new ConfigurationString(Encode(snapshot)))]));

    public static ResourceNamingSnapshot ResourceNaming(this ExactProviderResolution input) => ReadNaming(input.Naming);
    public static ResourceNamingSnapshot ResourceNaming(this UniformResourceProviderContext input) => ReadNaming(input.Naming);

    private static ResourceNamingSnapshot ReadNaming(ExactAdapterInput? input)
    {
        if (input is not { } naming || naming.Contract.Value != Contract.Value)
            throw new InvalidDataException("Resource naming input is unavailable.");
        if (naming.Value.Members.Count != 1 || !naming.Value.Members.TryGetValue("snapshot", out var value) || value is not ConfigurationString snapshot)
            throw new InvalidDataException("Resource naming input is unavailable.");
        return Decode(snapshot.Value);
    }

    public static string Encode(ResourceNamingSnapshot value)
    {
        value.Validate();
        return JsonSerializer.Serialize(value, ResourceNamingJsonContext.Default.ResourceNamingSnapshot);
    }
    public static ResourceNamingSnapshot Decode(string json)
    {
        try
        {
            if (json is null || json.Length > 2048) throw new InvalidDataException("Naming snapshot unavailable.");
            var result = JsonSerializer.Deserialize(json, ResourceNamingJsonContext.Default.ResourceNamingSnapshot)
                ?? throw new InvalidDataException("Naming snapshot unavailable.");
            result.Validate();
            if (Encode(result) != json) throw new InvalidDataException("Noncanonical naming snapshot.");
            return result;
        }
        catch (JsonException) { throw new InvalidDataException("Naming snapshot unavailable."); }
    }
    public static ResourceNamingPolicy ParsePolicy(string json)
    {
        try
        {
            if (json is null || json.Length > 1024) throw new InvalidDataException("Invalid naming declaration.");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid naming declaration.");
            var fields = doc.RootElement.EnumerateObject().ToArray();
            if (fields.Any(x => x.Value.ValueKind != JsonValueKind.String) ||
                fields.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                throw new InvalidDataException("Invalid naming declaration.");
            var value = JsonSerializer.Deserialize(json, ResourceNamingJsonContext.Default.ResourceNamingPolicy)
                ?? throw new InvalidDataException("Invalid naming declaration.");
            value.Validate();
            return value;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid naming declaration."); }
    }
}

public enum ResourceNamingOutcome
{
    Created, Found, Changed, Unchanged, NotFound, StaleExpectation, InvalidDeclaration,
    EnvironmentNotFound, BindingNotFound, RevisionNotFound, RevisionConflict, StoreUnavailable
}
public sealed record ResourceNamingState(ResourceNamingSnapshot Snapshot, string? Token, string Actor, DateTimeOffset ChangedAt);
public sealed record ResourceNamingResult(ResourceNamingOutcome Outcome, ResourceNamingState? State = null);

/// <summary>Selection cannot change the Provider binding. Null expectedToken means expected absence.</summary>
public interface IResourceNamingStore
{
    ValueTask<ResourceNamingResult> CreateAsync(EnvironmentRecordId environment, ResourceNamingReference reference,
        ResourceNamingPolicy policy, string actor, CancellationToken token);
    ValueTask<ResourceNamingResult> GetAsync(EnvironmentRecordId environment, ResourceNamingReference reference, CancellationToken token);
    ValueTask<ResourceNamingResult> GetSelectionAsync(EnvironmentRecordId environment, ProviderAlias alias, CancellationToken token);
    ValueTask<ResourceNamingResult> SelectAsync(EnvironmentRecordId environment, ProviderAlias alias,
        ProviderAliasBinding expectedBinding, string? expectedToken, ResourceNamingReference reference, string actor, CancellationToken token);
}

/// <summary>Adapters compare physical-name semantics against the original frozen Resource inputs.</summary>
public interface IDurableResourceNamingCompatibility
{
    bool SameNames(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs original,
        DurablePlanningInputs proposed, PreparedProviderConfiguration configuration);
}

/// <summary>Explicit opt-in to interpreting a selected naming policy for this Resource family.</summary>
public interface IResourceNamingPolicyHandler
{
    bool SupportsNaming(ResourceNamingPolicy policy);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ResourceNamingSnapshot))]
[JsonSerializable(typeof(ResourceNamingPolicy))]
internal partial class ResourceNamingJsonContext : JsonSerializerContext;
