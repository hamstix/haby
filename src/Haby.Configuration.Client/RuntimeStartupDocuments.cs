using System.Security.Cryptography;

namespace Haby.Configuration.Client;

/// <summary>Closed startup failures. No transport details, paths or sensitive content are retained.</summary>
public enum RuntimeStartupFailure
{
    InvalidBootstrap, CredentialUnavailable, InvalidCredential, InvalidResponse, OutputLimit,
    IdentityRefused, AccessDenied, ConfigurationUnavailable, TransportFailure, CallerCancelled, StartupDeadlineExceeded
}

public sealed class RuntimeStartupException(RuntimeStartupFailure failure) : Exception("Runtime configuration startup refused.")
{
    public RuntimeStartupFailure Failure { get; } = failure;
    public override string ToString() => $"Runtime configuration startup: {Failure}.";
}

/// <summary>Owned transient raw JSON. Dispose the enclosing set when application initialization is complete.</summary>
public sealed class RuntimeStartupDocument : IDisposable
{
    private byte[]? bytes;
    internal RuntimeStartupDocument(string revisionId, string sourceFingerprint, byte[] json)
    { RevisionId = revisionId; SourceFingerprint = sourceFingerprint; bytes = json; }
    public string RevisionId { get; }
    public string SourceFingerprint { get; }
    public ReadOnlyMemory<byte> Json => bytes ?? throw new ObjectDisposedException(nameof(RuntimeStartupDocument));
    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref bytes, null);
        if (owned is not null) CryptographicOperations.ZeroMemory(owned);
    }
    public override string ToString() => "[Runtime configuration document: redacted]";
}

/// <summary>Complete mandatory set only. Disposing clears owned bytes, including previously obtained memory views.</summary>
public sealed class RuntimeStartupDocuments : IDisposable
{
    private IReadOnlyList<RuntimeStartupDocument>? documents;
    internal RuntimeStartupDocuments(RuntimeStartupDocument[] owned) => documents = Array.AsReadOnly(owned);
    public IReadOnlyList<RuntimeStartupDocument> Documents => documents ?? throw new ObjectDisposedException(nameof(RuntimeStartupDocuments));
    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref documents, null);
        if (owned is not null) foreach (var document in owned) document.Dispose();
    }
    public override string ToString() => "[Complete runtime configuration: redacted]";
}
