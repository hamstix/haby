using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Haby.Configuration.Contracts;

namespace Haby.Configuration.Client;

/// <summary>
/// Sequential exact startup retrieval. Caller owns the generated client/channel, TLS and absence of ambient
/// credentials/retry/hedging policies. This helper owns each call and all buffers it returns.
/// </summary>
public sealed class RuntimeStartupClient
{
    internal const int MaximumJsonBytes = 4 * 1024 * 1024;
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly Func<DeliverRuntimeConfigurationRequest, Metadata, DateTime, CancellationToken, Task<DeliverConfigurationReply>> deliver;
    private readonly Func<string, CancellationToken, Task<byte[]>> read;
    private readonly TimeProvider clock;
    private readonly Func<double> random;
    internal Action<RuntimeStartupDocument>? DocumentAccepted { get; init; }

    public RuntimeStartupClient(ConfigurationDelivery.ConfigurationDeliveryClient client)
        : this(async (request, headers, deadline, token) =>
        {
            using var call = client.DeliverRuntimeAsync(request, headers, deadline: deadline, cancellationToken: token);
            return await call.ResponseAsync.ConfigureAwait(false);
        }, TimeProvider.System, Random.Shared.NextDouble, ProjectedCredentialFile.ReadAsync)
    { ArgumentNullException.ThrowIfNull(client); }

    internal RuntimeStartupClient(Func<DeliverRuntimeConfigurationRequest, Metadata, DateTime, CancellationToken, Task<DeliverConfigurationReply>> deliver,
        TimeProvider clock, Func<double> random, Func<string, CancellationToken, Task<byte[]>> read)
    { this.deliver = deliver; this.clock = clock; this.random = random; this.read = read; }

    public async Task<RuntimeStartupDocuments> FetchAsync(string bindingId, IReadOnlyList<string> revisionIds, string projectedTokenPath,
        RuntimeStartupOptions? options = null, CancellationToken cancellationToken = default)
    {
        var start = clock.GetTimestamp();
        var accepted = new List<RuntimeStartupDocument>();
        var budgetValidated = false;
        var totalCancellation = CancellationToken.None;
        void CheckBudget()
        {
            if (cancellationToken.IsCancellationRequested) throw Failure(RuntimeStartupFailure.CallerCancelled);
            if (budgetValidated && (totalCancellation.IsCancellationRequested || Remaining(start, options!.TotalTimeout) <= TimeSpan.Zero))
                throw Failure(RuntimeStartupFailure.StartupDeadlineExceeded);
        }
        try
        {
            options ??= new();
            if (cancellationToken.IsCancellationRequested) throw Failure(RuntimeStartupFailure.CallerCancelled);
            options.Validate();
            budgetValidated = true;
            CheckBudget();
            if (!Guid.TryParseExact(bindingId, "D", out var binding) || binding == Guid.Empty || revisionIds is null ||
                revisionIds.Count is < 1 or > 64 || string.IsNullOrWhiteSpace(projectedTokenPath)) throw Failure(RuntimeStartupFailure.InvalidBootstrap);
            var revisions = new string[revisionIds.Count];
            for (var index = 0; index < revisions.Length; index++)
            {
                CheckBudget();
                revisions[index] = revisionIds[index];
            }
            if (revisions.Any(x => !ValidRevision(x)) || revisions.Distinct(StringComparer.Ordinal).Count() != revisions.Length)
                throw Failure(RuntimeStartupFailure.InvalidBootstrap);
            using var total = new CancellationTokenSource(Remaining(start, options.TotalTimeout), clock);
            totalCancellation = total.Token;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, total.Token);
            var aggregate = 0;
            foreach (var revision in revisions)
            {
                var ceiling = options.InitialBackoff;
                while (true)
                {
                    CheckBudget();
                    byte[]? credential = null;
                    try
                    {
                        credential = await ReadCredentialAsync(projectedTokenPath, lifetime.Token).ConfigureAwait(false);
                        CheckBudget();
                        var duration = Min(options.RpcTimeout, Remaining(start, options.TotalTimeout));
                        using var rpcTimer = new CancellationTokenSource(duration, clock);
                        using var rpc = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, rpcTimer.Token);
                        var headers = new Metadata { { "authorization", "Bearer " + Encoding.ASCII.GetString(credential) } };
                        var deadline = clock.GetUtcNow().UtcDateTime.Add(duration);
                        CheckBudget();
                        DeliverConfigurationReply reply;
                        try
                        {
                            reply = await deliver(new() { BindingId = bindingId, RevisionId = revision }, headers, deadline, rpc.Token).ConfigureAwait(false);
                        }
                        catch (RpcException error)
                        {
                            CheckBudget();
                            if (!Retryable(error)) throw Failure(Classify(error));
                            reply = null!;
                        }
                        catch (OperationCanceledException) when (rpcTimer.IsCancellationRequested)
                        {
                            CheckBudget();
                            reply = null!;
                        }
                        CheckBudget();
                        if (rpcTimer.IsCancellationRequested) reply = null!;
                        if (reply is not null)
                        {
                            ValidateReply(reply, revision, options.MaximumAggregateBytes - aggregate, () => CheckBudget());
                            var document = new RuntimeStartupDocument(revision, reply.SourceFingerprint, reply.Json.ToByteArray());
                            accepted.Add(document);
                            DocumentAccepted?.Invoke(document);
                            aggregate += reply.Json.Length;
                            CheckBudget();
                            break;
                        }
                    }
                    finally { if (credential is not null) CryptographicOperations.ZeroMemory(credential); }
                    CheckBudget();
                    var delay = Jitter(Min(ceiling, Remaining(start, options.TotalTimeout)), random());
                    await Task.Delay(delay, clock, lifetime.Token).ConfigureAwait(false);
                    ceiling = Grow(ceiling, options.MaximumBackoff);
                }
            }
            CheckBudget();
            var complete = new RuntimeStartupDocuments(accepted.ToArray());
            accepted.Clear();
            return complete;
        }
        catch (RuntimeStartupException)
        {
            CheckBudget();
            throw;
        }
        catch (Exception)
        {
            CheckBudget();
            throw Failure(RuntimeStartupFailure.TransportFailure);
        }
        finally { foreach (var document in accepted) document.Dispose(); }
    }

    private async Task<byte[]> ReadCredentialAsync(string path, CancellationToken token)
    {
        // Even synchronous open cannot block the startup continuation. Never retry an unsettled read.
        var worker = Task.Run(() => read(path, token), CancellationToken.None);
        try { return await worker.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = ClearLateReadAsync(worker);
            throw;
        }
        catch (InvalidDataException) { throw Failure(RuntimeStartupFailure.InvalidCredential); }
        catch (Exception) { throw Failure(RuntimeStartupFailure.CredentialUnavailable); }
    }

    private static async Task ClearLateReadAsync(Task<byte[]> worker)
    {
        try { CryptographicOperations.ZeroMemory(await worker.ConfigureAwait(false)); }
        catch (Exception) { /* Observe without retaining raw file diagnostics. */ }
    }

    private TimeSpan Remaining(long start, TimeSpan total) => total - clock.GetElapsedTime(start);
    private static RuntimeStartupException Failure(RuntimeStartupFailure failure) => new(failure);
    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
    internal static TimeSpan Grow(TimeSpan current, TimeSpan maximum) => current.Ticks >= maximum.Ticks - current.Ticks ? maximum : TimeSpan.FromTicks(current.Ticks * 2);
    internal static TimeSpan Jitter(TimeSpan ceiling, double sample)
    {
        if (!double.IsFinite(sample) || sample is < 0 or > 1) throw Failure(RuntimeStartupFailure.InvalidBootstrap);
        return TimeSpan.FromTicks((long)(ceiling.Ticks * sample));
    }
    internal static bool ValidRevision(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out var id) &&
        value == id.ToString("D") && value[14] == '7' && value[19] is '8' or '9' or 'a' or 'b';

    internal static bool Retryable(RpcException error)
    {
        if (error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded) return true;
        if (error.StatusCode != StatusCode.ResourceExhausted) return false;
        return HasReason(error, RuntimeDeliveryReasons.AdmissionOverload);
    }
    private static bool HasReason(RpcException error, string expected)
    {
        var reasons = error.Trailers.Where(x => x.Key == RuntimeDeliveryReasons.Header || x.Key == RuntimeDeliveryReasons.Header + "-bin").Take(2).ToArray();
        return reasons.Length == 1 && !reasons[0].IsBinary && reasons[0].Value == expected;
    }
    private static RuntimeStartupFailure Classify(RpcException error) => error.StatusCode switch
    {
        StatusCode.Unauthenticated => RuntimeStartupFailure.IdentityRefused,
        StatusCode.PermissionDenied => RuntimeStartupFailure.AccessDenied,
        StatusCode.FailedPrecondition => RuntimeStartupFailure.ConfigurationUnavailable,
        StatusCode.ResourceExhausted when HasReason(error, RuntimeDeliveryReasons.OutputLimit) => RuntimeStartupFailure.OutputLimit,
        _ => RuntimeStartupFailure.TransportFailure
    };

    internal static void ValidateReply(DeliverConfigurationReply reply, string revision, int remainingBytes, Action check)
    {
        check();
        if (reply.CalculateSize() > MaximumResponseBytes || reply.Json.Length > MaximumJsonBytes || reply.Json.Length > remainingBytes)
            throw Failure(RuntimeStartupFailure.OutputLimit);
        const string prefix = "sha256:configuration-revision-v1:";
        if (reply.RevisionId != revision || !reply.SourceFingerprint.StartsWith(prefix, StringComparison.Ordinal) ||
            reply.SourceFingerprint.Length != prefix.Length + 64 || reply.SourceFingerprint.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") >= 0 ||
            reply.Json.IsEmpty) throw Failure(RuntimeStartupFailure.InvalidResponse);
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(reply.Json.Span);
            check();
            var reader = new Utf8JsonReader(reply.Json.Span, new JsonReaderOptions { MaxDepth = 64 });
            while (reader.Read()) { check(); }
            if (reader.BytesConsumed != reply.Json.Length || reader.TokenType == JsonTokenType.None) throw new JsonException();
        }
        catch (JsonException) { throw Failure(RuntimeStartupFailure.InvalidResponse); }
        catch (DecoderFallbackException) { throw Failure(RuntimeStartupFailure.InvalidResponse); }
        check();
    }
}
