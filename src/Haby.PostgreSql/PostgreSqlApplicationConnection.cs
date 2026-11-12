using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;

namespace Haby.PostgreSql;

/// <summary>Internal connection evidence; password is an immutable reference, never material.</summary>
public sealed record PostgreSqlApplicationConnection(string Host, int Port, string DatabaseName, string Schema, string UserName, SecretRef Password);

public sealed class PostgreSqlApplicationConnectionContract(IProviderConfigurationStore providers, PostgreSqlProviderConfigurationCodec codec,
    IGeneratedCredentialReader generated) : IExternalOperationOutputContract
{
    public const string Contract = "postgresql.application-login-output/v1alpha1";
    public const string Key = "connection";
    public DurableActionExecutionKey Producer => PostgreSqlApplicationAccessAction.RegistrationKey;
    public ExternalOperationOutputIdentity Identity => new(Contract, Key);
    public static ExternalOperationOutput Encode(PostgreSqlApplicationConnection value) => new(Contract, Key,
        JsonSerializer.Serialize(new PostgreSqlConnectionDocument(value.Host, value.Port, value.DatabaseName, value.Schema, value.UserName,
            new(value.Password.SecretId.ToString(), value.Password.SecretVersionId.ToString())), PostgreSqlConnectionJsonContext.Default.PostgreSqlConnectionDocument));
    public static PostgreSqlApplicationConnection? Decode(ExternalOperationOutput output)
    {
        if (output.Contract != Contract || output.Key != Key) return null;
        try
        {
            using var document = JsonDocument.Parse(output.Payload);
            var members = document.RootElement.EnumerateObject().ToArray();
            if (members.Length != 6 || members.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 6 ||
                !document.RootElement.TryGetProperty("password"u8, out var password) || password.ValueKind != JsonValueKind.Object ||
                password.EnumerateObject().Count() != 2 || password.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 2) return null;
            var value = JsonSerializer.Deserialize(output.Payload, PostgreSqlConnectionJsonContext.Default.PostgreSqlConnectionDocument);
            if (value is null || string.IsNullOrWhiteSpace(value.Host) || value.Host.Length > 255 || Uri.CheckHostName(value.Host) == UriHostNameType.Unknown ||
                value.Port is < 1 or > 65535 || value.Password is null || value.DatabaseName is null ||
                !PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(value.DatabaseName) || !PostgreSqlApplicationLoginContract.ValidSchema(value.Schema) ||
                value.UserName is null || !PostgreSqlResourceNaming.ValidRoleName(value.UserName)) return null;
            return new(value.Host, value.Port, value.DatabaseName, value.Schema, value.UserName,
                new(SecretId.Parse(value.Password.SecretId), SecretVersionId.Parse(value.Password.SecretVersionId)));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException) { return null; }
    }
    public bool Validate(DurableOperationNode node, ExternalOperationOutput output) =>
        PostgreSqlApplicationAccessAction.Decode(node) is { } input && Decode(output) is { } value &&
        input.DatabaseName == value.DatabaseName && input.Schema == value.Schema;

    public bool ValidateReuse(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
        IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration prepared,
        IReadOnlyCollection<ProviderResourceReservation> reservations)
    {
        if (!Validate(accepted.Node, output) || !PostgreSqlPairExecution.IsSupported(accepted, prepared)) return false;
        var configuration = codec.DecodeTyped(prepared.PayloadJson);
        var input = PostgreSqlApplicationAccessAction.Decode(accepted.Node)!;
        var selector = input.PasswordSource.ToReference();
        var candidates = sources.Where(x => x.Producer == selector.ProducerOperationId && x.Output.Contract == selector.OutputContract && x.Output.Key == selector.OutputKey).ToArray();
        if (candidates.Length != 1 || GeneratedCredentialOutputCodec.Decode(candidates[0].Output.Contract, candidates[0].Output.Payload) is not { } password) return false;
        var owner = new ManagedResourceKey(accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId, accepted.Node.ResourceId);
        return Decode(output) == new PostgreSqlApplicationConnection(configuration.Host, configuration.Port, input.DatabaseName,
            input.Schema, PostgreSqlResourceNaming.Role(owner, accepted.Provider.ResourceNaming().Policy)!, password.Reference) &&
            reservations.Contains(new(accepted.Provider.Instance, owner.EnvironmentRecordId, owner.ApplicationRecordId,
                owner.ResourceLocalId, PostgreSqlApplicationLoginAction.RoleReservationNamespace, PostgreSqlResourceNaming.Role(owner, accepted.Provider.ResourceNaming().Policy)!));
    }

    public async ValueTask<bool> ValidateAcceptedAsync(DurableOperationKey key, AcceptedOperation accepted,
        ExternalOperationOutput output, CancellationToken cancellationToken)
    {
        if (!Validate(accepted.Node, output)) return false;
        var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(accepted.Inputs.Environment,
            accepted.Provider.Instance, accepted.Provider.Configuration, cancellationToken))?.Configuration;
        if (configuration is null || !PostgreSqlPairExecution.IsSupported(accepted, codec.Encode(configuration))) return false;
        var input = PostgreSqlApplicationAccessAction.Decode(accepted.Node)!;
        var password = await generated.ConsumeAsync(key, input.PasswordSource.ToReference(), cancellationToken);
        if (password is null) return false;
        var owner = new ManagedResourceKey(accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId, accepted.Node.ResourceId);
        return Decode(output) == new PostgreSqlApplicationConnection(configuration.Host, configuration.Port, input.DatabaseName,
            input.Schema, PostgreSqlResourceNaming.Role(owner, accepted.Provider.ResourceNaming().Policy)!, password.Reference);
    }
}

internal sealed record PostgreSqlSecretReferenceDocument(string SecretId, string SecretVersionId);
internal sealed record PostgreSqlConnectionDocument(string Host, int Port, string DatabaseName, string Schema, string UserName, PostgreSqlSecretReferenceDocument Password);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PostgreSqlConnectionDocument))]
internal partial class PostgreSqlConnectionJsonContext : JsonSerializerContext;
