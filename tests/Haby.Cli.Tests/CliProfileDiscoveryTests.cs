using System.Net;
using Grpc.Core;
using Haby.Operator.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    private sealed class DiscoveryRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal ListProfilesReply Profiles { get; set; } = new();
        internal ListProfileRevisionsReply Revisions { get; set; } = new() { ProfileId = "layout" };
        internal int Calls { get; private set; }
        public override Task<ListProfilesReply> ListProfiles(ListProfilesRequest request, ServerCallContext context)
        { Calls++; Assert.Equal(1, request.PageSize); Assert.Equal("a", request.AfterProfileId); return Task.FromResult(Profiles); }
        public override Task<ListProfileRevisionsReply> ListProfileRevisions(ListProfileRevisionsRequest request, ServerCallContext context)
        { Calls++; Assert.Equal("layout", request.ProfileId); Assert.Equal(1, request.PageSize); Assert.Equal(2, request.AfterRevision); return Task.FromResult(Revisions); }
    }

    private static ProfileMetadata DiscoveryItem(string id = "layout", int revision = 10) => new()
    {
        Reference = new() { ProfileId = id, Revision = revision },
        TechnicalActorId = "original-actor",
        ImportedAtUtc = "2026-01-01T00:00:00.0000000+00:00"
    };

    [Fact]
    public async Task Profile_discovery_validates_replies_and_cursors_before_output_in_managed_and_published_cli()
    {
        var binary = AotBinary(); var registry = new DiscoveryRegistry();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(); builder.Services.AddSingleton(registry);
        await using var host = builder.Build(); host.MapGrpcService<DiscoveryRegistry>(); await host.StartAsync();
        var profilePages = new List<(ListProfilesReply Page, bool Valid)> { (new(), true) };
        var valid = new ListProfilesReply { NextAfterProfileId = "layout" }; valid.Items.Add(DiscoveryItem()); profilePages.Add((valid, true));
        foreach (var bad in new[] { "", "BAD", "other" }) { var page = valid.Clone(); page.NextAfterProfileId = bad; profilePages.Add((page, false)); }
        var tooMany = valid.Clone(); tooMany.Items.Add(DiscoveryItem()); profilePages.Add((tooMany, false));
        foreach (var mutate in new Action<ProfileMetadata>[]
        {
            x => x.Reference = null, x => x.Reference.ProfileId = "BAD", x => x.Reference.ProfileId = "a",
            x => x.Reference.Revision = 0, x => x.TechnicalActorId = "sensitive\nactor", x => x.TechnicalActorId = "",
            x => x.ImportedAtUtc = "sensitive-invalid-time", x => x.ImportedAtUtc = "2026-01-01T01:00:00.0000000+01:00"
        }) { var page = valid.Clone(); mutate(page.Items[0]); profilePages.Add((page, false)); }
        foreach (var (page, accepted) in profilePages)
        {
            registry.Profiles = page;
            await Check(["profile", "list", "--page-size", "1", "--after-profile-id", "a"], accepted ? 0 : 2);
        }
        var revisions = new ListProfileRevisionsReply { ProfileId = "layout", NextAfterRevision = 10 }; revisions.Items.Add(DiscoveryItem());
        registry.Revisions = revisions;
        await Check(["profile", "revision", "list", "layout", "--page-size", "1", "--after-revision", "2"], 0);
        foreach (var mutate in new Action<ListProfileRevisionsReply>[]
        {
            x => x.ProfileId = "foreign", x => x.Items[0].Reference.ProfileId = "foreign", x => x.Items[0].Reference.Revision = 2,
            x => x.NextAfterRevision = 0, x => x.NextAfterRevision = 11, x => x.Items.Clear(), x => x.Items.Add(DiscoveryItem())
        })
        {
            registry.Revisions = revisions.Clone(); mutate(registry.Revisions);
            await Check(["profile", "revision", "list", "layout", "--page-size", "1", "--after-revision", "2"], 2);
        }
        var calls = registry.Calls;
        foreach (var command in new[]
        {
            new[] { "profile", "list", "--page-size", "0" }, new[] { "profile", "list", "--page-size", "101" },
            new[] { "profile", "list", "--after-profile-id", "BAD-sensitive" }, new[] { "profile", "revision", "list", "BAD-sensitive" },
            new[] { "profile", "revision", "list", "layout", "--after-revision", "0" }, new[] { "profile", "revision", "list", "layout", "--after-revision", "-1" }
        }) await Check(command, 2);
        Assert.Equal(calls, registry.Calls);
        await host.StopAsync();

        async Task Check(string[] command, int expected)
        {
            foreach (var format in new[] { "text", "json" })
            {
                var arguments = command.Concat(new[] { "-o", format }).ToArray();
                using var output = new StringWriter(); using var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), output, error);
                var before = registry.Calls;
                Assert.Equal(expected, await cli.RunAsync(arguments));
                Assert.InRange(registry.Calls - before, 0, 1); // No auto-pagination.
                if (expected != 0) { Assert.Empty(output.ToString()); Assert.NotEmpty(error.ToString()); }
                else { Assert.NotEmpty(output.ToString()); Assert.Empty(error.ToString()); }
                Assert.DoesNotContain("sensitive", output.ToString()); Assert.DoesNotContain("sensitive", error.ToString());
                if (binary is not null)
                {
                    var result = await RunProfileBinary(binary, host.Urls.Single(), arguments);
                    Assert.Equal(expected, result.Exit);
                    if (expected != 0) { Assert.Empty(result.Output); Assert.NotEmpty(result.Error); }
                    else { Assert.NotEmpty(result.Output); Assert.Empty(result.Error); }
                    Assert.DoesNotContain("sensitive", result.Output); Assert.DoesNotContain("sensitive", result.Error);
                }
            }
        }
    }
}

public sealed class ProfileDiscoveryPresentationTests
{
    [Fact]
    public void Duplicate_out_of_order_and_cursor_preceding_rows_are_rejected()
    {
        ProfileMetadata Item(string id, int revision) => new()
        { Reference = new() { ProfileId = id, Revision = revision }, TechnicalActorId = "actor", ImportedAtUtc = "2026-01-01T00:00:00.0000000+00:00" };
        foreach (var ids in new[] { new[] { "a", "a" }, new[] { "b", "a" } })
        {
            var page = new ListProfilesReply(); page.Items.AddRange(ids.Select(id => Item(id, 1)));
            Assert.Throws<InvalidDataException>(() => ProfileDiscoveryPresentation.Read(page, 50, null));
        }
        foreach (var numbers in new[] { new[] { 2, 2 }, new[] { 10, 2 } })
        {
            var page = new ListProfileRevisionsReply { ProfileId = "a" }; page.Items.AddRange(numbers.Select(n => Item("a", n)));
            Assert.Throws<InvalidDataException>(() => ProfileDiscoveryPresentation.Read(page, "a", 50, null));
        }
    }
}
