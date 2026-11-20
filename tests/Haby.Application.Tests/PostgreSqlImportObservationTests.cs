using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlImportObservationTests
{
    [Fact]
    public void Existing_effective_permissions_qualify_without_claiming_schema_ownership_or_private_public_acl()
    {
        var (input, observed) = Example();
        Assert.Equal(PostgreSqlImportVerificationStatus.Confirmed, PostgreSqlImportObservation.Verify(input, observed));
        var payload = PostgreSqlImportObservation.Encode(observed);
        Assert.Equal(observed, PostgreSqlImportObservation.Decode(payload));
        Assert.Equal("postgres", PostgreSqlImportObservation.Decode(payload)!.SchemaOwner);
        Assert.Equal("{=UC/postgres,postgres=UC/postgres}", PostgreSqlImportObservation.Decode(payload)!.SchemaAcl);
        Assert.Equal(PostgreSqlImportVerificationStatus.Confirmed, PostgreSqlImportObservation.Verify(input,
            observed with { SchemaOwner = "separate_owner", SchemaOwnerOid = 92, PublicSchemaOwner = "separate_owner", PublicSchemaOwnerOid = 92 }));
        Assert.Null(PostgreSqlImportObservation.Decode(payload with { Json = payload.Json[..^1] + ",\"password\":\"forbidden\"}" }));
    }

    [Fact]
    public void Identity_drift_unsafe_login_or_missing_effective_permission_refuses_without_normalization()
    {
        var (input, observed) = Example();
        foreach (var invalid in new[]
        {
            observed with { DatabaseOid = 91 }, observed with { LoginOid = 91 }, observed with { SchemaOid = 91 },
            observed with { PublicSchemaOid = 91 }, observed with { DatabaseOwner = "separate_owner", DatabaseOwnerOid = 91 },
            observed with { CanLogin = false }, observed with { SuperUser = true }, observed with { CreateDatabase = true },
            observed with { CreateRole = true }, observed with { Replication = true }, observed with { BypassRowSecurity = true },
            observed with { HasRoleMembership = true }, observed with { CredentialAuthenticated = false },
            observed with { EffectiveConnect = false }, observed with { EffectiveDatabaseCreate = false },
            observed with { EffectiveSchemaUsage = false }, observed with { EffectiveSchemaCreate = false }, observed with { ApplicationTls = true }
        }) Assert.Equal(PostgreSqlImportVerificationStatus.Conflict, PostgreSqlImportObservation.Verify(input, invalid));
    }

    private static (PostgreSqlExistingPairInput, PostgreSqlExistingPairObservation) Example() =>
        (new("db", "login", "existing_database", 40, "app_login", 41, "public", 42, 42),
         new("existing_database", 40, "app_login", 41, "public", 42, 42, "app_login", 41, "postgres", 10, "postgres", 10,
             null, "{=UC/postgres,postgres=UC/postgres}", "{=UC/postgres,postgres=UC/postgres}",
             true, false, false, false, false, false, false, true, true, true, true, true, false, false));
}
