using Haby.Application;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class MaterializeApplicationConfigurationTests
{
    private sealed class Ports(int count = 3) : IConfigurationApplicationInventory, IConfigurationDocumentStore
    {
        internal readonly ConfigurationApplicationTarget Target = new(ApplicationDeploymentId.New(), DeploymentGeneration.Create(1));
        internal readonly Dictionary<ConfigurationDocumentTarget, ConfigurationDocumentRevision> Saved = [];
        internal readonly List<ConfigurationDocumentTarget> Calls = [];
        internal int Selections;
        internal int? FailAt;
        internal Action<int>? After;
        internal Exception? Fault;
        internal ConfigurationDocumentStatus SelectionStatus = ConfigurationDocumentStatus.Found;
        public ValueTask<ConfigurationApplicationInventory> SelectApplicationAsync(ConfigurationApplicationSelection selection, CancellationToken token)
        {
            Selections++;
            return ValueTask.FromResult(new ConfigurationApplicationInventory(SelectionStatus, Target,
                Enumerable.Range(0, count).Select(i => new ConfigurationDocumentTarget(Target.Deployment, Target.Generation,
                    ManifestLocalId.Create("component"), ManifestLocalId.Create("d" + i.ToString("D4")))).ToArray()));
        }
        public ValueTask<ConfigurationDocumentResult> SelectAsync(ConfigurationDocumentSelection selection, CancellationToken token) => throw new InvalidOperationException("Must not select each document.");
        public ValueTask<ConfigurationDocumentResult> ReadAsync(ConfigurationDocumentTarget target, CancellationToken token) =>
            ValueTask.FromResult(Saved.TryGetValue(target, out var saved) ? new ConfigurationDocumentResult(ConfigurationDocumentStatus.Found, saved, target) : new(ConfigurationDocumentStatus.NotFound, Target: target));
        public ValueTask<ConfigurationDocumentResult> MaterializeAsync(ConfigurationDocumentTarget target, string actor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(target);
            if (FailAt == Calls.Count)
            {
                if (Fault is not null) throw Fault;
                return ValueTask.FromResult(new ConfigurationDocumentResult(ConfigurationDocumentStatus.InputUnavailable, Target: target));
            }
            var existed = Saved.TryGetValue(target, out var saved);
            saved ??= new(new(ConfigurationDocumentRevisionId.New(), target, EnvironmentId.Create("pilot"), Haby.Domain.ApplicationId.Create("app"),
                ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), DeploymentPlanId.New(), "settings.json", "json", ConfigurationJson.Profile,
                "sha256:configuration-revision-v1:" + new string('a', 64), new string('b', 64), actor, DateTimeOffset.UnixEpoch), "", "", new ConfigurationBoolean(true));
            Saved[target] = saved;
            After?.Invoke(Calls.Count);
            return ValueTask.FromResult(new ConfigurationDocumentResult(existed ? ConfigurationDocumentStatus.Existing : ConfigurationDocumentStatus.Created, saved, target));
        }
    }
    private static ConfigurationApplicationSelection Selection => new(EnvironmentId.Create("pilot"), Haby.Domain.ApplicationId.Create("app"), DeploymentGeneration.Create(1));

    [Theory]
    [InlineData("typed")]
    [InlineData("corrupt")]
    [InlineData("storage")]
    [InlineData("limit")]
    public async Task Stop_on_second_failure_reports_full_inventory_and_exact_repeat_reuses_original_artifact(string mode)
    {
        var ports = new Ports { FailAt = 2, Fault = mode switch { "corrupt" => new InvalidDataException("private-sentinel"), "storage" => new IOException("private-sentinel"), "limit" => new ConfigurationDocumentLimitException(), _ => null } };
        var useCase = new MaterializeApplicationConfiguration(ports, ports);
        var first = await useCase.ExecuteAsync(Selection, "original", default);
        Assert.Equal(ConfigurationApplicationStatus.Failed, first.Status);
        Assert.Equal(3, first.Items.Count); Assert.Equal(2, ports.Calls.Count); Assert.Single(ports.Saved);
        Assert.Equal(ConfigurationDocumentStatus.Created, first.Items[0].Status);
        Assert.Equal(ConfigurationDocumentStatus.NotAttempted, first.Items[2].Status);
        Assert.All(first.Items.Skip(1), i => Assert.Null(i.Artifact));
        ports.FailAt = null; ports.Calls.Clear();
        var repeat = await useCase.ExecuteAsync(Selection, "retry", default);
        Assert.Equal(ConfigurationApplicationStatus.Succeeded, repeat.Status);
        Assert.Equal(new[] { ConfigurationDocumentStatus.Existing, ConfigurationDocumentStatus.Created, ConfigurationDocumentStatus.Created }, repeat.Items.Select(i => i.Status));
        Assert.Equal(first.Items[0].Artifact, repeat.Items[0].Artifact);
        Assert.Equal(2, ports.Selections); Assert.All(ports.Calls, p => Assert.Equal(ports.Target.Generation, p.Generation));
    }

    [Fact]
    public async Task Cancellation_after_confirmed_first_commit_never_starts_second_and_exact_retry_converges()
    {
        using var cts = new CancellationTokenSource();
        var ports = new Ports { After = _ => cts.Cancel() };
        var useCase = new MaterializeApplicationConfiguration(ports, ports);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => useCase.ExecuteAsync(Selection, "original", cts.Token).AsTask());
        Assert.Single(ports.Calls); var accepted = Assert.Single(ports.Saved).Value.Metadata;
        ports.After = null;
        var repeat = await useCase.ExecuteAsync(Selection, "retry", default);
        Assert.Equal(ConfigurationApplicationStatus.Succeeded, repeat.Status); Assert.Equal(accepted, repeat.Items[0].Artifact);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public async Task Inventory_count_is_bounded_before_any_materialization_and_empty_is_success(int count, bool allowed)
    {
        var ports = new Ports(count);
        var result = await new MaterializeApplicationConfiguration(ports, ports).ExecuteAsync(Selection, "actor", default);
        Assert.Equal(allowed ? ConfigurationApplicationStatus.Succeeded : ConfigurationApplicationStatus.LimitExceeded, result.Status);
        Assert.Equal(allowed ? count : 0, ports.Calls.Count); Assert.Equal(allowed ? count : 0, ports.Saved.Count);
        Assert.True(ConfigurationApplicationLimits.ResponseReservation(512) < 8 * 1024 * 1024);
    }

    [Theory]
    [InlineData(ConfigurationDocumentStatus.NotFound, ConfigurationApplicationStatus.NotFound)]
    [InlineData(ConfigurationDocumentStatus.NotApplied, ConfigurationApplicationStatus.NotApplied)]
    public async Task Ineligible_selection_never_starts_items(ConfigurationDocumentStatus selected, ConfigurationApplicationStatus expected)
    {
        var ports = new Ports { SelectionStatus = selected };
        Assert.Equal(expected, (await new MaterializeApplicationConfiguration(ports, ports).ExecuteAsync(Selection, "actor", default)).Status);
        Assert.Empty(ports.Calls);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public async Task Complete_existing_metadata_budget_is_checked_before_any_new_write(int actorLength, bool allowed)
    {
        var ports = new Ports { FailAt = 2 };
        var useCase = new MaterializeApplicationConfiguration(ports, ports);
        await useCase.ExecuteAsync(Selection, "original", default);
        var pair = Assert.Single(ports.Saved);
        ports.Saved[pair.Key] = pair.Value with { Metadata = pair.Value.Metadata with { Actor = new string('\uFFFF', actorLength) } };
        ports.Calls.Clear(); ports.FailAt = null;
        var result = await useCase.ExecuteAsync(Selection, "retry", default);
        Assert.Equal(allowed ? ConfigurationApplicationStatus.Succeeded : ConfigurationApplicationStatus.InputCorrupt, result.Status);
        Assert.Equal(allowed ? 3 : 0, ports.Calls.Count); Assert.Equal(allowed ? 3 : 1, ports.Saved.Count);
    }
}
