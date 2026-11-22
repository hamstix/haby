namespace Haby.Application;

public sealed record OperatorImportBindingResult(OperatorImportRequestStatus Status, OperatorImportRequestBinding? Binding = null);

/// <summary>Trusted server-only lookup. The full nonsecret binding is never accepted from a public caller.</summary>
public interface IOperatorImportRequestBindings
{
    ValueTask<OperatorImportBindingResult> FindBindingAsync(Guid requestId, CancellationToken token);
}

/// <summary>Prepares new names server-side; retries compare against the original frozen binding without Provider I/O.</summary>
public sealed class CreatePreparedOperatorImport(PrepareResourceImport preparation, IOperatorImportRequestBindings bindings,
    OperatorImportRequests requests)
{
    public async ValueTask<OperatorImportRequestResult> ExecuteAsync(ResourceImportPreparationRequest request,
        OperatorImportCorrectionTarget? correction, SecretMaterial material, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!PrepareResourceImport.Valid(request)) return new(OperatorImportRequestStatus.InvalidInput);
        try { correction?.Validate(request.RequestId); }
        catch (ArgumentException) { return new(OperatorImportRequestStatus.InvalidInput); }
        if (!preparation.Supports(request.Input.Format)) return new(OperatorImportRequestStatus.ImportNotSupported);
        var saved = await bindings.FindBindingAsync(request.RequestId, token);
        OperatorImportRequestBinding binding;
        if (saved.Status == OperatorImportRequestStatus.NotFound)
        {
            var result = await preparation.ExecuteAsync(request, token);
            if (result.Status != ResourceImportPreparationStatus.Prepared || result.Binding is null)
                return new(result.Status switch
                {
                    ResourceImportPreparationStatus.InvalidInput => OperatorImportRequestStatus.InvalidInput,
                    ResourceImportPreparationStatus.RevisionNotFound => OperatorImportRequestStatus.RevisionNotFound,
                    ResourceImportPreparationStatus.ImportNotSupported => OperatorImportRequestStatus.ImportNotSupported,
                    ResourceImportPreparationStatus.ProviderUnavailable => OperatorImportRequestStatus.ProviderUnavailable,
                    ResourceImportPreparationStatus.SecretUnavailable => OperatorImportRequestStatus.SecretUnavailable,
                    ResourceImportPreparationStatus.Conflict => OperatorImportRequestStatus.Conflict,
                    ResourceImportPreparationStatus.Connectivity => OperatorImportRequestStatus.Connectivity,
                    _ => OperatorImportRequestStatus.StorageUnavailable
                });
            binding = result.Binding with { Correction = correction };
        }
        else if (saved.Status is OperatorImportRequestStatus.Pending or OperatorImportRequestStatus.Created or OperatorImportRequestStatus.Existing or OperatorImportRequestStatus.Superseded)
        {
            if (saved.Binding is null) return new(OperatorImportRequestStatus.StorageUnavailable);
            binding = saved.Binding;
        }
        else return new(saved.Status);
        if (binding.Correction != correction || !preparation.Matches(request, binding)) return new(OperatorImportRequestStatus.Conflict);
        return await requests.CreateAsync(binding, material, actor, token);
    }
}
