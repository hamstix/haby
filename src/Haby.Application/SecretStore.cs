using Haby.Domain;

namespace Haby.Application;

/// <summary>Material returned only by an explicitly scoped secret resolution.</summary>
public sealed class SecretMaterial : IDisposable
{
    private readonly byte[] _bytes;
    private readonly object _gate = new();
    private bool _disposed;

    public SecretMaterial(byte[] bytes) => _bytes = (byte[])(bytes ?? throw new ArgumentNullException(nameof(bytes))).Clone();

    public byte[] CopyBytes()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (byte[])_bytes.Clone();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Array.Clear(_bytes);
            _disposed = true;
        }
    }

    public override string ToString() => nameof(SecretMaterial);
}

/// <summary>Internal storage boundary. The caller supplies a trusted Environment identity.</summary>
public interface ISecretStore
{
    /// <summary>Compatibility helper: create-or-get metadata only. Prefer EnvironmentAdministration for explicit administration.</summary>
    ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken cancellationToken);
    ValueTask CreateAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
        CancellationToken cancellationToken);
    ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
        CancellationToken cancellationToken);
    ValueTask<SecretMaterial?> ResolveAsync(EnvironmentRecordId environment, SecretRef reference,
        CancellationToken cancellationToken);
}
