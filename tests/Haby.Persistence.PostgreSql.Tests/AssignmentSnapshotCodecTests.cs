using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class AssignmentSnapshotCodecTests
{
    [Fact]
    public void Closed_snapshot_round_trips_all_decisions_and_canonicalizes_order()
    {
        var target = new ProfileAssignmentTarget.Component(Haby.Domain.ApplicationId.Create("example"), ManifestLocalId.Create("api"));
        ProfileDecision[] decisions = [new ProfileDecision.Use(new(ProfileId.Create("layout"), 3)), new ProfileDecision.Disable(ProfileId.Create("absent"))];
        var assignment = new ConfigurationProfileAssignment(target, decisions);
        var snapshot = AssignmentSnapshotCodec.Encode(assignment);
        Assert.Equal(assignment, AssignmentSnapshotCodec.Decode(snapshot, target));
        Assert.Equal(snapshot, AssignmentSnapshotCodec.Encode(new(target, decisions.Reverse())));
        Assert.Empty(AssignmentSnapshotCodec.Decode(AssignmentSnapshotCodec.Encode(new(target, [])), target).Decisions);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{private-snapshot")]
    [InlineData("{\"format\":2,\"model\":1,\"decisions\":[]}")]
    [InlineData("{\"format\":1,\"model\":2,\"decisions\":[]}")]
    [InlineData("{\"format\":1,\"format\":1,\"model\":1,\"decisions\":[]}")]
    [InlineData("{\"format\":1,\"model\":1,\"private-snapshot\":true,\"decisions\":[]}")]
    [InlineData("{\"format\":1,\"model\":1,\"decisions\":null}")]
    [InlineData("{\"format\":1,\"model\":1,\"decisions\":[null]}")]
    [InlineData("{\"format\":1,\"model\":1,\"decisions\":[{\"id\":\"layout\",\"kind\":\"use\",\"revision\":0}]}")]
    [InlineData("{\"format\":1,\"model\":1,\"decisions\":[{\"id\":\"layout\",\"kind\":\"unknown\"}]}")]
    [InlineData("{\"format\":1,\"model\":1,\"decisions\":[{\"id\":\"layout\",\"kind\":\"disable\",\"revision\":1}]}")]
    [InlineData("{\"format\":1,\"model\":1,\"decisions\":[{\"id\":\"layout\",\"id\":\"layout\",\"kind\":\"disable\"}]}")]
    [InlineData("{\"format\":1,\"model\":1,\"decisions\":[{\"id\":\"layout\",\"kind\":\"disable\"},{\"id\":\"layout\",\"kind\":\"disable\"}]}")]
    public void Corruption_and_unsupported_shapes_fail_without_echoing_content(string snapshot)
    {
        var error = Assert.Throws<InvalidDataException>(() => AssignmentSnapshotCodec.Decode(snapshot, new ProfileAssignmentTarget.Environment()));
        Assert.Equal("Stored assignment snapshot is invalid or unsupported.", error.Message);
        Assert.Null(error.InnerException);
    }
}
