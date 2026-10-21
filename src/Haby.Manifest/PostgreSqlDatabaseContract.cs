using Haby.Domain;

namespace Haby.Manifest;

/// <summary>The portable checkpoint contract, independent of its implementation.</summary>
public static class PostgreSqlDatabaseContract
{
    public static ResourceTypeId Id { get; } = ResourceTypeId.Create("postgresql.database/v1alpha1");

    public static ResourceTypeDefinition Definition { get; } = new(Id,
        """{"type":"object","required":["name"],"properties":{"name":{"type":"string","minLength":1,"maxLength":63,"pattern":"^[a-z][a-z0-9_]*$"}},"additionalProperties":false}""");
}
