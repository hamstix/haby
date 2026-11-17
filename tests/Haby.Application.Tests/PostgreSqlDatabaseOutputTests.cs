using Haby.Application;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlDatabaseOutputTests
{
    [Fact]
    public void Database_evidence_has_exact_adapter_owned_contract_and_uint32_roundtrip()
    {
        var evidence = new PostgreSqlDatabaseOutput("dedicated_database", uint.MaxValue, 2200);
        var encoded = PostgreSqlDatabaseOutputContract.Encode(evidence);
        Assert.Equal("postgresql.database-output/v2", encoded.Contract);
        Assert.Equal("database", encoded.Key);
        Assert.Equal("{\"databaseName\":\"dedicated_database\",\"databaseOid\":4294967295,\"publicSchemaOid\":2200}", encoded.Payload);
        Assert.Equal(evidence, PostgreSqlDatabaseOutputContract.Decode(encoded));
        Assert.Null(PostgreSqlDatabaseOutputContract.Decode(encoded with { Contract = "other/v1" }));
        Assert.Null(PostgreSqlDatabaseOutputContract.Decode(encoded with { Key = "other" }));
        Assert.Null(PostgreSqlDatabaseOutputContract.Decode(encoded with { Contract = "postgresql.database-output/v1alpha1" }));
        foreach (var invalid in new[] { evidence with { DatabaseName = "Bad-Db" }, evidence with { DatabaseOid = 0 }, evidence with { PublicSchemaOid = 0 } })
            Assert.Null(PostgreSqlDatabaseOutputContract.Decode(PostgreSqlDatabaseOutputContract.Encode(invalid)));
        Assert.Null(PostgreSqlDatabaseOutputContract.Decode(encoded with { Payload = encoded.Payload[..^1] + ",\"publicSchemaOid\":2200}" }));
    }

    [Theory]
    [InlineData("{\"databaseName\":\"app\",\"databaseOid\":42,\"publicSchemaOid\":0}")]
    [InlineData("{\"databaseName\":\"app\",\"databaseOid\":42,\"publicSchemaOid\":-1}")]
    [InlineData("{\"databaseName\":\"app\",\"databaseOid\":42,\"publicSchemaOid\":4294967296}")]
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
