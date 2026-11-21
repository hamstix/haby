using Haby.Domain;

namespace Haby.Application;

/// <summary>Trusted, adapter-validated nonsecret intent. Public preparation is a separate boundary.</summary>
public sealed record OperatorImportRequestBinding(Guid RequestId, EnvironmentRecordId Environment,
    ManifestRevisionRef Revision, ManifestLocalId CredentialResource, ResourceImportIntent Input)
{
    public ManagedResourceKey CredentialOwner => new(Environment, Revision.ApplicationRecordId, CredentialResource);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Environment);
        ArgumentNullException.ThrowIfNull(Revision);
        ArgumentNullException.ThrowIfNull(CredentialResource);
        ArgumentNullException.ThrowIfNull(Input);
        if (RequestId == Guid.Empty || Input.Resources.Any(x => x.Credential is not null) ||
            !Input.Resources.Any(x => x.Resource == CredentialResource))
            throw new ArgumentException("Invalid operator import request binding.");
        _ = ResourceImportIntentCodec.Encode(Input);
    }

    public ResourceImportIntent Attach(ProtectedCredentialReference reference) => new(Input.Resources.Select(x =>
        x.Resource == CredentialResource ? x with { Credential = reference } : x));
}

/// <summary>Safe request metadata. Pending means no linked ChangeSet, not successful credential staging.</summary>
public sealed record OperatorImportRequestView(Guid RequestId, EnvironmentRecordId Environment,
    ManifestRevisionRef Revision, ManifestLocalId CredentialResource, DateTimeOffset CreatedAt, string CreatedBy,
    DeploymentChangeSetId? ChangeSetId, DateTimeOffset? CompletedAt, string? CompletedBy);

public enum OperatorImportRequestStatus
{
    Pending, Created, Existing, NotFound, InvalidInput, EnvironmentNotFound, RevisionNotFound,
    ImportNotSupported, Conflict, StorageUnavailable
}

public sealed record OperatorImportRequestResult(OperatorImportRequestStatus Status, OperatorImportRequestView? Request = null);

/// <summary>Immutable binding first; ChangeSet creation and linkage then share one catalog transaction.</summary>
public interface IOperatorImportRequestStore
{
    ValueTask<OperatorImportRequestResult> BindAsync(OperatorImportRequestBinding binding, string actor, CancellationToken token);
    ValueTask<OperatorImportRequestResult> CompleteAsync(OperatorImportRequestBinding binding,
        ProtectedCredentialReference credential, string actor, CancellationToken token);
    ValueTask<OperatorImportRequestResult> GetAsync(Guid requestId, CancellationToken token);
}

/// <summary>No public transport or adapter preparation policy. Callers authenticate and validate adapter support.</summary>
public sealed class OperatorImportRequests(IOperatorImportRequestStore requests, IProtectedApplicationCredentialIntake credentials)
{
    public async ValueTask<OperatorImportRequestResult> CreateAsync(OperatorImportRequestBinding binding,
        SecretMaterial material, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { ArgumentNullException.ThrowIfNull(binding); binding.Validate(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return new(OperatorImportRequestStatus.InvalidInput); }
        var bound = await requests.BindAsync(binding, actor, token);
        if (bound.Status is not (OperatorImportRequestStatus.Pending or OperatorImportRequestStatus.Created or OperatorImportRequestStatus.Existing))
            return bound;
        // Even a linked request must verify the exact-byte replay before acknowledging Create again.
        var staged = await credentials.StageAsync(binding.RequestId, binding.CredentialOwner, material, token);
        if (staged.Status is not (ProtectedCredentialStageStatus.Staged or ProtectedCredentialStageStatus.Existing))
            return new(staged.Status switch
            {
                ProtectedCredentialStageStatus.Conflict => OperatorImportRequestStatus.Conflict,
                ProtectedCredentialStageStatus.InvalidInput => OperatorImportRequestStatus.InvalidInput,
                _ => OperatorImportRequestStatus.StorageUnavailable
            }, bound.Request);
        if (staged.Reference is null) return new(OperatorImportRequestStatus.StorageUnavailable, bound.Request);
        token.ThrowIfCancellationRequested();
        return await requests.CompleteAsync(binding, staged.Reference, actor, token);
    }

    public ValueTask<OperatorImportRequestResult> GetAsync(Guid requestId, CancellationToken token) =>
        requests.GetAsync(requestId, token);
}
