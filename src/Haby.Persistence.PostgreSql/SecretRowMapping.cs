using Haby.Domain;

namespace Haby.Persistence.PostgreSql;

/// <summary>Explicit typed-to-column mapping; no polymorphic JSON or reflection.</summary>
internal static class SecretRowMapping
{
    internal static SecretRow Create(SecretDefinition definition, Guid id)
    {
        if (!definition.IsSupported) throw new ArgumentException("Unsupported secret owner or purpose.");
        var resource = definition.Owner as SecretOwner.Resource;
        return new SecretRow
        {
            Id = id,
            EnvironmentRecordId = Guid.Parse(definition.Environment.ToString()),
            Owner = resource is null ? "Environment" : "Resource",
            ApplicationRecordId = resource is null ? null : Guid.Parse(resource.Key.ApplicationRecordId.ToString()),
            ResourceLocalId = resource?.Key.ResourceLocalId.Value,
            Purpose = definition.Purpose switch
            {
                SecretPurpose.ProviderAdministrator => "ProviderAdministrator",
                SecretPurpose.ApplicationLogin => "ApplicationLogin",
                _ => throw new ArgumentException("Unsupported secret purpose.")
            },
            Sensitivity = "Credential",
            ContentType = "application/octet-stream",
            StorageBackend = "PostgreSqlPlaintext",
            OwnershipPolicy = "Managed"
        };
    }

    internal static bool IsAdministrative(SecretRow row) => row.Owner == "Environment" &&
        row.Purpose == "ProviderAdministrator" && row.ApplicationRecordId is null && row.ResourceLocalId is null &&
        row.Sensitivity == "Credential" && row.ContentType == "application/octet-stream" &&
        row.OwnershipPolicy == "Managed" && row.StorageBackend == "PostgreSqlPlaintext";

    internal static bool Matches(SecretDefinition definition, SecretRow row)
    {
        if (!definition.IsSupported) return false;
        var expected = Create(definition, row.Id);
        return row.EnvironmentRecordId == expected.EnvironmentRecordId && row.Owner == expected.Owner &&
            row.ApplicationRecordId == expected.ApplicationRecordId && row.ResourceLocalId == expected.ResourceLocalId &&
            row.Purpose == expected.Purpose && row.Sensitivity == expected.Sensitivity && row.ContentType == expected.ContentType &&
            row.StorageBackend == expected.StorageBackend && row.OwnershipPolicy == expected.OwnershipPolicy;
    }
}
