using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Haby.Operator.Contracts;

/// <summary>Versioned correlation keys; no authority, list idempotency or resume guarantee.</summary>
public static class BatchObservationKeys
{
    public static string CanonicalDeployment(string value)
    {
        if (!Guid.TryParse(value, out var id))
            throw new ArgumentException("Invalid deployment identity.");
        var canonical = id.ToString("D");
        if (canonical[14] != '7' || canonical[19] is not ('8' or '9' or 'a' or 'b')) throw new ArgumentException("Invalid deployment identity.");
        return canonical;
    }
    public static string Member(string batch, string deployment, long generation)
    {
        if (string.IsNullOrEmpty(batch) || batch.Length > 128 || batch.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) || generation <= 0)
            throw new ArgumentException("Invalid batch identity.");
        var domain = Encoding.ASCII.GetBytes("haby.batch-wait/v1");
        var key = Encoding.UTF8.GetBytes(batch);
        var reference = Encoding.ASCII.GetBytes(CanonicalDeployment(deployment));
        var bytes = new byte[domain.Length + 4 + key.Length + 36 + 8];
        domain.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(domain.Length), (uint)key.Length);
        key.CopyTo(bytes, domain.Length + 4); reference.CopyTo(bytes, domain.Length + 4 + key.Length);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(bytes.Length - 8), (ulong)generation);
        return "bw1-" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
