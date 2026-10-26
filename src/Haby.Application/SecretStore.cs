using Haby.Domain;

namespace Haby.Application;

/// <summary>Material returned only by an explicitly scoped secret resolution.</summary>
public sealed class SecretMaterial : IDisposable
{
    private readonly byte[] _bytes;

    public SecretMaterial(byte[] bytes) => _bytes = (byte[])(bytes ?? throw new ArgumentNullException(nameof(bytes))).Clone();

    public byte[] CopyBytes() => (byte[])_bytes.Clone();

    public void Dispose() => Array.Clear(_bytes);

    public override string ToString() => nameof(SecretMaterial);
}

/// <summary>Internal storage boundary. The caller supplies a trusted Environment identity.</summary>
public interface ISecretStore
{
    ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken cancellationToken);
    ValueTask CreateAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
        CancellationToken cancellationToken);
    ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
        CancellationToken cancellationToken);
    ValueTask<SecretMaterial?> ResolveAsync(EnvironmentRecordId environment, SecretRef reference,
        CancellationToken cancellationToken);
}
