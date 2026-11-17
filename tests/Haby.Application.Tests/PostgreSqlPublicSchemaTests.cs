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
}
