using System.Security.Cryptography;
using System.Text;

namespace Haby.Application;

/// <summary>Safe operator-admin grant metadata. Identity, never display name, establishes authority.</summary>
public sealed record OperatorAccessGrant(Guid Id, string? DisplayName, string Actor, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, string? RevokedBy, DateTimeOffset? RevokedAt);

/// <summary>One-time sensitive result; never log or present as ordinary metadata.</summary>
public sealed class OperatorAccessCreated(OperatorAccessGrant grant, string credential)
{
    public OperatorAccessGrant Grant { get; } = grant;
    public string Credential { get; } = credential;
    public override string ToString() => "[Operator access created]";
}

public interface IOperatorAccessStore
{
    ValueTask<OperatorAccessCreated> CreateAsync(string? displayName, string actor, DateTimeOffset? expiresAt, CancellationToken token);
    ValueTask<OperatorAccessGrant?> GetAsync(Guid id, CancellationToken token);
    ValueTask<IReadOnlyList<OperatorAccessGrant>> ListAsync(Guid? after, int limit, CancellationToken token);
    ValueTask<OperatorAccessGrant?> RevokeAsync(Guid id, string actor, CancellationToken token);
    ValueTask<OperatorAccessGrant?> AuthorizeAsync(string credential, CancellationToken token);
}

/// <summary>Closed operator-only credential format containing 256 random secret bits.</summary>
public static class OperatorCredential
{
    public const string Prefix = "haby_operator_v1.";
    public static string Actor(Guid id) => "operator-grant:" + id.ToString("D");
    public static string Create(Guid id)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try { return Prefix + id.ToString("N") + "." + Convert.ToHexStringLower(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static bool TryIdentify(string value, out Guid id)
    {
        id = default;
        return value.Length == Prefix.Length + 97 && value.StartsWith(Prefix, StringComparison.Ordinal) &&
            value[Prefix.Length + 32] == '.' && value.AsSpan(Prefix.Length + 33).IndexOfAnyExcept("0123456789abcdef") < 0 &&
            Guid.TryParseExact(value.AsSpan(Prefix.Length, 32), "N", out id) && id != Guid.Empty;
    }
    public static byte[] Hash(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static bool Verify(string value, byte[] hash)
    {
        var candidate = Hash(value);
        try { return hash.Length == 32 && CryptographicOperations.FixedTimeEquals(candidate, hash); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }
}
