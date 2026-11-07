using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.PostgreSql;

/// <summary>Portable login metadata. Registering it does not register planning or external effects.</summary>
public static class PostgreSqlApplicationLoginContract
{
    public static ResourceTypeId Id { get; } = ResourceTypeId.Create("postgresql.application-login/v1alpha1");
    public const string Policy = "database-owner/v1";
    public const string DatabaseOutput = "postgresql.database-output/v1alpha1";
    public const string RoleOutput = "postgresql.role-evidence/v1";
    public const string ConnectionOutput = "postgresql.application-login-output/v1alpha1";
    public static ResourceTypeDefinition Definition { get; } = new(Id,
        """
        {"type":"object","required":["databaseRef","schema","privilegePolicy"],"additionalProperties":false,
         "properties":{"databaseRef":{"type":"string","pattern":"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$","maxLength":63},
         "schema":{"type":"string","pattern":"^[a-z][a-z0-9_]{0,62}$","not":{"anyOf":[{"pattern":"^pg_"},{"enum":["public","information_schema"]}]}},
         "privilegePolicy":{"const":"database-owner/v1"}}}
        """, PostgreSqlConsumerOptions.Schema);

    public static PostgreSqlApplicationLoginProperties? Parse(ConfigurationObject properties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in properties.Members)
            {
                if (member.Value is not ConfigurationString value) return null;
                writer.WriteString(member.Key, value.Value);
            }
            writer.WriteEndObject();
        }
        return ParseJson(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    public static PostgreSqlApplicationLoginProperties? ParseJson(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize(json, PostgreSqlPairJsonContext.Default.PostgreSqlApplicationLoginProperties);
            if (value is null) return null;
            _ = ManifestLocalId.Create(value.DatabaseRef);
            if (!ValidSchema(value.Schema) || value.PrivilegePolicy != Policy) return null;
            return value;
        }
        catch (Exception e) when (e is ArgumentException or JsonException) { return null; }
    }

    internal static bool ValidSchema(string? schema) => schema is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(schema, @"\A[a-z][a-z0-9_]{0,62}\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking) &&
        !schema.StartsWith("pg_", StringComparison.Ordinal) && schema is not ("public" or "information_schema");
}

public sealed record PostgreSqlApplicationLoginProperties(string DatabaseRef, string Schema, string PrivilegePolicy);
public sealed record PostgreSqlPairOwner(string Environment, string Application, string Resource);
public sealed record PostgreSqlDatabaseRestrictInput(string DatabaseName, OperationOutputRefDocument DatabaseSource, string ExpectedOwnerUserName);
public sealed record PostgreSqlLoginCreateInput(PostgreSqlPairOwner Owner, string DatabaseName,
    OperationOutputRefDocument DatabaseSource, string RoleName, OperationOutputRefDocument PasswordSource);
public sealed record PostgreSqlLoginAccessInput(string DatabaseName, OperationOutputRefDocument DatabaseSource,
    string Schema, string Policy, OperationOutputRefDocument RoleSource, OperationOutputRefDocument PasswordSource);
public sealed record OperationOutputRefDocument(string ProducerOperationId, string OutputContract, string OutputKey)
{
    public OperationOutputRef ToReference() => new(OperationId.Create(ProducerOperationId), OutputContract, OutputKey);
}

/// <summary>Pure PostgreSQL pair admission. The Host composes its database half with standalone admission.
/// It owns exactly the nodes of its declared Resource; no executor or success placeholder is provided.</summary>
public sealed class PostgreSqlPairResourceAdmission(ResourceTypeId contract) : IDurableActionAdmissionHandler, IDurableResourcePlanComposer
{
    private static ProviderAdapterKey Provider => new("postgresql/v1", "haby.postgresql.connection/v1", PostgreSqlDatabaseCheckpointCapability.ImplementationId);
    public DurableActionExecutionKey Key { get; } = new(new(contract, Provider), OperationContractId.Create(
        contract == PostgreSqlDatabaseContract.Id ? "postgresql.database.create/v1alpha1" : "postgresql.application-login.create/v1alpha1"));

    public bool Validate(NormalizedResource resource, DurablePlanningInputs inputs) => false;
    public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) =>
        throw new InvalidDataException("Pair context required.");

    public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        TryPair(spec, inputs, configuration) is not null && spec.Resources.TryGetValue(resource, out var declared) &&
        declared.Type == Key.Admission.ResourceContract;

    // A semantic invariant independent of one-pair admission and declaration order.
    internal static bool HasSingleOwner(NormalizedManifestSpec spec)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var login in spec.Resources.Values.Where(r => r.Type == PostgreSqlApplicationLoginContract.Id))
        {
            var properties = PostgreSqlApplicationLoginContract.Parse(login.Properties);
            if (properties is null || !owners.Add(properties.DatabaseRef)) return false;
        }
        return true;
    }

    private sealed record Pair(ManifestLocalId Database, ManifestLocalId Login, string Name,
        PostgreSqlApplicationLoginProperties Properties, string Administrator);
    private static Pair? TryPair(NormalizedManifestSpec spec, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration)
    {
        if (!HasSingleOwner(spec) || spec.Resources.Count != 2 || spec.Exports.Count != 0 || spec.Parameters.Count != 0 ||
            configuration.ProviderKindId != Provider.ProviderKind || configuration.ShapeId != Provider.ConfigurationContract ||
            configuration.ImplementationId != Provider.ImplementationId || inputs.ImplementationId != Provider.ImplementationId ||
            configuration.PayloadFormatVersion != 1 || spec.Resources.Values.Any(r => r.ProviderRef != inputs.Alias)) return null;
        var databases = spec.Resources.Where(r => r.Value.Type == PostgreSqlDatabaseContract.Id).ToArray();
        var logins = spec.Resources.Where(r => r.Value.Type == PostgreSqlApplicationLoginContract.Id).ToArray();
        if (databases.Length != 1 || logins.Length != 1 || PostgreSqlDatabaseCreateValidation.Name(databases[0].Value) is not { } name ||
            PostgreSqlApplicationLoginContract.Parse(logins[0].Value.Properties) is not { } properties ||
            properties.DatabaseRef != databases[0].Key.Value) return null;
        foreach (var component in spec.Components.Values)
            if (component.Workloads.Count != 0 || component.Bindings.Values.Any(b =>
                b.Target is not NormalizedLocalResourceTarget target || target.ResourceRef != logins[0].Key)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize(configuration.PayloadJson, PostgreSqlConfigurationJsonContext.Default.PostgreSqlConfigurationSnapshot);
            return snapshot is null || snapshot.ImplementationId != inputs.ImplementationId || string.IsNullOrWhiteSpace(snapshot.AdministratorUserName)
                ? null : new(databases[0].Key, logins[0].Key, name, properties, snapshot.AdministratorUserName);
        }
        catch (JsonException) { return null; }
    }

    public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration)
    {
        var pair = TryPair(spec, inputs, configuration) ?? throw new InvalidDataException("Unsupported PostgreSQL pair.");
        var references = new DurableActionInputReferences(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, inputs.ImplementationId);
        DurableOperationNode Node<T>(string id, string action, string format, T input, JsonTypeInfo<T> metadata,
            string[] dependencies, params OperationOutputRefDocument[] outputs) =>
            new(OperationId.Create(id), resource, new(Key.Admission, OperationContractId.Create(action)), references,
                new(format, JsonSerializer.Serialize(input, metadata)), dependencies.Select(OperationId.Create).ToArray())
            { OutputReferences = outputs.Select(o => o.ToReference()).ToArray() };
        var database = new OperationOutputRefDocument("db-create", PostgreSqlApplicationLoginContract.DatabaseOutput, "database");
        var password = new OperationOutputRefDocument("app-login-material", GeneratedCredentialContracts.OutputContract, "password");
        var role = new OperationOutputRefDocument("app-login-create", PostgreSqlApplicationLoginContract.RoleOutput, "role");
        if (resource == pair.Database)
            return [Node("db-create", "postgresql.database.create/v1alpha1", PostgreSqlDatabaseCreateAction.Format,
                new PostgreSqlDatabaseActionInput(pair.Name), PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput, []),
                Node("db-restrict", "postgresql.database.restrict/v1alpha1", "haby.postgresql.database-restrict/v1",
                    new PostgreSqlDatabaseRestrictInput(pair.Name, database, pair.Administrator), PostgreSqlPairJsonContext.Default.PostgreSqlDatabaseRestrictInput,
                    ["db-create"], database)];
        var owner = new ManagedResourceKey(inputs.Environment, inputs.Revision.ApplicationRecordId, pair.Login);
        var material = new DurableOperationNode(OperationId.Create("app-login-material"), pair.Login,
            new(Key.Admission, OperationContractId.Create(GeneratedCredentialContracts.Action)), references,
            GeneratedCredentialInputCodec.Encode(new(owner, GeneratedCredentialSlot.ApplicationPassword, GeneratedCredentialSlot.RandomPasswordV1)),
            [OperationId.Create("db-restrict")]);
        return [material,
            Node("app-login-create", "postgresql.application-login.create/v1alpha1", "haby.postgresql.application-login-create/v1",
                new PostgreSqlLoginCreateInput(new(owner.EnvironmentRecordId.ToString(), owner.ApplicationRecordId.ToString(), owner.ResourceLocalId.Value),
                    pair.Name, database, RoleName(owner), password), PostgreSqlPairJsonContext.Default.PostgreSqlLoginCreateInput,
                    ["db-create", "db-restrict", "app-login-material"], database, password),
            Node("app-login-access", "postgresql.application-login.configure-access/v1alpha1", "haby.postgresql.application-login-configure-access/v1",
                new PostgreSqlLoginAccessInput(pair.Name, database, pair.Properties.Schema, pair.Properties.PrivilegePolicy, role, password),
                PostgreSqlPairJsonContext.Default.PostgreSqlLoginAccessInput, ["db-create", "db-restrict", "app-login-create", "app-login-material"], database, role, password)];
    }

    public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration, DurableOperationGraph graph)
    {
        // Exact typed current-format nodes, including complete dependency and selector sets.
        // No external existence/name-availability assertion is made by this comparison.
        var expected = Plan(spec, resource, inputs, configuration).OrderBy(n => n.Id.Value, StringComparer.Ordinal).ToArray();
        var actual = graph.Nodes.Where(n => n.ResourceId == resource).ToArray();
        return expected.Length == actual.Length && expected.Zip(actual).All(p =>
            p.First.Id == p.Second.Id && p.First.Action == p.Second.Action && p.First.References == p.Second.References &&
            p.First.Input == p.Second.Input && p.First.Dependencies.OrderBy(d => d.Value, StringComparer.Ordinal).SequenceEqual(p.Second.Dependencies) &&
            p.First.OutputReferences.OrderBy(o => o.ProducerOperationId.Value, StringComparer.Ordinal).ThenBy(o => o.OutputContract, StringComparer.Ordinal)
                .ThenBy(o => o.OutputKey, StringComparer.Ordinal).SequenceEqual(p.Second.OutputReferences));
    }

    /// <summary>Canonical owner encoding: compact JSON array [environment UUID D lowercase, application UUID D lowercase, ResourceLocalId].</summary>
    public static string RoleName(ManagedResourceKey owner)
    {
        var json = JsonSerializer.Serialize(new[] { owner.EnvironmentRecordId.ToString().ToLowerInvariant(),
            owner.ApplicationRecordId.ToString().ToLowerInvariant(), owner.ResourceLocalId.Value }, PostgreSqlPairJsonContext.Default.StringArray);
        return "haby_a_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()[..48];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PostgreSqlApplicationLoginProperties))]
[JsonSerializable(typeof(PostgreSqlDatabaseRestrictInput))]
[JsonSerializable(typeof(PostgreSqlLoginCreateInput))]
[JsonSerializable(typeof(PostgreSqlLoginAccessInput))]
[JsonSerializable(typeof(string[]))]
internal partial class PostgreSqlPairJsonContext : JsonSerializerContext;
