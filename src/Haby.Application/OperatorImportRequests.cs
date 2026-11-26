using Haby.Domain;

namespace Haby.Application;

public enum OperatorImportCorrectionReason { CredentialRejected, InputCorrection, ExplicitRetry }

public sealed record OperatorImportCorrectionTarget(Guid PreviousRequestId, ApplicationDeploymentId? Deployment,
    DeploymentGeneration? Generation, OperatorImportCorrectionReason Reason)
{
    public void Validate(Guid requestId)
    {
        if (PreviousRequestId == Guid.Empty || PreviousRequestId == requestId ||
            (Deployment is null) != (Generation is null) || Reason is not
                (OperatorImportCorrectionReason.CredentialRejected or OperatorImportCorrectionReason.InputCorrection or OperatorImportCorrectionReason.ExplicitRetry))
            throw new ArgumentException("Invalid operator import correction target.");
    }
}

/// <summary>Trusted, adapter-validated nonsecret intent. Public preparation is a separate boundary.</summary>
public sealed record OperatorImportRequestBinding(Guid RequestId, EnvironmentRecordId Environment,
    ManifestRevisionRef Revision, ManifestLocalId CredentialResource, ResourceImportIntent Input,
    OperatorImportCorrectionTarget? Correction = null)
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
        Correction?.Validate(RequestId);
    }

    public ResourceImportIntent Attach(ProtectedCredentialReference reference) => new(Input.Resources.Select(x =>
        x.Resource == CredentialResource ? x with { Credential = reference } : x));
}

/// <summary>Safe request metadata. Pending means no linked ChangeSet, not successful credential staging.</summary>
public sealed record OperatorImportRequestView(Guid RequestId, EnvironmentRecordId Environment,
    ManifestRevisionRef Revision, ManifestLocalId CredentialResource, DateTimeOffset CreatedAt, string CreatedBy,
    DeploymentChangeSetId? ChangeSetId, DateTimeOffset? CompletedAt, string? CompletedBy,
    OperatorImportCorrectionTarget? Correction = null, Guid? SupersededByRequestId = null);

public enum OperatorImportRequestStatus
{
    Pending, Created, Existing, NotFound, InvalidInput, EnvironmentNotFound, RevisionNotFound,
    ImportNotSupported, Conflict, StorageUnavailable, Superseded, ProviderUnavailable, SecretUnavailable, Connectivity
}

public sealed record OperatorImportRequestResult(OperatorImportRequestStatus Status, OperatorImportRequestView? Request = null);

/// <summary>Immutable binding first; ChangeSet creation and linkage then share one catalog transaction.</summary>
public interface IOperatorImportRequestStore
{
    ValueTask<OperatorImportRequestResult> BindAsync(OperatorImportRequestBinding binding, string actor, CancellationToken token);
    /// <summary>Explicit protected group intake; implementations without subset support refuse before staging.</summary>
    ValueTask<OperatorImportRequestResult> BindGroupAsync(OperatorImportRequestBinding binding, string actor, CancellationToken token) =>
        ValueTask.FromResult(new OperatorImportRequestResult(OperatorImportRequestStatus.ImportNotSupported));
    ValueTask<OperatorImportRequestResult> CompleteAsync(OperatorImportRequestBinding binding,
        ProtectedCredentialReference credential, string actor, CancellationToken token);
    ValueTask<OperatorImportRequestResult> GetAsync(Guid requestId, CancellationToken token);
}

/// <summary>No public transport or adapter preparation policy. Callers authenticate and validate adapter support.</summary>
public interface IProtectedApplicationCredentialCandidateIntake
{
    ValueTask<ProtectedCredentialStageResult> StageCandidateAsync(OperatorImportRequestBinding binding,
        SecretMaterial material, CancellationToken token);
}

public sealed class OperatorImportRequests(IOperatorImportRequestStore requests, IProtectedApplicationCredentialIntake credentials,
    IProtectedApplicationCredentialCandidateIntake? candidates = null)
{
    public async ValueTask<OperatorImportRequestResult> CreateAsync(OperatorImportRequestBinding binding,
        SecretMaterial material, string actor, CancellationToken token, bool stageOnly = false)
    {
        token.ThrowIfCancellationRequested();
        try { ArgumentNullException.ThrowIfNull(binding); binding.Validate(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return new(OperatorImportRequestStatus.InvalidInput); }
        if (binding.Correction is not null && candidates is null) return new(OperatorImportRequestStatus.ImportNotSupported);
        var bound = stageOnly ? await requests.BindGroupAsync(binding, actor, token) : await requests.BindAsync(binding, actor, token);
        if (bound.Status is not (OperatorImportRequestStatus.Pending or OperatorImportRequestStatus.Created or OperatorImportRequestStatus.Existing))
            return bound;
        // Even a linked request must verify the exact-byte replay before acknowledging Create again.
        var staged = binding.Correction is null
            ? await credentials.StageAsync(binding.RequestId, binding.CredentialOwner, material, token)
            : await candidates!.StageCandidateAsync(binding, material, token);
        if (staged.Status is not (ProtectedCredentialStageStatus.Staged or ProtectedCredentialStageStatus.Existing))
            return new(staged.Status switch
            {
                ProtectedCredentialStageStatus.Conflict => OperatorImportRequestStatus.Conflict,
                ProtectedCredentialStageStatus.InvalidInput => OperatorImportRequestStatus.InvalidInput,
                _ => OperatorImportRequestStatus.StorageUnavailable
            }, bound.Request);
        if (staged.Reference is null) return new(OperatorImportRequestStatus.StorageUnavailable, bound.Request);
        token.ThrowIfCancellationRequested();
        return stageOnly ? bound : await requests.CompleteAsync(binding, staged.Reference, actor, token);
    }

    public ValueTask<OperatorImportRequestResult> GetAsync(Guid requestId, CancellationToken token) =>
        requests.GetAsync(requestId, token);
}
