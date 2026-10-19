namespace Haby.Domain;

/// <summary>A reference pinned to one immutable Secret version; contains no secret material.</summary>
public sealed record SecretRef
{
    public SecretRef(SecretId secretId, SecretVersionId secretVersionId)
    {
        SecretId = secretId ?? throw new ArgumentNullException(nameof(secretId));
        SecretVersionId = secretVersionId ?? throw new ArgumentNullException(nameof(secretVersionId));
    }

    public SecretId SecretId { get; }
    public SecretVersionId SecretVersionId { get; }
}
