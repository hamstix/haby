using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Npgsql;

namespace Haby.PostgreSql;

public sealed record PostgreSqlImportNames(string DatabaseName, string LoginName, string Schema);
internal sealed record PostgreSqlPreparedImportInput(PostgreSqlExistingPairInput Pair, string Environment,
    string ProviderInstance, long ProviderRevision, string Implementation);

/// <summary>Observes exact existing identities; does not stage or authenticate an application password.</summary>
public sealed class PostgreSqlImportPreparer(IProviderAliasResolver aliases, PostgreSqlImportVerifier verifier) : IResourceImportPreparer
{
    public const string NamesFormat = "haby.postgresql.import-names/v1";
    internal const string PreparedFormat = "haby.postgresql.prepared-existing-pair/v1";
    public string InputFormat => NamesFormat;
    public ImportCredentialSlot CredentialSlot => new("application-password", SecretPurpose.ApplicationLogin);
    public static DurableActionPayload Encode(PostgreSqlImportNames names) => new(NamesFormat,
        JsonSerializer.Serialize(names, PostgreSqlImportPreparationJsonContext.Default.PostgreSqlImportNames));

    public bool MatchesPreparedRequest(ResourceImportPreparationRequest request, OperatorImportRequestBinding binding)
    {
        var names = DecodeNames(request.Input);
        if (names is null || binding.Input.Resources.Count != 2) return false;
        var frozen = Decode(binding.Input.Resources[0].Input);
        if (frozen is null || frozen.Environment != binding.Environment.ToString() ||
            binding.Input.Resources.Any(x => x.Credential is not null || Decode(x.Input) != frozen)) return false;
        var pair = frozen.Pair;
        return pair.DatabaseName == names.DatabaseName && pair.LoginName == names.LoginName && pair.Schema == names.Schema &&
            binding.CredentialResource.Value == pair.LoginResource &&
            binding.Input.Resources.Select(x => x.Resource.Value).ToHashSet(StringComparer.Ordinal)
                .SetEquals([pair.DatabaseResource, pair.LoginResource]);
    }

    private static PostgreSqlImportNames? DecodeNames(DurableActionPayload input)
    {
        if (input.Format != NamesFormat || input.Json.Length > 4096) return null;
        try
        {
            var names = JsonSerializer.Deserialize(input.Json, PostgreSqlImportPreparationJsonContext.Default.PostgreSqlImportNames);
            return names is not null && PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(names.DatabaseName) &&
                PostgreSqlResourceNaming.ValidExistingRoleName(names.LoginName) && PostgreSqlApplicationLoginContract.ValidSchema(names.Schema) ? names : null;
        }
        catch (JsonException) { return null; }
    }

    public async ValueTask<ResourceImportPreparationResult> PrepareAsync(ResourceImportPreparationRequest request,
        NormalizedManifestSpec spec, CancellationToken token)
    {
        if (DecodeNames(request.Input) is not { } names) return new(ResourceImportPreparationStatus.InvalidInput);
        if ((spec.Resources.Count != 2 && request.CredentialSlot != CredentialSlot.Name) || spec.Components.Values.Any(x => x.Workloads.Count > 0))
            return new(ResourceImportPreparationStatus.ImportNotSupported);
        var databases = spec.Resources.Where(x => x.Value.Type == PostgreSqlDatabaseContract.Id).ToArray();
        var logins = spec.Resources.Where(x => x.Value.Type == PostgreSqlApplicationLoginContract.Id).ToArray();
        if (databases.Length != 1 || logins.Length != 1) return new(ResourceImportPreparationStatus.ImportNotSupported);
        var database = databases[0]; var login = logins[0];
        if (PostgreSqlDatabaseCreateValidation.Name(database.Value) is null ||
            PostgreSqlApplicationLoginContract.Parse(login.Value.Properties) is not { } properties ||
            properties.DatabaseRef != database.Key.Value || properties.Schema != names.Schema || database.Value.ProviderRef != login.Value.ProviderRef)
            return new(ResourceImportPreparationStatus.ImportNotSupported);
        var selected = await aliases.ResolveAsync(request.Environment, database.Value.ProviderRef, token);
        if (selected is null) return new(ResourceImportPreparationStatus.ProviderUnavailable);
        var observed = await verifier.PreparePairAsync(request.Environment, selected, database.Key, login.Key, names, token);
        if (observed.Input is null) return new(observed.Status);
        var payload = new DurableActionPayload(PreparedFormat, JsonSerializer.Serialize(observed.Input,
            PostgreSqlImportPreparationJsonContext.Default.PostgreSqlPreparedImportInput));
        return new(ResourceImportPreparationStatus.Prepared, new(request.RequestId, request.Environment, request.Revision, login.Key,
            new([new(database.Key, payload), new(login.Key, payload)])));
    }

    internal static PostgreSqlPreparedImportInput? Decode(DurableActionPayload payload)
    {
        if (payload.Format != PreparedFormat) return null;
        try
        {
            var value = JsonSerializer.Deserialize(payload.Json, PostgreSqlImportPreparationJsonContext.Default.PostgreSqlPreparedImportInput);
            if (value is null || value.ProviderRevision < 1 || string.IsNullOrWhiteSpace(value.Implementation)) return null;
            _ = EnvironmentRecordId.Parse(value.Environment); _ = ProviderInstanceId.Parse(value.ProviderInstance);
            return PostgreSqlImportContracts.Decode(PostgreSqlImportContracts.Encode(value.Pair)) is null ? null : value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
    }

    internal static bool Matches(DurableActionPayload payload, DurablePlanningInputs inputs, ManifestLocalId resource)
    {
        if (payload.Format != PreparedFormat) return payload.Format == PostgreSqlImportContracts.InputFormat;
        var prepared = Decode(payload);
        var provider = inputs.Providers.Resource(resource);
        return prepared is not null && prepared.Environment == inputs.Environment.ToString() &&
            prepared.ProviderInstance == provider.Instance.ToString() && prepared.ProviderRevision == provider.Configuration.Value &&
            prepared.Implementation == provider.Implementation.Value;
    }
}

public sealed partial class PostgreSqlImportVerifier
{
    internal async ValueTask<(ResourceImportPreparationStatus Status, PostgreSqlPreparedImportInput? Input)> PreparePairAsync(
        EnvironmentRecordId environment, PersistedProviderAliasResolution selected, ManifestLocalId database, ManifestLocalId login,
        PostgreSqlImportNames names, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        byte[]? bytes = null;
        NpgsqlConnectionStringBuilder? builder = null;
        try
        {
            var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(environment,
                selected.Binding.InstanceId, selected.Binding.RevisionNumber, token))?.Configuration;
            if (configuration is null || configuration.ImplementationId != selected.ImplementationId)
                return (ResourceImportPreparationStatus.ProviderUnavailable, null);
            using var material = await secrets.ResolveAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator),
                configuration.AdministratorPassword, token);
            if (material is null) return (ResourceImportPreparationStatus.SecretUnavailable, null);
            bytes = material.CopyBytes();
            builder = Builder(configuration, names.DatabaseName, configuration.AdministratorUserName, new UTF8Encoding(false, true).GetString(bytes));
            var input = new PostgreSqlExistingPairInput(database.Value, login.Value, names.DatabaseName, 0, names.LoginName, 0, names.Schema, 0, 0);
            var observed = await ObserveAsync(builder, input, token);
            if (observed is null || observed.AdministratorTls != (configuration.TransportMode == PostgreSqlTransportMode.VerifyFull))
                return (ResourceImportPreparationStatus.Conflict, null);
            input = input with
            {
                DatabaseOid = observed.DatabaseOid,
                LoginOid = observed.LoginOid,
                SchemaOid = observed.SchemaOid,
                PublicSchemaOid = observed.PublicSchemaOid
            };
            return (ResourceImportPreparationStatus.Prepared, new(input, environment.ToString(), selected.Binding.InstanceId.ToString(),
                selected.Binding.RevisionNumber.Value, selected.ImplementationId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (DecoderFallbackException) { return (ResourceImportPreparationStatus.SecretUnavailable, null); }
        catch (PostgresException error) when (error.SqlState.StartsWith("28", StringComparison.Ordinal) ||
            error.SqlState == PostgresErrorCodes.InvalidCatalogName || error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        { return (ResourceImportPreparationStatus.Conflict, null); }
        catch (Exception error) when (error is NpgsqlException or IOException or TimeoutException or OperationCanceledException)
        { return (ResourceImportPreparationStatus.Connectivity, null); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return (ResourceImportPreparationStatus.ProviderUnavailable, null); }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (builder is not null) builder.Password = null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PostgreSqlImportNames))]
[JsonSerializable(typeof(PostgreSqlPreparedImportInput))]
internal partial class PostgreSqlImportPreparationJsonContext : JsonSerializerContext;
