using Haby.Domain;

namespace Haby.Application;

/// <summary>One request's exact metadata decision. Never stored, serialized or shared across requests.</summary>
internal sealed class RuntimeConfigurationAuthority(RuntimeAuthenticatedContext identity, Guid permission,
    ConfigurationDocumentRevisionId revision, ConfigurationDocumentTarget target, Guid application, Guid plan,
    long manifestRevision, string fingerprint, string inputFingerprint, Guid checkpoint)
{
    internal RuntimeAuthenticatedContext Identity { get; } = identity;
    internal Guid Permission { get; } = permission;
    internal ConfigurationDocumentRevisionId Revision { get; } = revision;
    internal ConfigurationDocumentTarget Target { get; } = target;
    internal Guid Application { get; } = application;
    internal Guid Plan { get; } = plan;
    internal long ManifestRevision { get; } = manifestRevision;
    internal string Fingerprint { get; } = fingerprint;
    internal string InputFingerprint { get; } = inputFingerprint;
    internal Guid Checkpoint { get; } = checkpoint;
    private int consumed;
    internal void Consume()
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0) throw new InvalidOperationException("Runtime authority already consumed.");
    }
    public override string ToString() => "[Runtime configuration authority]";
}

internal interface IRuntimeConfigurationStore
{
    ValueTask<RuntimeConfigurationAuthority?> AuthorizeAsync(RuntimeAuthenticatedContext identity,
        ConfigurationDocumentRevisionId revision, CancellationToken token);
    ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(RuntimeConfigurationAuthority authority, CancellationToken token);
}

internal enum RuntimeDeliveryOutcome { Delivered, Unauthenticated, PermissionDenied, AdmissionOverload, OutputLimit, Unavailable, FailedPrecondition }

internal sealed class RuntimeDeliveryResult(RuntimeDeliveryOutcome outcome, ConfigurationDeliveryResult? document = null) : IDisposable
{
    internal RuntimeDeliveryOutcome Outcome { get; } = outcome;
    internal ConfigurationDeliveryResult? Document { get; } = document;
    public void Dispose() => Document?.Dispose();
    public override string ToString() => "[Runtime delivery result]";
}

internal sealed class DeliverRuntimeConfiguration(RuntimeAuthentication authentication, IRuntimeConfigurationStore documents,
    ConfigurationDeliveryMaterializer materializer)
{
    internal async ValueTask<RuntimeDeliveryResult> ExecuteAsync(Guid binding, ConfigurationDocumentRevisionId revision,
        ReadOnlyMemory<byte> credential, CancellationToken token)
    {
        try
        {
            var authenticated = await authentication.AuthenticateAsync(binding, credential, token);
            if (authenticated.Outcome != RuntimeAuthenticationOutcome.Authenticated || authenticated.Context is null)
                return new(authenticated.Outcome switch
                {
                    RuntimeAuthenticationOutcome.PermissionDenied => RuntimeDeliveryOutcome.PermissionDenied,
                    RuntimeAuthenticationOutcome.ResourceExhausted => RuntimeDeliveryOutcome.AdmissionOverload,
                    RuntimeAuthenticationOutcome.Unavailable => RuntimeDeliveryOutcome.Unavailable,
                    _ => RuntimeDeliveryOutcome.Unauthenticated
                });
            token.ThrowIfCancellationRequested();
            RuntimeConfigurationAuthority? authority;
            try { authority = await documents.AuthorizeAsync(authenticated.Context, revision, token); }
            catch (InvalidDataException) { return new(RuntimeDeliveryOutcome.PermissionDenied); }
            if (authority is null) return new(RuntimeDeliveryOutcome.PermissionDenied);
            token.ThrowIfCancellationRequested();
            var artifact = await documents.ReadAsync(authority, token);
            if (artifact is null) return new(RuntimeDeliveryOutcome.FailedPrecondition);
            token.ThrowIfCancellationRequested();
            var result = await materializer.ExecuteAsync(artifact, token);
            return new(result.Status switch
            {
                ConfigurationDeliveryStatus.Delivered => RuntimeDeliveryOutcome.Delivered,
                ConfigurationDeliveryStatus.LimitExceeded => RuntimeDeliveryOutcome.OutputLimit,
                ConfigurationDeliveryStatus.StorageUnavailable => RuntimeDeliveryOutcome.Unavailable,
                _ => RuntimeDeliveryOutcome.FailedPrecondition
            }, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (ConfigurationDocumentLimitException) { return new(RuntimeDeliveryOutcome.OutputLimit); }
        catch (ConfigurationDeliveryStorageException) { return new(RuntimeDeliveryOutcome.Unavailable); }
        catch (InvalidDataException) { return new(RuntimeDeliveryOutcome.FailedPrecondition); }
        catch (Exception) { return new(RuntimeDeliveryOutcome.Unavailable); }
    }
}
