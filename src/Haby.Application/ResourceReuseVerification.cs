using Haby.Domain;

namespace Haby.Application;

public enum ResourceReuseValidationStatus { Confirmed, Conflict, InputUnavailable, Connectivity }

/// <summary>Explicit activation binds a returned verification to the caller's async invocation.</summary>
public interface IResourceReuseValidationScope : IDisposable
{
    void Activate();
}

/// <summary>One execution invocation's verification scope, never a health cache or persisted receipt.
/// Dispose it after that invocation's consequential transition or refusal.</summary>
public sealed class ResourceReuseValidationResult(ResourceReuseValidationStatus status, IDisposable? scope = null) : IDisposable
{
    public ResourceReuseValidationStatus Status { get; } = status;
    public void ActivateScope()
    {
        if (Status != ResourceReuseValidationStatus.Confirmed) throw new InvalidOperationException("Reuse verification is not confirmed.");
        if (scope is IResourceReuseValidationScope invocation) invocation.Activate();
    }
    public DurableExecutionOutcome FailureOutcome => Status switch
    {
        ResourceReuseValidationStatus.Conflict => DurableExecutionOutcome.Failed,
        ResourceReuseValidationStatus.Connectivity => DurableExecutionOutcome.RetryableFailure,
        _ => DurableExecutionOutcome.InputUnavailable
    };
    public void Dispose() => scope?.Dispose();
    public override string ToString() => Status.ToString();
}

/// <summary>Explicit exact-type/mode capability. Called outside catalog transactions before later use.
/// Historical import receipts and normal delivery do not acquire a continuous health requirement.</summary>
public interface IDurableResourceReuseVerifier
{
    DurableActionAdmissionKey Key { get; }
    ValueTask<ResourceReuseValidationStatus> VerifyAsync(AcceptedOperation original, ResourceImportReceipt receipt,
        CancellationToken cancellationToken);
}
