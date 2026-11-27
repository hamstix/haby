using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ProviderConnectionStatusTests
{
    [Fact]
    public async Task Snapshot_selection_is_held_through_alias_change_and_one_failure_preserves_other_results()
    {
        var fixture = new Fixture();
        fixture.Probe.Run = (context, token) =>
        {
            fixture.Selection.Items = [fixture.Selected with { Revision = ProviderConfigurationRevisionNumber.Create(99) }];
            Assert.Equal(7, context.Revision.Value);
            Assert.Equal(fixture.Environment, context.Environment);
            Assert.Equal(fixture.Selected.Provider, context.Provider);
            return ValueTask.FromResult(new ProviderConnectionResult(ProviderConnectionOutcome.AuthenticationFailed));
        };
        var result = await fixture.Service.ObserveAsync("staging", null, default);
        Assert.Equal(ProviderConnectionStatusOutcome.Observed, result.Outcome);
        Assert.Equal(ProviderConnectionOutcome.AuthenticationFailed, result.Items[0].Result.Outcome);
        Assert.Equal(7, result.Items[0].Selection.Revision!.Value);
        Assert.Equal(ProviderConnectionOutcome.NotSelected, result.Items[1].Result.Outcome);
        Assert.Null(result.Items[1].Selection.Revision);
        Assert.Equal(1, fixture.Selection.Calls);
        Assert.Single(fixture.Probe.Contexts);
        Assert.All(result.Items, i => Assert.InRange(i.ObservedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task Empty_and_one_not_found_are_distinct_and_enumeration_is_capped_before_probes()
    {
        var fixture = new Fixture();
        fixture.Selection.Items = [];
        Assert.Empty((await fixture.Service.ObserveAsync("staging", null, default)).Items);
        Assert.Equal(ProviderConnectionStatusOutcome.NotFound, (await fixture.Service.ObserveAsync("staging", "missing", default)).Outcome);
        fixture.Selection.Items = Enumerable.Repeat(fixture.Selected, 129).ToArray();
        Assert.Equal(ProviderConnectionStatusOutcome.TooManyProviders, (await fixture.Service.ObserveAsync("staging", null, default)).Outcome);
        Assert.Empty(fixture.Probe.Contexts);
        Assert.Equal(129, fixture.Selection.Maximum);
    }
    [Fact]
    public async Task Probe_timeout_and_caller_cancellation_are_not_authentication_failures_and_errors_are_redacted()
    {
        var fixture = new Fixture(new() { ProbeTimeout = TimeSpan.FromMilliseconds(20) });
        fixture.Probe.Run = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(ProviderConnectionOutcome.Connected); };
        var result = await fixture.Service.ObserveAsync("staging", "selected", default);
        Assert.Equal(ProviderConnectionOutcome.DeadlineExceeded, result.Items[0].Result.Outcome);
        var caller = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        caller.Probe.Run = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(ProviderConnectionOutcome.Connected);
        };
        using var cancellation = new CancellationTokenSource();
        var observing = caller.Service.ObserveAsync("staging", "selected", cancellation.Token).AsTask();
        // These bounds only detect a hung test; cancellation is ordered by probe entry.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(cancellation.IsCancellationRequested);
        cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await observing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        fixture.Probe.Run = (_, _) => throw new InvalidDataException("password=private");
        result = await fixture.Service.ObserveAsync("staging", "selected", default);
        Assert.Equal(ProviderConnectionOutcome.ConfigurationUnavailable, result.Items[0].Result.Outcome);
        Assert.DoesNotContain("private", result.Items[0].Result.ToString());
        var capacity = new Fixture();
        using var release = new CancellationTokenSource();
        capacity.Probe.Run = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(ProviderConnectionOutcome.Connected); };
        var active = Enumerable.Range(0, 4).Select(_ => capacity.Service.ObserveAsync("staging", "selected", release.Token).AsTask()).ToArray();
        Assert.Equal(4, capacity.Probe.Contexts.Count);
        Assert.Equal(ProviderConnectionStatusOutcome.CapacityUnavailable, (await capacity.Service.ObserveAsync("staging", "selected", default)).Outcome);
        release.Cancel();
        foreach (var call in active) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

    }
    [Fact]
    public async Task Missing_probe_is_explicit_and_unregistered_codec_cannot_dispatch()
    {
        var codec = new Codec(); var probe = new Probe(codec); var env = EnvironmentRecordId.New();
        var context = new ProviderConnectionContext(env, ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1));
        var reader = new Reader(new(new("sample/v1", "sample.configuration/v1", "other/v1"), codec, new Settings()));
        Assert.Equal(ProviderConnectionOutcome.Unsupported,
            (await new ExactProviderConnectionProbe(reader, new([probe], [codec])).ProbeAsync(context, default)).Outcome);
        reader.Loaded = new(probe.Keys[0], new Codec(), new Settings());
        Assert.Equal(ProviderConnectionOutcome.ConfigurationUnavailable,
            (await new ExactProviderConnectionProbe(reader, new([probe], [codec])).ProbeAsync(context, default)).Outcome);
        Assert.Empty(probe.Contexts);
        Assert.Throws<InvalidOperationException>(() => new ProviderConnectionProbeRegistry([probe, probe], [codec]));
        Assert.Throws<InvalidOperationException>(() => new ProviderConnectionProbeRegistry([probe], [new Codec()]));
    }
    private sealed class Fixture
    {
        public EnvironmentRecordId Environment { get; } = EnvironmentRecordId.New();
        public ProviderConnectionSelection Selected { get; } = new("selected", ProviderInstanceId.New(), "primary", ProviderConfigurationRevisionNumber.Create(7));
        public Selection Selection { get; }
        public Probe Probe { get; }
        public ProviderConnectionStatus Service { get; }
        public Fixture(ProviderConnectionStatusOptions? options = null)
        {
            var codec = new Codec(); Probe = new(codec);
            Selection = new([Selected, new("unbound", ProviderInstanceId.New(), null, null)]);
            Service = new(new Environments(Environment), Selection,
                new(new Reader(new(Probe.Keys[0], codec, new Settings())), new([Probe], [codec])), options ?? new());
        }
    }
    private sealed class Environments(EnvironmentRecordId environment) : IEnvironmentQuery
    { public ValueTask<EnvironmentRecordId?> FindAsync(EnvironmentId id, CancellationToken token) => ValueTask.FromResult<EnvironmentRecordId?>(environment); }
    private sealed class Selection(IReadOnlyList<ProviderConnectionSelection> items) : IProviderConnectionSelectionQuery
    {
        public IReadOnlyList<ProviderConnectionSelection> Items = items;
        public int Calls, Maximum;
        public ValueTask<IReadOnlyList<ProviderConnectionSelection>> SelectAsync(EnvironmentRecordId env, string? name, int maximum, CancellationToken token)
        { Calls++; Maximum = maximum; return ValueTask.FromResult<IReadOnlyList<ProviderConnectionSelection>>(Items.Where(x => name is null || x.Name == name).ToArray()); }
    }
    private sealed record Settings;
    private sealed class Codec : ProviderConfigurationCodec<Settings>
    {
        public override string ProviderKindId => "sample/v1";
        public override string ShapeId => "sample.configuration/v1";
        public override int PayloadFormatVersion => 1;
        public override PreparedProviderConfiguration Encode(Settings value) => throw new NotSupportedException();
        public override Settings DecodeTyped(string json) => throw new NotSupportedException();
    }
    private sealed class Probe(Codec codec) : ProviderConnectionProbe<Settings>(codec)
    {
        public override IReadOnlyList<ProviderAdapterKey> Keys { get; } = [new("sample/v1", "sample.configuration/v1", "sample.impl/v1")];
        public Func<ProviderConnectionContext, CancellationToken, ValueTask<ProviderConnectionResult>> Run = (_, _) => ValueTask.FromResult(new ProviderConnectionResult(ProviderConnectionOutcome.Connected));
        public List<ProviderConnectionContext> Contexts { get; } = [];
        public override ValueTask<ProviderConnectionResult> ProbeTypedAsync(ProviderConnectionContext context, Settings configuration, CancellationToken token)
        { Contexts.Add(context); return Run(context, token); }
    }
    private sealed class Reader(LoadedProviderRevision loaded) : IProviderPreflightRevisionReader
    {
        public LoadedProviderRevision Loaded = loaded;
        public ValueTask<ProviderAdapterKey?> FindKeyAsync(EnvironmentRecordId env, ProviderAliasBinding target, CancellationToken token) => ValueTask.FromResult<ProviderAdapterKey?>(Loaded.Key);
        public ValueTask<LoadedProviderRevision?> ReadAsync(EnvironmentRecordId env, ProviderAliasBinding target, CancellationToken token) => ValueTask.FromResult<LoadedProviderRevision?>(Loaded);
    }
}
