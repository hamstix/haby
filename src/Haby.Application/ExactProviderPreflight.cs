using Haby.Domain;

namespace Haby.Application;

/// <summary>Exact persisted envelope decoded with its registered format/version reader.</summary>
public sealed record LoadedProviderRevision(ProviderAdapterKey Key, IProviderConfigurationCodec Codec, object Configuration);

public interface IProviderPreflightRevisionReader
{
    ValueTask<ProviderAdapterKey?> FindKeyAsync(EnvironmentRecordId environment, ProviderAliasBinding target,
        CancellationToken cancellationToken);
    ValueTask<LoadedProviderRevision?> ReadAsync(EnvironmentRecordId environment, ProviderAliasBinding target,
        CancellationToken cancellationToken);
}

/// <summary>Explicit typed preflight registration; keys never imply compatible substitutions.</summary>
public interface IProviderPreflightHandler
{
    IReadOnlyList<ProviderAdapterKey> Keys { get; }
    IProviderConfigurationCodec Codec { get; }
    ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, object configuration, CancellationToken cancellationToken);
}

public abstract class ProviderPreflightHandler<TConfiguration>(ProviderConfigurationCodec<TConfiguration> codec)
    : IProviderPreflightHandler where TConfiguration : class
{
    public IProviderConfigurationCodec Codec => codec;
    public abstract IReadOnlyList<ProviderAdapterKey> Keys { get; }
    public abstract ValueTask<ProviderPreflightResult> CheckTypedAsync(EnvironmentRecordId environment,
        TConfiguration configuration, CancellationToken cancellationToken);
    public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, object configuration,
        CancellationToken cancellationToken) => configuration is TConfiguration typed
        ? CheckTypedAsync(environment, typed, cancellationToken)
        : ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.ConfigurationUnavailable));
}

public sealed class ProviderPreflightRegistry
{
    private readonly Dictionary<ProviderAdapterKey, IProviderPreflightHandler> _handlers = [];
    public ProviderPreflightRegistry(IEnumerable<IProviderPreflightHandler> handlers,
        IEnumerable<IProviderConfigurationCodec> codecs, IEnumerable<IProviderDeclarationAdapter> declarations)
    {
        var readers = codecs.ToArray();
        foreach (var handler in handlers)
        {
            if (handler.Keys.Count is < 1 or > 64 || readers.Count(c => ReferenceEquals(c, handler.Codec)) != 1)
                throw new InvalidOperationException("Provider preflight registration is invalid.");
            foreach (var key in handler.Keys)
                if (!ProviderDeclarationRegistry.ValidId(key.ImplementationId) ||
                    !ProviderDeclarationRegistry.ValidId(key.ProviderKind) || !ProviderDeclarationRegistry.ValidId(key.ConfigurationContract) ||
                    key.ProviderKind != handler.Codec.ProviderKindId || key.ConfigurationContract != handler.Codec.ShapeId ||
                    handler.Codec.PayloadFormatVersion <= 0 || !_handlers.TryAdd(key, handler))
                    throw new InvalidOperationException("Provider preflight registration is invalid or ambiguous.");
        }
        foreach (var declaration in declarations)
            if (!_handlers.TryGetValue(declaration.Key, out var handler) || !ReferenceEquals(handler.Codec, declaration.Codec))
                throw new InvalidOperationException("Provider declaration and preflight registrations are inconsistent.");
    }
    public IProviderPreflightHandler? Find(ProviderAdapterKey key) => _handlers.GetValueOrDefault(key);
}

/// <summary>Shared side-effect-free dispatch. Catalog loading and adapter I/O share one bounded deadline.</summary>
public sealed class ExactProviderRevisionPreflight(IProviderPreflightRevisionReader reader, ProviderPreflightRegistry registry)
    : IProviderRevisionPreflight
{
    public async ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment,
        ProviderAliasBinding target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var key = await reader.FindKeyAsync(environment, target, deadline.Token);
            if (key is null) return new(ProviderPreflightOutcome.RevisionUnavailable);
            var handler = registry.Find(key);
            if (handler is null) return new(ProviderPreflightOutcome.HandlerUnavailable);
            var revision = await reader.ReadAsync(environment, target, deadline.Token);
            if (revision is null) return new(ProviderPreflightOutcome.RevisionUnavailable);
            if (revision.Key != key || !ReferenceEquals(handler.Codec, revision.Codec)) return new(ProviderPreflightOutcome.ConfigurationUnavailable);
            var result = await handler.CheckAsync(environment, revision.Configuration, deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return deadline.IsCancellationRequested ? new(ProviderPreflightOutcome.DeadlineExceeded) : result;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested) { return new(ProviderPreflightOutcome.DeadlineExceeded); }
        catch (Exception) { return new(ProviderPreflightOutcome.ConfigurationUnavailable); }
    }
}
