using System.Text;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Tests;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.Application.Tests;

public sealed class ConfigurationProfileImportTests
{
    [Theory]
    [InlineData("{bad")]
    [InlineData("{}")]
    [InlineData("{\"private-value\":\"sentinel\"}")]
    public async Task Invalid_declarations_return_wire_diagnostics_without_store_calls(string json)
    {
        var store = new Store();
        var result = Assert.IsType<ImportConfigurationProfileResult.InvalidDeclaration>(
            await Import(store).ExecuteAsync(Encoding.UTF8.GetBytes(json), "trusted"));
        Assert.False(result.Report.IsSuccess);
        Assert.Null(result.Report.Declaration);
        Assert.NotEmpty(result.Report.Diagnostics);
        Assert.DoesNotContain("sentinel", string.Join("", result.Report.Diagnostics));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Equivalent_normalized_repeats_preserve_full_declaration_and_original_audit()
    {
        var store = new Store();
        var node = Node();
        var first = Success(await Import(store).ExecuteAsync(Bytes(node), "first"));
        Assert.Equal(ImportConfigurationProfileResult.ImportDisposition.Created, first.Disposition);
        Assert.Equal(new ImportAuditMetadata("first", Time), first.Revision.AuditMetadata);
        var repeat = Success(await new ImportConfigurationProfile(store, new Clock(Time.AddDays(1)))
            .ExecuteAsync(Bytes(Equivalent(node)), "later"));
        Assert.Equal(ImportConfigurationProfileResult.ImportDisposition.ExistingEquivalent, repeat.Disposition);
        Assert.Equal(first.Revision, repeat.Revision);
        Assert.Equal(Parse(node), repeat.Revision.Declaration);
        node["spec"] = new JsonObject();
        Assert.Single(first.Revision.Declaration.Documents);
        Assert.Equal(2, first.Revision.Declaration.Contributions.Count);
    }

    [Fact]
    public async Task Omitted_and_explicit_empty_defaults_are_equivalent()
    {
        var store = new Store(); var node = Node(false);
        node["spec"]!["bindingRules"]!["connection"]!.AsObject().Remove("options");
        var first = Success(await Import(store).ExecuteAsync(Bytes(node), "first"));
        node["spec"]!["bindingRules"]!["connection"]!["options"] = new JsonObject();
        Assert.Equal(first.Revision, Success(await Import(store).ExecuteAsync(Bytes(node), "later")).Revision);
        var empty = Node(); empty["metadata"]!["revision"] = 2; empty["spec"] = new JsonObject();
        var second = Success(await Import(store).ExecuteAsync(Bytes(empty), "second"));
        empty["spec"] = new JsonObject { ["documents"] = new JsonObject(), ["staticContributions"] = new JsonObject(), ["bindingRules"] = new JsonObject() };
        Assert.Equal(second.Revision, Success(await Import(store).ExecuteAsync(Bytes(empty), "later")).Revision);
    }

    public static IEnumerable<object[]> ContentChanges()
    {
        yield return ["spec/documents/settings/fileName", "other.json"];
        yield return ["spec/documents/settings/template", "changed"];
        yield return ["spec/staticContributions/logging/documentRef", "another"];
        yield return ["spec/staticContributions/logging/path", "/Other"];
        yield return ["spec/staticContributions/logging/value/Level", "Debug"];
        yield return ["spec/staticContributions/telemetry/secretProjections/0/path", "/OtherToken"];
        yield return ["spec/staticContributions/telemetry/secretProjections/0/format", "json"];
        yield return ["spec/staticContributions/telemetry/secretProjections/0/secretRef/name", "another-token"];
        yield return ["spec/staticContributions/telemetry/secretProjections/0/secretRef/version", "4"];
        yield return ["spec/bindingRules/connection/slot", "other"];
        yield return ["spec/bindingRules/connection/rendererRef", "other.connection/v1"];
        yield return ["spec/bindingRules/connection/options/nested/mode", "other"];
        yield return ["spec/bindingRules/connection/documentProjections/0/path", "/Other"];
        yield return ["spec/bindingRules/connection/documentProjections/0/documentRef", "another"];
        foreach (var map in new[] { "documents", "staticContributions", "bindingRules" }) yield return ["rename/" + map, "renamed"];
    }

    [Theory, MemberData(nameof(ContentChanges))]
    public async Task Every_supported_behavior_change_conflicts_without_overwrite(string path, string value)
    {
        var node = Node(); var store = new Store();
        var first = Success(await Import(store).ExecuteAsync(Bytes(node), "first"));
        var parts = path.Split('/');
        if (parts[0] == "rename")
        {
            var map = node["spec"]![parts[1]]!.AsObject();
            var pair = map.First(); map.Remove(pair.Key); map[value] = pair.Value;
        }
        else
        {
            JsonNode target = node;
            foreach (var part in parts[..^1]) target = target is JsonArray array ? array[int.Parse(part)]! : target[part]!;
            target[parts[^1]] = parts[^1] == "version" ? JsonValue.Create(int.Parse(value)) : JsonValue.Create(value);
        }
        // All candidates remain valid declarations; this is a semantic conflict, not parser refusal.
        Assert.NotEqual(first.Revision.Declaration, Parse(node));
        Assert.Equal(first.Revision.Reference, Assert.IsType<ImportConfigurationProfileResult.RevisionContentConflict>(
            await Import(store).ExecuteAsync(Bytes(node), "later")).Reference);
        Assert.Equal(first.Revision, store.Accepted.Values.Single());
        node["metadata"]!["revision"] = 2;
        Assert.Equal(ImportConfigurationProfileResult.ImportDisposition.Created,
            Success(await Import(store).ExecuteAsync(Bytes(node), "second")).Disposition);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nactor")]
    public async Task Untrusted_actor_shapes_refuse_before_persistence(string actor)
    {
        var store = new Store();
        await Assert.ThrowsAsync<ArgumentException>(async () => await Import(store).ExecuteAsync(Bytes(Node()), actor));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Import(store).ExecuteAsync(Bytes(Node()), new string('a', 257)));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Import(store).ExecuteAsync(Bytes(Node()), null!));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Cancellation_before_acceptance_and_after_confirmed_commit_have_distinct_results()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store { AfterCommit = cancellation.Cancel };
        var result = Success(await Import(store).ExecuteAsync(Bytes(Node()), "first", cancellation.Token));
        Assert.Equal(ImportConfigurationProfileResult.ImportDisposition.Created, result.Disposition);
        Assert.True(cancellation.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Import(store).ExecuteAsync(Bytes(Node()), "later", cancellation.Token));
        Assert.Equal(1, store.Calls);
        using var before = new CancellationTokenSource();
        var rejecting = new Store { BeforeCommit = before.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Import(rejecting).ExecuteAsync(Bytes(Node()), "actor", before.Token));
        Assert.Empty(rejecting.Accepted);
    }

    private static ImportConfigurationProfile Import(Store store) => new(store, new Clock(Time));
    private static ImportConfigurationProfileResult.Succeeded Success(ImportConfigurationProfileResult result) => Assert.IsType<ImportConfigurationProfileResult.Succeeded>(result);
    private sealed class Store : IConfigurationProfileImportStore
    {
        internal readonly Dictionary<ProfileRevisionRef, AcceptedConfigurationProfile> Accepted = [];
        internal int Calls;
        internal Action? BeforeCommit;
        internal Action? AfterCommit;
        public ValueTask<ProfileCompareAndCreateResult> CompareAndCreateAsync(ConfigurationProfileRevision candidate,
            ImportAuditMetadata auditMetadata, CancellationToken cancellationToken)
        {
            Calls++;
            BeforeCommit?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            if (Accepted.TryGetValue(candidate.Reference, out var existing)) return ValueTask.FromResult(new ProfileCompareAndCreateResult(existing, false));
            var revision = new AcceptedConfigurationProfile(candidate, auditMetadata);
            Accepted.Add(candidate.Reference, revision);
            AfterCommit?.Invoke();
            return ValueTask.FromResult(new ProfileCompareAndCreateResult(revision, true));
        }
    }
}
