using Haby.Domain;

namespace Haby.Application;

/// <summary>Provider-neutral, versioned identity of a declared external target.</summary>
public sealed record ProviderTargetIdentity
{
    public ProviderTargetIdentity(int formatVersion, string value)
    {
        if (formatVersion <= 0) throw new ArgumentOutOfRangeException(nameof(formatVersion));
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            throw new ArgumentException("Invalid Provider target identity.", nameof(value));
        FormatVersion = formatVersion;
        Value = value;
    }

    public int FormatVersion { get; }
    public string Value { get; }
}

/// <summary>One exact SecretRef extracted from a typed Provider configuration.</summary>
public sealed record ProviderConfigurationSecretReference(string Slot, SecretRef Reference);

/// <summary>Validated, non-secret snapshot prepared by a registered Provider codec.</summary>
public sealed record PreparedProviderConfiguration(string ProviderKindId, ProviderTargetIdentity TargetIdentity,
    string ImplementationId, string ShapeId, int PayloadFormatVersion, string PayloadJson,
    IReadOnlyList<ProviderConfigurationSecretReference> SecretReferences);

/// <summary>Build-time registered typed codec. Payloads never contain secret material.</summary>
/// <remarks>
/// A persisted kind, shape and format triple is a frozen byte contract: exact reads re-encode and compare
/// the JSON text. The current registry permits one codec per configuration CLR type. Changing its serialized
/// bytes requires an explicit compatibility reader or migration before historical revisions can be used.
/// </remarks>
public interface IProviderConfigurationCodec
{
    Type ConfigurationType { get; }
    string ProviderKindId { get; }
    string ShapeId { get; }
    int PayloadFormatVersion { get; }
    PreparedProviderConfiguration Encode(object configuration);
    object Decode(string payloadJson);
}

/// <summary>Typed shape implementation with a narrow heterogeneous catalog bridge.</summary>
public abstract class ProviderConfigurationCodec<TConfiguration> : IProviderConfigurationCodec
    where TConfiguration : class
{
    public Type ConfigurationType => typeof(TConfiguration);
    public abstract string ProviderKindId { get; }
    public abstract string ShapeId { get; }
    public abstract int PayloadFormatVersion { get; }
    public abstract PreparedProviderConfiguration Encode(TConfiguration configuration);
    public abstract TConfiguration DecodeTyped(string payloadJson);

    PreparedProviderConfiguration IProviderConfigurationCodec.Encode(object configuration) =>
        configuration is TConfiguration typed ? Encode(typed) :
        throw new ArgumentException("Unsupported Provider configuration type.", nameof(configuration));
    object IProviderConfigurationCodec.Decode(string payloadJson) => DecodeTyped(payloadJson);
}

public sealed record ProviderConfigurationRevision<TConfiguration>(ProviderInstanceId InstanceId,
    ProviderConfigurationRevisionNumber RevisionNumber, EnvironmentRecordId EnvironmentId,
    TConfiguration Configuration);

/// <summary>Internal asynchronous store; codec validation precedes persistence and exact reads.</summary>
public interface IProviderConfigurationStore
{
    ValueTask<ProviderInstanceId> CreateInstanceAsync<TConfiguration>(EnvironmentRecordId environment,
        TConfiguration configuration, CancellationToken cancellationToken) where TConfiguration : class;
    ValueTask AddRevisionAsync<TConfiguration>(EnvironmentRecordId environment, ProviderInstanceId instanceId,
        ProviderConfigurationRevisionNumber revision, TConfiguration configuration,
        CancellationToken cancellationToken) where TConfiguration : class;
    ValueTask<ProviderConfigurationRevision<TConfiguration>?> FindRevisionAsync<TConfiguration>(
        EnvironmentRecordId environment, ProviderInstanceId instanceId,
        ProviderConfigurationRevisionNumber revision, CancellationToken cancellationToken) where TConfiguration : class;
}
