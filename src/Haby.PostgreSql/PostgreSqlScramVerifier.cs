using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Haby.PostgreSql;

/// <summary>Only random-password/v1 ASCII material, not a general SASL implementation.
/// RFC 5802 / PostgreSQL 17 scram_build_secret; cryptography stays in the platform.</summary>
internal static partial class PostgreSqlScramVerifier
{
    internal static bool ValidPassword(ReadOnlySpan<byte> password)
    {
        if (password.Length != 43) return false;
        foreach (var b in password)
            if (!(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or
                >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-')) return false;
        return "AEIMQUYcgkosw048".Contains((char)password[^1], StringComparison.Ordinal);
    }

    internal static string Create(ReadOnlySpan<byte> password)
    {
        if (!ValidPassword(password)) throw new ArgumentException("Unsupported generated material.");
        Span<byte> salt = stackalloc byte[16];
        Span<byte> salted = stackalloc byte[32];
        Span<byte> client = stackalloc byte[32];
        Span<byte> stored = stackalloc byte[32];
        Span<byte> server = stackalloc byte[32];
        try
        {
            RandomNumberGenerator.Fill(salt);
            Rfc2898DeriveBytes.Pbkdf2(password, salt, salted, 4096, HashAlgorithmName.SHA256);
            HMACSHA256.HashData(salted, "Client Key"u8, client);
            SHA256.HashData(client, stored);
            HMACSHA256.HashData(salted, "Server Key"u8, server);
            var verifier = $"SCRAM-SHA-256$4096:{Convert.ToBase64String(salt)}${Convert.ToBase64String(stored)}:{Convert.ToBase64String(server)}";
            if (!Format().IsMatch(verifier)) throw new InvalidOperationException("Invalid verifier format.");
            return verifier;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(salted);
            CryptographicOperations.ZeroMemory(client);
            CryptographicOperations.ZeroMemory(stored);
            CryptographicOperations.ZeroMemory(server);
        }
    }

    [GeneratedRegex(@"\ASCRAM-SHA-256\$4096:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=\z", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
}
