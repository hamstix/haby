using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ResourceNamingTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{\"mode\":null}")]
    [InlineData("{\"mode\":\"exact\",\"prefix\":null}")]
    [InlineData("{\"mode\":\"exact\",\"prefix\":\"staging_\"}")]
    [InlineData("{\"mode\":\"prefixed\"}")]
    [InlineData("{\"mode\":\"prefixed\",\"prefix\":\"\"}")]
    [InlineData("{\"mode\":\"exact\",\"extra\":\"x\"}")]
    [InlineData("{\"mode\":\"exact\",\"mode\":\"exact\"}")]
    public void ClosedDeclarationRefusesMalformedInputs(string json) =>
        Assert.Throws<InvalidDataException>(() => ResourceNamingCodec.ParsePolicy(json));

    [Fact]
    public void FrozenPolicyCodecPreservesExactIdentity()
    {
        var snapshot = new ResourceNamingSnapshot(1, new("contour", 2), new("prefixed", "staging_"));
        Assert.Equal(snapshot, ResourceNamingCodec.Decode(ResourceNamingCodec.Encode(snapshot)));
        Assert.NotEqual(snapshot, snapshot with { Reference = new("contour", 3) });
    }

    [Fact]
    public void CanonicalAbsentSelectionSurvivesTheCurrentCodec() =>
        Assert.Equal(ResourceNamingSnapshot.Default, ResourceNamingCodec.Decode(ResourceNamingCodec.Encode(ResourceNamingSnapshot.Default)));

    [Fact]
    public void PrefixIsAppliedLiterallyAndFinalNameIsBounded()
    {
        var policy = new ResourceNamingPolicy("prefixed", "staging_");
        Assert.Equal("staging_orders", PostgreSqlResourceNaming.Database("orders", policy));
        Assert.Equal("staging_staging_orders", PostgreSqlResourceNaming.Database("staging_orders", policy));
        Assert.Equal(new string('a', 63), PostgreSqlResourceNaming.Database(new string('a', 63), ResourceNamingPolicy.Exact));
        Assert.Null(PostgreSqlResourceNaming.Database(new string('a', 56), policy));
        Assert.Equal(63, PostgreSqlResourceNaming.Database(new string('a', 55), policy)!.Length);
        Assert.Null(PostgreSqlResourceNaming.Database("orders", new("prefixed", "Bad_")));
        Assert.Null(PostgreSqlResourceNaming.Database("orders", new("prefixed", "pg_")));
    }

    [Fact]
    public void GeneratedNamesPreserveOwnerSeparationAndPrefixBudget()
    {
        var owner = new ManagedResourceKey(EnvironmentRecordId.New(), ApplicationRecordId.New(), ManifestLocalId.Create("login"));
        Assert.Equal(PostgreSqlPairResourceAdmission.RoleName(owner), PostgreSqlResourceNaming.Role(owner, ResourceNamingPolicy.Exact));
        var policy = new ResourceNamingPolicy("prefixed", "staging_shard_");
        var name = PostgreSqlResourceNaming.Role(owner, policy);
        Assert.StartsWith("staging_shard_haby_a_", name);
        Assert.Equal(name, PostgreSqlResourceNaming.Role(owner, policy));
        Assert.NotEqual(name, PostgreSqlResourceNaming.Role(owner with { EnvironmentRecordId = EnvironmentRecordId.New() }, policy));
        Assert.Equal(63, PostgreSqlResourceNaming.Role(owner, new("prefixed", new string('a', 24)))!.Length);
        Assert.Null(PostgreSqlResourceNaming.Role(owner, new("prefixed", new string('a', 25))));
    }
}
