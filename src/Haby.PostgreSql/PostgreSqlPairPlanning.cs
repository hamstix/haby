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
        """, PostgreSqlConsumerOptions.Schema, new("databaseRef", PostgreSqlDatabaseContract.Id));

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
public sealed class PostgreSqlPairResourceAdmission(ResourceTypeId contract) : IDurableActionAdmissionHandler, IDurableResourcePlanComposer, IDurableResourceReusePolicy, IDurableResourceAdditionPolicy, IDurableResourceRetentionPolicy
{
    public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target,
        ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        TryPair(accepted, inputs, configuration, resource) is { } before &&
        TryPair(target, inputs, configuration, resource) is { } after &&
        before.Database == after.Database && before.Login == after.Login &&
        SameResource(accepted, target, before.Database) && SameResource(accepted, target, before.Login);

    private static bool SameResource(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource) =>
        original.Resources.TryGetValue(resource, out var before) && target.Resources.TryGetValue(resource, out var after) &&
        before.Type == after.Type && before.ProviderRef == after.ProviderRef &&
        before.Properties.Equals(after.Properties) && before.DeletionPolicy == after.DeletionPolicy;

    public bool CanRetain(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        CanRetainPair(original, target, resource, inputs, configuration);

    internal static bool CanRetainPair(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration)
    {
        // Validate the dependency closure against its ORIGINAL declaration. The
        // target deliberately does not declare either peer; it must still contain
        // another original compatible pair, never a replacement ownership shape.
        if (TryPair(original, inputs, configuration, resource) is not { } pair ||
            TryPair(target, inputs, configuration) is null ||
            original.Resources[pair.Database].DeletionPolicy != ResourceDeletionPolicy.Retain ||
            original.Resources[pair.Login].DeletionPolicy != ResourceDeletionPolicy.Retain ||
            target.Resources.ContainsKey(pair.Database) || target.Resources.ContainsKey(pair.Login) ||
            !original.Resources.Keys.Where(k => !target.Resources.ContainsKey(k)).ToHashSet().SetEquals([pair.Database, pair.Login]) ||
            target.Resources.Values.Where(r => r.Type == PostgreSqlDatabaseContract.Id).Any(r =>
                PostgreSqlDatabaseCreateValidation.Name(r) == pair.Name)) return false;
        return original.Resources.Where(r => r.Value.Type == PostgreSqlApplicationLoginContract.Id && r.Key != pair.Login).Any(r =>
            TryPair(original, inputs, configuration, r.Key) is { } survivor &&
            TryPair(target, inputs, configuration, r.Key) is { } current &&
            survivor.Database == current.Database && survivor.Login == current.Login &&
            SameResource(original, target, survivor.Database) && SameResource(original, target, survivor.Login));
    }

    private static ProviderAdapterKey Provider => new("postgresql/v1", "haby.postgresql.connection/v1", PostgreSqlDatabaseCheckpointCapability.ImplementationId);
    public DurableActionExecutionKey Key { get; } = new(new(contract, Provider), OperationContractId.Create(
        contract == PostgreSqlDatabaseContract.Id ? "postgresql.database.create/v1alpha1" : "postgresql.application-login.create/v1alpha1"));

    public bool Validate(NormalizedResource resource, DurablePlanningInputs inputs) => false;
    public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) =>
        throw new InvalidDataException("Pair context required.");

    public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        TryPair(spec, inputs, configuration, resource) is { } pair && (resource == pair.Database || resource == pair.Login) &&
        spec.Resources.TryGetValue(resource, out var declared) && declared.Type == Key.Admission.ResourceContract;

    internal static bool SupportsShape(NormalizedManifestSpec spec, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        TryPair(spec, inputs, configuration) is not null;

    internal static bool IsIndependentDatabase(NormalizedManifestSpec spec, ManifestLocalId resource) =>
        spec.Resources.TryGetValue(resource, out var declared) && PostgreSqlDatabaseCreateValidation.Name(declared) is not null &&
        spec.Resources.Values.Where(r => r.Type == PostgreSqlApplicationLoginContract.Id).All(r =>
            PostgreSqlApplicationLoginContract.Parse(r.Properties) is { } p && p.DatabaseRef != resource.Value) &&
        spec.Components.Values.SelectMany(c => c.Bindings.Values).All(b =>
            b.Target is NormalizedLocalResourceTarget t && t.ResourceRef != resource);

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
    private static Pair? TryPair(NormalizedManifestSpec spec, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, ManifestLocalId? resource = null)
    {
        if (configuration.ProviderKindId != Provider.ProviderKind || configuration.ShapeId != Provider.ConfigurationContract ||
            configuration.ImplementationId != Provider.ImplementationId || inputs.ImplementationId != Provider.ImplementationId ||
            configuration.PayloadFormatVersion != 1) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize(configuration.PayloadJson, PostgreSqlConfigurationJsonContext.Default.PostgreSqlConfigurationSnapshot);
            return snapshot is null || snapshot.ImplementationId != inputs.ImplementationId ? null : TryShape(spec, inputs, snapshot.AdministratorUserName, resource);
        }
        catch (JsonException) { return null; }
    }

    private static Pair? TryShape(NormalizedManifestSpec spec, DurablePlanningInputs inputs, string administrator, ManifestLocalId? resource = null)
    {
        if (!HasSingleOwner(spec) || spec.Resources.Count < 2 || spec.Exports.Count != 0 || spec.Parameters.Count != 0 ||
            inputs.ImplementationId != Provider.ImplementationId || string.IsNullOrWhiteSpace(administrator) ||
            spec.Resources.Values.Any(r => r.ProviderRef != inputs.Alias)) return null;
        var databases = spec.Resources.Where(r => r.Value.Type == PostgreSqlDatabaseContract.Id).ToArray();
        var logins = spec.Resources.Where(r => r.Value.Type == PostgreSqlApplicationLoginContract.Id).ToArray();
        if (logins.Length == 0 || databases.Length + logins.Length != spec.Resources.Count ||
            databases.Any(r => PostgreSqlDatabaseCreateValidation.Name(r.Value) is null) ||
            databases.Select(r => PostgreSqlDatabaseCreateValidation.Name(r.Value)).Distinct(StringComparer.Ordinal).Count() != databases.Length) return null;
        var pairs = new List<Pair>();
        foreach (var login in logins)
        {
            if (PostgreSqlApplicationLoginContract.Parse(login.Value.Properties) is not { } properties) return null;
            var database = databases.SingleOrDefault(r => r.Key.Value == properties.DatabaseRef);
            if (database.Key is null || PostgreSqlDatabaseCreateValidation.Name(database.Value) is not { } name) return null;
            pairs.Add(new(database.Key, login.Key, name, properties, administrator));
        }
        foreach (var component in spec.Components.Values)
            if (component.Workloads.Count != 0 || component.Bindings.Values.Any(b =>
                b.Target is not NormalizedLocalResourceTarget target || !pairs.Any(p => p.Login == target.ResourceRef))) return null;
        return resource is null ? pairs[0] : pairs.SingleOrDefault(p => p.Database == resource || p.Login == resource);
    }

    public bool CanAdd(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        CanAddPair(accepted, target, resource, inputs, configuration);

    internal static bool CanAddPair(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration)
    {
        var added = target.Resources.Keys.Where(k => !accepted.Resources.ContainsKey(k)).ToArray();
        if (added.Length != 2 || target.Resources.Count != accepted.Resources.Count + 2 ||
            !accepted.Resources.Keys.All(target.Resources.ContainsKey) || TryPair(accepted, inputs, configuration) is null ||
            TryPair(target, inputs, configuration, resource) is not { } pair) return false;
        return added.Contains(pair.Database) && added.Contains(pair.Login);
    }

    internal static PlanDescriptionStatus DescribeGraph(NormalizedManifestSpec spec, DurablePlanningInputs inputs, DurableOperationGraph? graph)
    {
        if (graph is null || graph.Nodes.Count != 5) return PlanDescriptionStatus.UnsupportedShape;
        var candidates = graph.Nodes.Where(n => n.Action == PostgreSqlDatabaseRestrictAction.RegistrationKey).Take(2).ToArray();
        if (candidates.Length != 1) return PlanDescriptionStatus.UnsupportedShape;
        var restrict = candidates[0];
        if (restrict.Input.Format != "haby.postgresql.database-restrict/v1") return PlanDescriptionStatus.UnsupportedShape;
        try
        {
            var frozen = JsonSerializer.Deserialize(restrict.Input.Json, PostgreSqlPairJsonContext.Default.PostgreSqlDatabaseRestrictInput);
            if (frozen is null) return PlanDescriptionStatus.InputCorrupt;
            var pair = TryShape(spec, inputs, frozen.ExpectedOwnerUserName, restrict.ResourceId);
            if (pair is null) return PlanDescriptionStatus.UnsupportedShape;
            var expected = spec.Resources.SelectMany(r => new PostgreSqlPairResourceAdmission(r.Value.Type).Plan(r.Key, inputs, pair));
            return new DurableOperationGraph(expected).Equals(graph) ? PlanDescriptionStatus.OperationGraph : PlanDescriptionStatus.PinnedInputMismatch;
        }
        catch (JsonException) { return PlanDescriptionStatus.InputCorrupt; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException) { return PlanDescriptionStatus.PinnedInputMismatch; }
    }

    public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource,
        DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
        Plan(resource, inputs, TryPair(spec, inputs, configuration, resource) ?? throw new InvalidDataException("Unsupported PostgreSQL pair."));

    private IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resource, DurablePlanningInputs inputs, Pair pair)
    {
        var references = new DurableActionInputReferences(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, inputs.ImplementationId);
        DurableOperationNode Node<T>(string id, string action, string format, T input, JsonTypeInfo<T> metadata,
            string[] dependencies, params OperationOutputRefDocument[] outputs) =>
            new(OperationId.Create(id), resource, new(Key.Admission, OperationContractId.Create(action)), references,
                new(format, JsonSerializer.Serialize(input, metadata)), dependencies.Select(OperationId.Create).ToArray())
            { OutputReferences = outputs.Select(o => o.ToReference()).ToArray() };
        var dbCreate = pair.Database.Value + "-create";
        var dbRestrict = pair.Database.Value + "-restrict";
        var loginMaterial = pair.Login.Value + "-material";
        var loginCreate = pair.Login.Value + "-create";
        var loginAccess = pair.Login.Value + "-access";
        var database = new OperationOutputRefDocument(dbCreate, PostgreSqlApplicationLoginContract.DatabaseOutput, "database");
        var password = new OperationOutputRefDocument(loginMaterial, GeneratedCredentialContracts.OutputContract, "password");
        var role = new OperationOutputRefDocument(loginCreate, PostgreSqlApplicationLoginContract.RoleOutput, "role");
        if (resource == pair.Database)
            return [Node(dbCreate, "postgresql.database.create/v1alpha1", PostgreSqlDatabaseCreateAction.Format,
                new PostgreSqlDatabaseActionInput(pair.Name), PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput, []),
                Node(dbRestrict, "postgresql.database.restrict/v1alpha1", "haby.postgresql.database-restrict/v1",
                    new PostgreSqlDatabaseRestrictInput(pair.Name, database, pair.Administrator), PostgreSqlPairJsonContext.Default.PostgreSqlDatabaseRestrictInput,
                    [dbCreate], database)];
        var owner = new ManagedResourceKey(inputs.Environment, inputs.Revision.ApplicationRecordId, pair.Login);
        var material = new DurableOperationNode(OperationId.Create(loginMaterial), pair.Login,
            new(Key.Admission, OperationContractId.Create(GeneratedCredentialContracts.Action)), references,
            GeneratedCredentialInputCodec.Encode(new(owner, GeneratedCredentialSlot.ApplicationPassword, GeneratedCredentialSlot.RandomPasswordV1)),
            [OperationId.Create(dbRestrict)]);
        return [material,
            Node(loginCreate, "postgresql.application-login.create/v1alpha1", "haby.postgresql.application-login-create/v1",
                new PostgreSqlLoginCreateInput(new(owner.EnvironmentRecordId.ToString(), owner.ApplicationRecordId.ToString(), owner.ResourceLocalId.Value),
                    pair.Name, database, RoleName(owner), password), PostgreSqlPairJsonContext.Default.PostgreSqlLoginCreateInput,
                    [dbCreate, dbRestrict, loginMaterial], database, password),
            Node(loginAccess, "postgresql.application-login.configure-access/v1alpha1", "haby.postgresql.application-login-configure-access/v1",
                new PostgreSqlLoginAccessInput(pair.Name, database, pair.Properties.Schema, pair.Properties.PrivilegePolicy, role, password),
                PostgreSqlPairJsonContext.Default.PostgreSqlLoginAccessInput, [dbCreate, dbRestrict, loginCreate, loginMaterial], database, role, password)];
    }

    public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
        PreparedProviderConfiguration configuration, DurableOperationGraph graph)
    {
        // Initial installation remains one pair. Updates create exactly one complete
        // independent pair; every other declared Resource has explicit reuse.
        if (graph.Baseline is null && spec.Resources.Count != 2) return false;
        if (TryPair(spec, inputs, configuration, resource) is not { } pair ||
            graph.ReusedResources.Any(r => r.Resource == pair.Database || r.Resource == pair.Login) ||
            graph.Nodes.Count != 5 || !graph.Nodes.Select(n => n.ResourceId).ToHashSet().SetEquals([pair.Database, pair.Login]) ||
            !spec.Resources.Keys.ToHashSet().SetEquals(graph.Nodes.Select(n => n.ResourceId).Concat(graph.ReusedResources.Select(r => r.Resource)))) return false;
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
