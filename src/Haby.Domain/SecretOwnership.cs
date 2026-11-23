namespace Haby.Domain;

/// <summary>Stable Resource declaration identity, independent of any Provider or deployment.</summary>
public sealed record ManagedResourceKey(EnvironmentRecordId EnvironmentRecordId,
    ApplicationRecordId ApplicationRecordId, ManifestLocalId ResourceLocalId);

public abstract record SecretOwner
{
    private SecretOwner() { }
    public sealed record Environment(EnvironmentRecordId Id) : SecretOwner;
    public sealed record Resource(ManagedResourceKey Key) : SecretOwner;
}

public enum SecretPurpose { ProviderAdministrator, ApplicationLogin, Configuration, SharedApplicationCredential }

/// <summary>Explicit producer metadata. Supported purposes use Managed Credential octet streams.</summary>
public sealed record SecretDefinition(SecretOwner Owner, SecretPurpose Purpose)
{
    public EnvironmentRecordId Environment => Owner switch
    {
        SecretOwner.Environment owner => owner.Id,
        SecretOwner.Resource owner => owner.Key.EnvironmentRecordId,
        _ => throw new ArgumentException("Invalid secret owner.")
    };

    public bool IsSupported => (Owner, Purpose) switch
    {
        (SecretOwner.Environment { Id: not null }, SecretPurpose.ProviderAdministrator) => true,
        (SecretOwner.Environment { Id: not null }, SecretPurpose.Configuration) => true,
        (SecretOwner.Environment { Id: not null }, SecretPurpose.SharedApplicationCredential) => true,
        (SecretOwner.Resource { Key: { EnvironmentRecordId: not null, ApplicationRecordId: not null, ResourceLocalId: not null } },
            SecretPurpose.ApplicationLogin) => true,
        _ => false
    };
}

/// <summary>The profile is deliberately absent from stable slot identity.</summary>
public sealed record GeneratedCredentialSlot(ManagedResourceKey Owner, string SlotId)
{
    public const string ApplicationPassword = "application-password";
    public const string RandomPasswordV1 = "random-password/v1";
}
