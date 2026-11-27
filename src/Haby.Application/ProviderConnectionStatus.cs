using Haby.Domain;

namespace Haby.Application;

/// <summary>Point-in-time login observation only; never a capability or authorization receipt.</summary>
public enum ProviderConnectionOutcome
{
    Connected, NotSelected, Unsupported, HandlerUnavailable, RevisionUnavailable, ConfigurationUnavailable,
    SecretUnavailable, AuthenticationFailed, AuthenticationUnavailable, ConnectionUnavailable, TlsFailed, DeadlineExceeded
}
public sealed record ProviderConnectionResult(ProviderConnectionOutcome Outcome)
{
    public bool ServerCertificateValidationDisabled { get; init; }
    public ProviderConnectionDiagnostic? Diagnostic { get; init; }
}
/// <summary>Engine-selected persisted identity, supplied to the probe and its credential resolver.</summary>
public sealed record ProviderConnectionContext(EnvironmentRecordId Environment, ProviderInstanceId Provider,
    ProviderConfigurationRevisionNumber Revision);
public interface IProviderConnectionProbe
{
    IReadOnlyList<ProviderAdapterKey> Keys { get; }
    IReadOnlyList<IProviderConfigurationCodec> Readers { get; }
    ValueTask<ProviderConnectionResult> ProbeAsync(ProviderConnectionContext context, object configuration, CancellationToken token);
}
public abstract class ProviderConnectionProbe<TConfiguration>(params ProviderConfigurationCodec<TConfiguration>[] readers)
    : IProviderConnectionProbe where TConfiguration : class
{
    public IReadOnlyList<IProviderConfigurationCodec> Readers { get; } = Array.AsReadOnly<IProviderConfigurationCodec>(readers.ToArray());
    public abstract IReadOnlyList<ProviderAdapterKey> Keys { get; }
    public abstract ValueTask<ProviderConnectionResult> ProbeTypedAsync(ProviderConnectionContext context, TConfiguration configuration, CancellationToken token);
    public ValueTask<ProviderConnectionResult> ProbeAsync(ProviderConnectionContext context, object configuration, CancellationToken token) =>
        configuration is TConfiguration typed ? ProbeTypedAsync(context, typed, token) : ValueTask.FromResult(new ProviderConnectionResult(ProviderConnectionOutcome.ConfigurationUnavailable));
}
public sealed class ProviderConnectionProbeRegistry
{
    private readonly Dictionary<ProviderAdapterKey, IProviderConnectionProbe> probes = [];
    public ProviderConnectionProbeRegistry(IEnumerable<IProviderConnectionProbe> registrations, IEnumerable<IProviderConfigurationCodec> codecs)
    {
        var known = codecs.ToArray();
        foreach (var probe in registrations)
        {
            if (probe.Keys.Count is < 1 or > 64 || probe.Readers.Count is < 1 or > 64 ||
                probe.Readers.Distinct(ReferenceEqualityComparer.Instance).Count() != probe.Readers.Count ||
                probe.Readers.Any(r => known.Count(c => ReferenceEquals(c, r)) != 1 || r.PayloadFormatVersion <= 0 ||
                    !probe.Keys.Any(k => k.ProviderKind == r.ProviderKindId && k.ConfigurationContract == r.ShapeId)))
                throw new InvalidOperationException("Invalid Provider connection probe registration.");
            foreach (var key in probe.Keys)
                if (!ProviderDeclarationRegistry.ValidId(key.ProviderKind) || !ProviderDeclarationRegistry.ValidId(key.ConfigurationContract) ||
                    !ProviderDeclarationRegistry.ValidId(key.ImplementationId) || !probe.Readers.Any(r => r.ProviderKindId == key.ProviderKind && r.ShapeId == key.ConfigurationContract) ||
                    !probes.TryAdd(key, probe)) throw new InvalidOperationException("Invalid or ambiguous Provider connection probe registration.");
        }
    }
    public IProviderConnectionProbe? Find(ProviderAdapterKey key) => probes.GetValueOrDefault(key);
}
public sealed class ExactProviderConnectionProbe(IProviderPreflightRevisionReader reader, ProviderConnectionProbeRegistry registry)
{
    public async ValueTask<ProviderConnectionResult> ProbeAsync(ProviderConnectionContext context, CancellationToken token)
    {
        var target = new ProviderAliasBinding(context.Provider, context.Revision);
        var key = await reader.FindKeyAsync(context.Environment, target, token);
        if (key is null) return new(ProviderConnectionOutcome.RevisionUnavailable);
        var probe = registry.Find(key);
        if (probe is null) return new(ProviderConnectionOutcome.Unsupported);
        var loaded = await reader.ReadAsync(context.Environment, target, token);
        if (loaded is null) return new(ProviderConnectionOutcome.RevisionUnavailable);
        if (loaded.Key != key || !probe.Readers.Any(r => ReferenceEquals(r, loaded.Codec))) return new(ProviderConnectionOutcome.ConfigurationUnavailable);
        return await probe.ProbeAsync(context, loaded.Configuration, token);
    }
}
public sealed record ProviderConnectionSelection(string? Name, ProviderInstanceId Provider, string? Alias, ProviderConfigurationRevisionNumber? Revision);
public interface IProviderConnectionSelectionQuery
{
    // Bounded snapshot of identities and current selections. No highest-revision fallback.
    ValueTask<IReadOnlyList<ProviderConnectionSelection>> SelectAsync(EnvironmentRecordId environment, string? name, int maximum, CancellationToken token);
}
public enum ProviderConnectionStatusOutcome { Observed, InvalidRequest, EnvironmentNotFound, NotFound, TooManyProviders, StorageUnavailable, DeadlineExceeded, CapacityUnavailable }
public sealed record ProviderConnectionObservation(ProviderConnectionSelection Selection, DateTimeOffset ObservedAt, ProviderConnectionResult Result);
public sealed record ProviderConnectionStatusResult(ProviderConnectionStatusOutcome Outcome, IReadOnlyList<ProviderConnectionObservation> Items);
public sealed class ProviderConnectionStatusOptions
{
    public int MaximumProviders { get; init; } = 128;
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
/// <summary>Sequential probes bound fanout to one per request; all failures retain their own observation.</summary>
public sealed class ProviderConnectionStatus(IEnvironmentQuery environments, IProviderConnectionSelectionQuery selections,
    ExactProviderConnectionProbe probe, ProviderConnectionStatusOptions options)
{
    private readonly SemaphoreSlim admission = new(4, 4);
    public async ValueTask<ProviderConnectionStatusResult> ObserveAsync(string environment, string? name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ProviderDeclarationInput.ValidName(environment) || name is not null && !ProviderDeclarationInput.ValidName(name))
            return new(ProviderConnectionStatusOutcome.InvalidRequest, []);
        if (options.MaximumProviders is < 1 or > 1024 || options.ProbeTimeout <= TimeSpan.Zero || options.ProbeTimeout > TimeSpan.FromSeconds(30) ||
            options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(2)) throw new InvalidOperationException("Invalid Provider status bounds.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.RequestTimeout);
        var entered = false;
        try
        {
            if (!await admission.WaitAsync(0, deadline.Token)) return new(ProviderConnectionStatusOutcome.CapacityUnavailable, []);
            entered = true;
            var env = await environments.FindAsync(EnvironmentId.Create(environment), deadline.Token);
            if (env is null) return new(ProviderConnectionStatusOutcome.EnvironmentNotFound, []);
            var selected = await selections.SelectAsync(env, name, options.MaximumProviders + 1, deadline.Token);
            if (selected.Count > options.MaximumProviders) return new(ProviderConnectionStatusOutcome.TooManyProviders, []);
            if (name is not null && selected.Count == 0) return new(ProviderConnectionStatusOutcome.NotFound, []);
            var observations = new List<ProviderConnectionObservation>();
            foreach (var selection in selected)
            {
                token.ThrowIfCancellationRequested();
                ProviderConnectionResult result;
                if (selection.Revision is null) result = new(ProviderConnectionOutcome.NotSelected);
                else if (deadline.IsCancellationRequested) result = new(ProviderConnectionOutcome.DeadlineExceeded);
                else
                {
                    using var call = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    call.CancelAfter(options.ProbeTimeout);
                    try
                    {
                        result = await probe.ProbeAsync(new(env, selection.Provider, selection.Revision), call.Token);
                        if (call.IsCancellationRequested) result = new(ProviderConnectionOutcome.DeadlineExceeded);
                    }
                    catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                    catch (Exception) when (call.IsCancellationRequested) { result = new(ProviderConnectionOutcome.DeadlineExceeded); }
                    catch (Exception) { result = new(ProviderConnectionOutcome.ConfigurationUnavailable); }
                }
                observations.Add(new(selection, DateTimeOffset.UtcNow, result));
            }
            return new(ProviderConnectionStatusOutcome.Observed, observations);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception) when (deadline.IsCancellationRequested) { return new(ProviderConnectionStatusOutcome.DeadlineExceeded, []); }
        catch (Exception) { return new(ProviderConnectionStatusOutcome.StorageUnavailable, []); }
        finally { if (entered) admission.Release(); }
    }
}
