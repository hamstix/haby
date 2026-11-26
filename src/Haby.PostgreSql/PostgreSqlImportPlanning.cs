using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.PostgreSql;

/// <summary>Exact observed identities supplied through the separate import intent. No credential material.</summary>
public sealed record PostgreSqlExistingPairInput(string DatabaseResource, string LoginResource, string DatabaseName,
    uint DatabaseOid, string LoginName, uint LoginOid, string Schema, uint SchemaOid, uint PublicSchemaOid);
internal sealed record PostgreSqlImportActionInput(PostgreSqlExistingPairInput Pair, Guid Staged,
    string Secret, string Version);

public static class PostgreSqlImportContracts
{
    public const string InputFormat = "haby.postgresql.existing-pair/v1";
    internal const string ActionFormat = "haby.postgresql.import-pair-action/v1";
    public static DurableActionExecutionKey Database { get; } = new(PostgreSqlDatabaseCreateAction.RegistrationKey.Admission with
    { Mode = ResourceAcquisitionMode.ImportUseAndRetain }, OperationContractId.Create("postgresql.database.import/v1"));
    public static DurableActionExecutionKey Login { get; } = new(Database.Admission with { ResourceContract = PostgreSqlApplicationLoginContract.Id },
        OperationContractId.Create("postgresql.application-login.import/v1"));
    public static DurableActionPayload Encode(PostgreSqlExistingPairInput input) => new(InputFormat,
        JsonSerializer.Serialize(input, PostgreSqlImportJsonContext.Default.PostgreSqlExistingPairInput));

    internal static PostgreSqlExistingPairInput? Decode(DurableActionPayload payload)
    {
        if (payload.Format == PostgreSqlImportPreparer.PreparedFormat) return PostgreSqlImportPreparer.Decode(payload)?.Pair;
        if (payload.Format != InputFormat) return null;
        try
        {
            var value = JsonSerializer.Deserialize(payload.Json, PostgreSqlImportJsonContext.Default.PostgreSqlExistingPairInput);
            return value is not null && value.DatabaseOid != 0 && value.LoginOid != 0 && value.SchemaOid != 0 && value.PublicSchemaOid != 0 &&
                value.DatabaseResource != value.LoginResource && PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(value.DatabaseName) &&
                PostgreSqlResourceNaming.ValidExistingRoleName(value.LoginName) && PostgreSqlApplicationLoginContract.ValidSchema(value.Schema) &&
                ManifestLocalId.Create(value.DatabaseResource) is not null && ManifestLocalId.Create(value.LoginResource) is not null ? value : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal static PostgreSqlImportActionInput? Decode(DurableOperationNode node)
    {
        if ((node.Action != Database && node.Action != Login) || node.Input.Format != ActionFormat || node.Dependencies.Count != 0 ||
            node.OutputReferences.Count != 0) return null;
        try
        {
            var input = JsonSerializer.Deserialize(node.Input.Json, PostgreSqlImportJsonContext.Default.PostgreSqlImportActionInput);
            if (input is null || Decode(Encode(input.Pair)) is null || input.Staged == Guid.Empty) return null;
            _ = SecretId.Parse(input.Secret); _ = SecretVersionId.Parse(input.Version);
            return node.Action == Database && node.ResourceId.Value == input.Pair.DatabaseResource && node.Id.Value == input.Pair.DatabaseResource + "-import" ||
                node.Action == Login && node.ResourceId.Value == input.Pair.LoginResource && node.Id.Value == input.Pair.LoginResource + "-access" ? input : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal static SecretRef Reference(PostgreSqlImportActionInput input) => new(SecretId.Parse(input.Secret), SecretVersionId.Parse(input.Version));
}

public sealed class PostgreSqlImportAdmission(ResourceTypeId type) : IDurableActionAdmissionHandler, IDurableResourcePlanComposer,
    IDurableResourceReusePolicy, IDurableResourceRetentionPolicy, IDurableResourceNamingCompatibility, IResourceNamingPolicyHandler
{
    public DurableActionExecutionKey Key => type == PostgreSqlDatabaseContract.Id ? PostgreSqlImportContracts.Database : PostgreSqlImportContracts.Login;
    public bool SupportsNaming(ResourceNamingPolicy policy) => PostgreSqlResourceNaming.Supports(policy);
    public bool SameNames(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs original,
        DurablePlanningInputs proposed, PreparedProviderConfiguration configuration) =>
        original.Providers.Resource(resource).ResourceNaming() == proposed.Providers.Resource(resource).ResourceNaming();
    public bool Validate(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) => false;
    public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) =>
        throw new InvalidDataException("Complete pair composition is required.");

    internal static PostgreSqlImportActionInput? Prepare(NormalizedManifestSpec spec, DurablePlanningInputs inputs)
    {
        if (inputs.Import?.Resources is not { Count: 2 } requests || spec.Resources.Count != 2) return null;
        var pair = PostgreSqlImportContracts.Decode(requests[0].Input);
        if (pair is null || requests.Any(x => PostgreSqlImportContracts.Decode(x.Input) != pair)) return null;
        var database = ManifestLocalId.Create(pair.DatabaseResource); var login = ManifestLocalId.Create(pair.LoginResource);
        if (!Shape(spec, database, login) || !spec.Resources.Keys.ToHashSet().SetEquals(requests.Select(x => x.Resource)) ||
            requests.Any(x => !PostgreSqlImportPreparer.Matches(x.Input, inputs, x.Resource)) ||
            PostgreSqlApplicationLoginContract.Parse(spec.Resources[login].Properties)?.Schema != pair.Schema ||
            requests.Single(x => x.Resource == database).Credential is not null || requests.Single(x => x.Resource == login).Credential is not { } credential) return null;
        return new(pair, credential.Id, credential.Reference.SecretId.ToString(), credential.Reference.SecretVersionId.ToString());
    }

    private static bool Shape(NormalizedManifestSpec spec, ManifestLocalId database, ManifestLocalId login) =>
        spec.Resources.Count == 2 && spec.Resources.TryGetValue(database, out var db) && db.Type == PostgreSqlDatabaseContract.Id &&
        PostgreSqlDatabaseCreateValidation.Name(db) is not null && spec.Resources.TryGetValue(login, out var role) &&
        role.Type == PostgreSqlApplicationLoginContract.Id && PostgreSqlApplicationLoginContract.Parse(role.Properties) is { } properties &&
        properties.DatabaseRef == database.Value && db.ProviderRef == role.ProviderRef;

    public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration) => spec.Resources.TryGetValue(resource, out var value) && value.Type == type &&
        configuration.ProviderKindId == PostgreSqlProviderConfigurationCodec.Kind && Prepare(spec, inputs) is not null;

    public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration)
    {
        var input = Prepare(spec, inputs) ?? throw new InvalidDataException("Unsupported existing pair import.");
        var provider = inputs.Providers.Resource(resource);
        return [new(OperationId.Create(resource.Value + (type == PostgreSqlDatabaseContract.Id ? "-import" : "-access")), resource, Key,
            new(provider.Environment, provider.Instance, provider.Configuration, provider.Implementation.Value) { Naming = provider.ResourceNaming() },
            new(PostgreSqlImportContracts.ActionFormat, JsonSerializer.Serialize(input, PostgreSqlImportJsonContext.Default.PostgreSqlImportActionInput)), [])
            { RequiresExternalOutput = true, RequiredOutput = type == PostgreSqlDatabaseContract.Id
                ? new(PostgreSqlDatabaseOutputContract.Contract, PostgreSqlDatabaseOutputContract.Key)
                : new(PostgreSqlApplicationConnectionContract.Contract, PostgreSqlApplicationConnectionContract.Key) }];
    }

    public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration, DurableOperationGraph graph)
    {
        var actual = graph.Nodes.Where(x => x.ResourceId == resource).ToArray();
        if (actual.Length != 1) return false;
        var expected = Plan(spec, resource, inputs, configuration).Single();
        var node = actual[0];
        return node.Id == expected.Id && node.Action == expected.Action && node.References == expected.References && node.Input == expected.Input &&
            node.RequiresExternalOutput == expected.RequiresExternalOutput && node.RequiredOutput == expected.RequiredOutput &&
            node.Dependencies.Count == 0 && node.OutputReferences.Count == 0;
    }

    public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        accepted.Resources.Count == 2 && accepted.Resources.All(x => target.Resources.TryGetValue(x.Key, out var value) &&
            x.Value.Type == value.Type && x.Value.ProviderRef == value.ProviderRef && x.Value.DeletionPolicy == value.DeletionPolicy && x.Value.Properties.Equals(value.Properties));

    public bool CanRetain(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => original.Resources.Count == 2 && target.Resources.Count == 0 &&
        original.Resources.Values.All(x => x.DeletionPolicy == ResourceDeletionPolicy.Retain);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PostgreSqlExistingPairInput))]
[JsonSerializable(typeof(PostgreSqlImportActionInput))]
internal partial class PostgreSqlImportJsonContext : JsonSerializerContext;
