using System.Security.Cryptography;

namespace Haby.Application;

internal enum RuntimeAuthenticationOutcome { Authenticated, Unauthenticated, PermissionDenied, Unavailable, ResourceExhausted }
internal sealed record RuntimeAuthenticatedContext(Guid Binding, Guid Environment, Guid ManagedWorkload, Guid Provider);
internal sealed record RuntimeAuthenticationResult(RuntimeAuthenticationOutcome Outcome, RuntimeAuthenticatedContext? Context = null)
{
    internal string Code => Outcome switch
    {
        RuntimeAuthenticationOutcome.Authenticated => "runtime-authenticated",
        RuntimeAuthenticationOutcome.Unauthenticated => "runtime-identity-refused",
        RuntimeAuthenticationOutcome.PermissionDenied => "runtime-binding-denied",
        RuntimeAuthenticationOutcome.ResourceExhausted => "runtime-authentication-overload",
        _ => "runtime-authentication-indeterminate"
    };
}

// Only trusted catalog metadata crosses this boundary. No document or material lookup contract.
internal sealed record RuntimeTrust(Guid Binding, Guid Installation, Guid Environment, Guid ManagedWorkload, Guid Provider,
    long Configuration, string Implementation, string ProviderKind, string ConfigurationShape, int ConfigurationFormat,
    string ConfigurationPayload, string Authenticator, string Audience, string Contract, int Format, string Payload,
    string InventoryContract, int InventoryFormat, string InventoryPayload, bool Revoked);
internal interface IRuntimeIdentityStore
{
    ValueTask<RuntimeTrust?> ReadAsync(Guid binding, CancellationToken token);
    ValueTask<bool> IsCurrentAsync(RuntimeTrust expected, CancellationToken token);
}
internal sealed record RuntimeAuthenticatorContract(string Authenticator, string Contract, int Format,
    string ProviderKind, string ConfigurationShape, int ConfigurationFormat, string Implementation);
internal interface IRuntimeAuthenticator
{
    RuntimeAuthenticatorContract Contract { get; }
    // Pure: must complete before resolving administrator material or opening a connection.
    bool Validate(RuntimeTrust trust);
    ValueTask<RuntimeAuthenticationOutcome> AuthenticateAsync(RuntimeTrust trust, ReadOnlyMemory<byte> credential, CancellationToken token);
}

internal sealed class RuntimeAuthentication(IRuntimeIdentityStore store, IEnumerable<IRuntimeAuthenticator> registrations,
    RuntimeAuthenticationAdmission admission, TimeProvider clock)
{
    internal const int MaximumCredentialBytes = 16384;
    private readonly IRuntimeAuthenticator[] authenticators = ValidateRegistrations(registrations);
    private static IRuntimeAuthenticator[] ValidateRegistrations(IEnumerable<IRuntimeAuthenticator> values)
    {
        var result = values.ToArray();
        if (result.Any(x => x.Contract is null) || result.GroupBy(x => x.Contract).Any(x => x.Count() != 1))
            throw new InvalidOperationException("Ambiguous runtime authenticator registration.");
        return result;
    }
    internal async ValueTask<RuntimeAuthenticationResult> AuthenticateAsync(Guid binding, ReadOnlyMemory<byte> credential, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (binding == Guid.Empty || !ValidCredential(credential.Span)) return new(RuntimeAuthenticationOutcome.Unauthenticated);
        using var ticket = admission.Enter(token);
        if (ticket is null) return new(RuntimeAuthenticationOutcome.ResourceExhausted);
        // Admission precedes ownership/copy. A bounded request never appears in a record ToString.
        var owned = credential.ToArray();
        try
        {
            await ticket.LookupAsync(token);
            if (ticket.Remaining <= TimeSpan.Zero) throw new RuntimeAdmissionException();
            using var queueDeadline = new CancellationTokenSource(ticket.Remaining, clock);
            using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, queueDeadline.Token);
            var trust = await store.ReadAsync(binding, lookupCancellation.Token);
            if (trust is null) return new(RuntimeAuthenticationOutcome.Unauthenticated);
            var key = new RuntimeAuthenticatorContract(trust.Authenticator, trust.Contract, trust.Format,
                trust.ProviderKind, trust.ConfigurationShape, trust.ConfigurationFormat, trust.Implementation);
            var adapter = authenticators.SingleOrDefault(x => x.Contract == key);
            if (adapter is null || !adapter.Validate(trust)) return new(RuntimeAuthenticationOutcome.Unauthenticated);
            if (trust.Revoked) return new(RuntimeAuthenticationOutcome.PermissionDenied);
            await ticket.ProviderAsync(trust.Provider, token);
            using var deadline = new CancellationTokenSource(admission.Options.CallTimeout, clock);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var result = await adapter.AuthenticateAsync(trust, owned, call.Token);
            call.Token.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(result)) return new(RuntimeAuthenticationOutcome.Unavailable);
            if (result != RuntimeAuthenticationOutcome.Authenticated) return new(result);
            // New catalog snapshot after the remote call. No transaction spans I/O.
            var current = await store.IsCurrentAsync(trust, call.Token);
            call.Token.ThrowIfCancellationRequested();
            return current ? new(RuntimeAuthenticationOutcome.Authenticated,
                new(binding, trust.Environment, trust.ManagedWorkload, trust.Provider)) : new(RuntimeAuthenticationOutcome.PermissionDenied);
        }
        catch (RuntimeAdmissionException) { return new(RuntimeAuthenticationOutcome.ResourceExhausted); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(ticket.Dispatched ? RuntimeAuthenticationOutcome.Unavailable : RuntimeAuthenticationOutcome.ResourceExhausted); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or System.Text.Json.JsonException or NullReferenceException)
        { return new(RuntimeAuthenticationOutcome.Unauthenticated); }
        catch (Exception) { return new(RuntimeAuthenticationOutcome.Unavailable); }
        finally { CryptographicOperations.ZeroMemory(owned); }
    }
    private static bool ValidCredential(ReadOnlySpan<byte> value)
    {
        if (value.Length is 0 or > MaximumCredentialBytes) return false;
        foreach (var c in value)
            if (!(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9') && !"-._~+/="u8.Contains(c)) return false;
        return true;
    }
}
