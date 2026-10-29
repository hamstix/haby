using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ExactProviderPreflightTests
{
    [Theory]
    [InlineData("impl-a", ProviderPreflightOutcome.Ready)]
    [InlineData("impl-b", ProviderPreflightOutcome.Ready)]
    [InlineData("impl-other", ProviderPreflightOutcome.HandlerUnavailable)]
    public async Task One_typed_handler_explicitly_supports_exact_combinations_without_fallback(string implementation, ProviderPreflightOutcome outcome)
    {
        var codec = new Codec();
        var handler = new Handler(codec);
        var reader = new Reader(new(new("queue/v1", "queue.settings/v1", implementation), codec, new Settings(42)));
        var preflight = new ExactProviderRevisionPreflight(reader, new([handler], [codec], []));
        var environment = EnvironmentRecordId.New();
        var binding = new ProviderAliasBinding(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(7));
        Assert.Equal(outcome, (await preflight.CheckAsync(environment, binding, default)).Outcome);
        Assert.Equal((environment, binding), reader.Request);
        Assert.Equal(outcome == ProviderPreflightOutcome.Ready ? 1 : 0, handler.Calls);
    }
    [Fact]
    public void Overlapping_keys_and_private_codec_are_rejected()
    {
        var codec = new Codec();
        Assert.Throws<InvalidOperationException>(() => new ProviderPreflightRegistry([new Handler(codec), new Handler(codec)], [codec], []));
        Assert.Throws<InvalidOperationException>(() => new ProviderPreflightRegistry([new Handler(new Codec())], [codec], []));
    }
    [Fact]
    public async Task Missing_revision_and_bad_decode_are_safe_and_cancellation_propagates()
    {
        var codec = new Codec();
        var binding = new ProviderAliasBinding(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1));
        var environment = EnvironmentRecordId.New();
        var registry = new ProviderPreflightRegistry([new Handler(codec)], [codec], []);
        Assert.Equal(ProviderPreflightOutcome.RevisionUnavailable,
            (await new ExactProviderRevisionPreflight(new Reader(null), registry).CheckAsync(environment, binding, default)).Outcome);
        var broken = new ExactProviderRevisionPreflight(new BrokenReader(), registry);
        var result = await broken.CheckAsync(environment, binding, default);
        Assert.Equal(ProviderPreflightOutcome.ConfigurationUnavailable, result.Outcome);
        Assert.DoesNotContain("credential", result.ToString());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await broken.CheckAsync(environment, binding, cancelled.Token));
    }
    [Theory]
    [InlineData("queue.settings/v1")]
    [InlineData("queue.settings/v2")]
    public async Task Typed_handler_accepts_only_its_explicit_registered_format_and_contract_readers(string historicalContract)
    {
        var current = new Codec(2);
        var historical = new Codec(1, historicalContract);
        var handler = new CompatibleHandler(current, historical);
        var registry = new ProviderPreflightRegistry([handler], [current, historical], []);
        var reader = new Reader(new(new("queue/v1", historicalContract, "impl-a"), historical, new Settings(42)));
        var environment = EnvironmentRecordId.New();
        var binding = new ProviderAliasBinding(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1));
        Assert.Equal(ProviderPreflightOutcome.Ready, (await new ExactProviderRevisionPreflight(reader, registry).CheckAsync(environment, binding, default)).Outcome);
        Assert.Equal(1, handler.Calls);
        // Merely registering a decoder never makes it an accepted input to another handler.
        var restricted = new CompatibleHandler(current);
        var restrictedRegistry = new ProviderPreflightRegistry([restricted], [current, historical], []);
        var result = await new ExactProviderRevisionPreflight(reader, restrictedRegistry).CheckAsync(environment, binding, default);
        Assert.Equal(historicalContract == current.ShapeId ? ProviderPreflightOutcome.ConfigurationUnavailable : ProviderPreflightOutcome.HandlerUnavailable, result.Outcome);
        Assert.Equal(0, restricted.Calls);
    }
    private sealed class CompatibleHandler(params Codec[] readers) : ProviderPreflightHandler<Settings>(readers)
    {
        public int Calls;
        public override IReadOnlyList<ProviderAdapterKey> Keys => Readers.Select(reader =>
            new ProviderAdapterKey(reader.ProviderKindId, reader.ShapeId, "impl-a")).Distinct().ToArray();
        public override ValueTask<ProviderPreflightResult> CheckTypedAsync(EnvironmentRecordId environment, Settings configuration, CancellationToken token)
        {
            Assert.Equal(42, configuration.QueueLimit);
            Calls++;
            return ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
        }
    }
    private sealed record Settings(int QueueLimit);
    private sealed class Codec(int format = 1, string contract = "queue.settings/v1") : ProviderConfigurationCodec<Settings>
    {
        public override string ProviderKindId => "queue/v1";
        public override string ShapeId => contract;
        public override int PayloadFormatVersion => format;
        public override PreparedProviderConfiguration Encode(Settings value) => throw new NotSupportedException();
        public override Settings DecodeTyped(string json) => throw new NotSupportedException();
    }
    private sealed class Handler(Codec codec) : ProviderPreflightHandler<Settings>(codec)
    {
        public int Calls;
        public override IReadOnlyList<ProviderAdapterKey> Keys => [new("queue/v1", "queue.settings/v1", "impl-a"), new("queue/v1", "queue.settings/v1", "impl-b")];
        public override ValueTask<ProviderPreflightResult> CheckTypedAsync(EnvironmentRecordId environment, Settings configuration, CancellationToken token)
        {
            Assert.Equal(42, configuration.QueueLimit);
            Calls++;
            return ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
        }
    }
    private sealed class Reader(LoadedProviderRevision? result) : IProviderPreflightRevisionReader
    {
        public (EnvironmentRecordId, ProviderAliasBinding)? Request;
        public ValueTask<ProviderAdapterKey?> FindKeyAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken token)
        { Request = (environment, target); return ValueTask.FromResult(result?.Key); }
        public ValueTask<LoadedProviderRevision?> ReadAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken token)
        { Request = (environment, target); return ValueTask.FromResult(result); }
    }
    private sealed class BrokenReader : IProviderPreflightRevisionReader
    {
        public ValueTask<ProviderAdapterKey?> FindKeyAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken token) =>
            throw new InvalidDataException("private credential driver diagnostic");
        public ValueTask<LoadedProviderRevision?> ReadAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken token) =>
            throw new InvalidDataException("private credential driver diagnostic");
    }
}
