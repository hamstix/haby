using Haby.Application;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlDatabaseOutputTests
{
    [Fact]
    public void Database_evidence_has_exact_adapter_owned_contract_and_uint32_roundtrip()
    {
        var evidence = new PostgreSqlDatabaseOutput("dedicated_database", uint.MaxValue);
        var encoded = PostgreSqlDatabaseOutputContract.Encode(evidence);
        Assert.Equal("postgresql.database-output/v1alpha1", encoded.Contract);
        Assert.Equal("database", encoded.Key);
        Assert.Equal("{\"databaseName\":\"dedicated_database\",\"databaseOid\":4294967295}", encoded.Payload);
        Assert.Equal(evidence, PostgreSqlDatabaseOutputContract.Decode(encoded));
        Assert.Null(PostgreSqlDatabaseOutputContract.Decode(encoded with { Contract = "other/v1" }));
        Assert.Null(PostgreSqlDatabaseOutputContract.Decode(encoded with { Key = "other" }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"databaseName\":\"app\"}")]
    [InlineData("{\"databaseOid\":42}")]
    [InlineData("{\"databaseName\":null,\"databaseOid\":42}")]
    [InlineData("{\"databaseName\":\"Bad-Db\",\"databaseOid\":42}")]
    [InlineData("{\"databaseName\":\"app\",\"databaseOid\":0}")]
    [InlineData("{\"databaseName\":\"app\",\"databaseOid\":-1}")]
    [InlineData("{\"databaseName\":\"app\",\"databaseOid\":4294967296}")]
    [InlineData("{\"databaseName\":\"app\",\"databaseOid\":\"42\"}")]
    [InlineData("{\"databaseName\":\"app\",\"databaseOid\":42,\"other\":true}")]
    public void Missing_unknown_or_invalid_evidence_is_refused(string payload) =>
        Assert.Null(PostgreSqlDatabaseOutputContract.Decode(new(PostgreSqlDatabaseOutputContract.Contract, "database", payload)));
}
