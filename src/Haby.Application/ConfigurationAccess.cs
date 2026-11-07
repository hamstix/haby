using System.Security.Cryptography;
using System.Text;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Safe immutable grant scope. The credential verifier is deliberately absent.</summary>
public sealed record ConfigurationAccessGrant(Guid Id, ConfigurationDocumentMetadata Document,
    string Actor, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? RevokedBy, DateTimeOffset? RevokedAt);

/// <summary>One-time sensitive result. Never send through an ordinary metadata presenter.</summary>
public sealed class ConfigurationAccessCreated(ConfigurationAccessGrant grant, string credential)
{
    public ConfigurationAccessGrant Grant { get; } = grant;
    public string Credential { get; } = credential;
    public override string ToString() => "[Configuration access created]";
}

public interface IConfigurationAccessStore
{
    ValueTask<ConfigurationAccessCreated?> CreateAsync(ConfigurationDocumentSelection selection, string actor,
        DateTimeOffset? expiresAt, CancellationToken token);
    ValueTask<ConfigurationAccessGrant?> GetAsync(Guid id, CancellationToken token);
    ValueTask<ConfigurationAccessGrant?> RevokeAsync(Guid id, string actor, CancellationToken token);
    /// <summary>Reads only credential/scope metadata. No artifact or material access before this succeeds.</summary>
    ValueTask<ConfigurationAccessGrant?> AuthorizeAsync(string credential, ConfigurationDocumentRevisionId revision, CancellationToken token);
}

/// <summary>Credential purpose is fixed by a closed format, separate from future operator credentials.</summary>
public static class ConfigurationDeliveryCredential
{
    public const string Prefix = "haby_delivery_v1.";
    public static string Create(Guid grant)
    {
        var entropy = RandomNumberGenerator.GetBytes(32);
        try { return Prefix + grant.ToString("N") + "." + Convert.ToHexStringLower(entropy); }
        finally { CryptographicOperations.ZeroMemory(entropy); }
    }
    public static bool TryIdentify(string value, out Guid grant)
    {
        grant = default;
        return value.Length == Prefix.Length + 32 + 1 + 64 && value.StartsWith(Prefix, StringComparison.Ordinal) &&
            value[Prefix.Length + 32] == '.' && value.AsSpan(Prefix.Length + 33).IndexOfAnyExcept("0123456789abcdef") < 0 &&
            Guid.TryParseExact(value.AsSpan(Prefix.Length, 32), "N", out grant) && grant != Guid.Empty;
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

public sealed record ConfigurationDeliveryArtifact(ConfigurationDocumentRevision Revision, EnvironmentRecordId Environment);

public interface IConfigurationDeliveryDocumentStore
{
    ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(ConfigurationAccessGrant grant, CancellationToken token);
}
