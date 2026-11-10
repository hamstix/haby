using Haby.Application;
using Haby.Domain;

namespace Haby.PostgreSql;

/// <summary>PostgreSQL interpretation of frozen naming policies.</summary>
public static class PostgreSqlResourceNaming
{
    internal static bool ValidRoleName(string? value) => value is not null && !value.StartsWith("pg_", StringComparison.Ordinal) &&
        System.Text.RegularExpressions.Regex.IsMatch(value,
            @"\A(?:haby_a_[a-f0-9]{48}|[a-z][a-z0-9_]{0,23}haby_a_[a-f0-9]{32})\z");
    public static bool SameNames(NormalizedManifestSpec spec, ManifestLocalId resource, ResourceNamingPolicy original, ResourceNamingPolicy proposed)
    {
        if (!Supports(original) || !Supports(proposed)) return false;
        if (spec.Resources[resource].Type == Haby.Manifest.PostgreSqlDatabaseContract.Id)
        {
            var declared = PostgreSqlDatabaseCreateValidation.Name(spec.Resources[resource]);
            return declared is not null && Database(declared, original) == Database(declared, proposed);
        }
        // A generated name always embeds the complete prefix. Equal policies give equal names;
        // changing exact/prefixed mode changes both marker encoding and owner hash budget.
        return original == proposed;
    }
    public const string Algorithm = "postgresql.owner-sha256/v1";
    public static bool Supports(ResourceNamingPolicy policy)
    {
        try { policy.Validate(); }
        catch (InvalidDataException) { return false; }
        return policy.Mode == "exact" || policy.Prefix is { Length: <= 24 } prefix &&
            !prefix.StartsWith("pg_", StringComparison.Ordinal) &&
            System.Text.RegularExpressions.Regex.IsMatch(prefix, @"\A[a-z][a-z0-9_]*\z");
    }
    public static string? Database(string declared, ResourceNamingPolicy policy)
    {
        if (!Supports(policy)) return null;
        if (!PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(declared)) return null;
        var result = (policy.Prefix ?? "") + declared;
        return PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(result) ? result : null;
    }
    // Exact retains 192 hash bits. Prefixed reserves 128 bits and seven marker bytes,
    // leaving at most 24 ASCII bytes for the complete distinguishing prefix.
    public static string? Role(ManagedResourceKey owner, ResourceNamingPolicy policy)
    {
        if (!Supports(policy)) return null;
        var exact = PostgreSqlPairResourceAdmission.RoleName(owner);
        return policy.Mode == "exact" ? exact : policy.Prefix + exact[..39];
    }
}
