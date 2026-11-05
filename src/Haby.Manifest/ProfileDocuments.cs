using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.Manifest;

public sealed class ConfigurationProfileDocument
{
    [JsonRequired] public required string ApiVersion { get; init; }
    [JsonRequired] public required string Kind { get; init; }
    [JsonRequired] public required ProfileMetadata Metadata { get; init; }
    [JsonRequired] public required ProfileSpec Spec { get; init; }
}
public sealed class ProfileMetadata
{
    [JsonRequired] public required string Id { get; init; }
    [JsonRequired, JsonConverter(typeof(ProfileRevisionConverter))] public required int Revision { get; init; }
}
public sealed class ProfileSpec
{
    private Dictionary<string, ProfileJsonDocument>? _documents;
    private Dictionary<string, ProfileContributionDocument>? _contributions;
    private Dictionary<string, ProfileRuleDocument>? _rules;
    public Dictionary<string, ProfileJsonDocument> Documents { get => _documents ??= []; init => _documents = value; }
    public Dictionary<string, ProfileContributionDocument> StaticContributions { get => _contributions ??= []; init => _contributions = value; }
    public Dictionary<string, ProfileRuleDocument> BindingRules { get => _rules ??= []; init => _rules = value; }
}
public sealed class ProfileJsonDocument
{
    [JsonRequired] public required string FileName { get; init; }
    [JsonRequired] public required string Format { get; init; }
    [JsonRequired] public required JsonElement Template { get; init; }
}
public sealed class ProfileContributionDocument
{
    [JsonRequired] public required string DocumentRef { get; init; }
    [JsonRequired] public required string Path { get; init; }
    [JsonRequired] public required JsonElement Value { get; init; }
    private List<ProfileSecretProjectionDocument>? _secretProjections;
    public List<ProfileSecretProjectionDocument> SecretProjections { get => _secretProjections ??= []; init => _secretProjections = value; }
}
public sealed class ProfileSecretProjectionDocument
{
    [JsonRequired] public required string Path { get; init; }
    [JsonRequired] public required string Format { get; init; }
    [JsonRequired] public required ProfileNamedSecretDocument SecretRef { get; init; }
}
public sealed class ProfileNamedSecretDocument
{
    [JsonRequired] public required string Name { get; init; }
    [JsonRequired, JsonConverter(typeof(ProfileRevisionConverter))] public required int Version { get; init; }
}
public sealed class ProfileRuleDocument
{
    [JsonRequired] public required string Slot { get; init; }
    [JsonRequired] public required string RendererRef { get; init; }
    private JsonElement _options;
    public JsonElement Options { get => _options.ValueKind == JsonValueKind.Undefined ? JsonDefaults.EmptyObject : _options; init => _options = value; }
    [JsonRequired] public required List<ProfileProjectionDocument> DocumentProjections { get; init; }
}
public sealed class ProfileProjectionDocument
{
    [JsonRequired] public required string DocumentRef { get; init; }
    [JsonRequired] public required string Path { get; init; }
}
public sealed class ConfigurationProfileAssignmentDocument
{
    [JsonRequired] public required string ApiVersion { get; init; }
    [JsonRequired] public required string Kind { get; init; }
    [JsonRequired] public required ProfileAssignmentSpec Spec { get; init; }
}
public sealed class ProfileAssignmentSpec
{
    [JsonRequired] public required ProfileTargetDocument Target { get; init; }
    [JsonRequired] public required List<ProfileDecisionDocument> Decisions { get; init; }
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ProfileEnvironmentTargetDocument), "environment")]
[JsonDerivedType(typeof(ProfileApplicationTargetDocument), "application")]
[JsonDerivedType(typeof(ProfileComponentTargetDocument), "component")]
public abstract class ProfileTargetDocument;
public sealed class ProfileEnvironmentTargetDocument : ProfileTargetDocument;
public sealed class ProfileApplicationTargetDocument : ProfileTargetDocument
{
    [JsonRequired] public required string ApplicationId { get; init; }
}
public sealed class ProfileComponentTargetDocument : ProfileTargetDocument
{
    [JsonRequired] public required string ApplicationId { get; init; }
    [JsonRequired] public required string ComponentRef { get; init; }
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ProfileUseDocument), "use")]
[JsonDerivedType(typeof(ProfileDisableDocument), "disable")]
public abstract class ProfileDecisionDocument;
public sealed class ProfileUseDocument : ProfileDecisionDocument
{
    [JsonRequired] public required ProfileReferenceDocument ProfileRef { get; init; }
}
public sealed class ProfileDisableDocument : ProfileDecisionDocument
{
    [JsonRequired] public required string ProfileId { get; init; }
}
public sealed class ProfileReferenceDocument
{
    [JsonRequired] public required string ProfileId { get; init; }
    [JsonRequired, JsonConverter(typeof(ProfileRevisionConverter))] public required int Revision { get; init; }
}
