using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlPublicSchemaTests
{
    [Fact]
    public void Explicit_public_schema_is_accepted_by_the_current_login_contract()
    {
        var properties = PostgreSqlApplicationLoginContract.ParseJson("""
            {"databaseRef":"db","schema":"public","privilegePolicy":"database-owner/v1"}
            """);
        Assert.NotNull(properties);
        Assert.Equal("public", properties.Schema);
    }
    [Theory]
    [InlineData("PostgreSqlApplicationLoginTarget.cs")]
    [InlineData("PostgreSqlApplicationAccessTarget.cs")]
    [InlineData("PostgreSqlDatabaseCreateTarget.cs")]
    [InlineData("PostgreSqlDatabaseRestrictTarget.cs")]
    public void Provisioning_admission_cannot_use_an_exclusive_version_allowlist_or_throwing_optional_role_lookup(string file)
    {
        // This source boundary protects the accepted admission policy. Real fixture
        // tests separately prove compatible state and refusal of foreign owner/ACLs.
        var source = File.ReadAllText(Path.Combine(Haby.Tests.TestPaths.SourceRoot, "src", "Haby.PostgreSql", file));
        Assert.DoesNotContain("server_version_num", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'pg_database_owner'::pg_catalog.regrole", source, StringComparison.Ordinal);
    }
}
