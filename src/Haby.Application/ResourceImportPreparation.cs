using System.Text;
using Haby.Domain;

namespace Haby.Application;

public enum ResourceImportPreparationStatus
{
    Prepared, InvalidInput, RevisionNotFound, ImportNotSupported, ProviderUnavailable,
    SecretUnavailable, Conflict, Connectivity
}

public sealed record ResourceImportPreparationRequest(Guid RequestId, EnvironmentRecordId Environment,
    ManifestRevisionRef Revision, DurableActionPayload Input);

public sealed record ResourceImportPreparationResult(ResourceImportPreparationStatus Status,
    OperatorImportRequestBinding? Binding = null);

/// <summary>Adapter-owned, read-only preparation. No credential staging or desired-state acceptance.</summary>
public interface IResourceImportPreparer
{
    string InputFormat { get; }
    ValueTask<ResourceImportPreparationResult> PrepareAsync(ResourceImportPreparationRequest request,
        NormalizedManifestSpec spec, CancellationToken token);
}

/// <summary>Trusted callers authenticate and derive actor identity at the public transport boundary.</summary>
public sealed class PrepareResourceImport(IManifestRevisionQuery revisions, IEnumerable<IResourceImportPreparer> preparers)
{
    private readonly IReadOnlyDictionary<string, IResourceImportPreparer> _preparers =
        preparers.ToDictionary(x => x.InputFormat, StringComparer.Ordinal);

    public async ValueTask<ResourceImportPreparationResult> ExecuteAsync(ResourceImportPreparationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request is null || request.RequestId == Guid.Empty || request.Environment is null || request.Revision is null ||
            request.Input is null || string.IsNullOrWhiteSpace(request.Input.Format) || request.Input.Format.Length > 128 ||
            request.Input.Json is null || Encoding.UTF8.GetByteCount(request.Input.Json) > 65536)
            return new(ResourceImportPreparationStatus.InvalidInput);
        if (!_preparers.TryGetValue(request.Input.Format, out var preparer)) return new(ResourceImportPreparationStatus.ImportNotSupported);
        var revision = await revisions.FindAsync(request.Revision, token);
        if (revision is null) return new(ResourceImportPreparationStatus.RevisionNotFound);
        var result = await preparer.PrepareAsync(request, revision.Declaration.Spec, token);
        if (result.Status != ResourceImportPreparationStatus.Prepared) return result with { Binding = null };
        try
        {
            var binding = result.Binding ?? throw new InvalidDataException("Prepared binding is unavailable.");
            binding.Validate();
            binding.Input.Validate(revision.Declaration.Spec);
            if (binding.RequestId != request.RequestId || binding.Environment != request.Environment || binding.Revision != request.Revision)
                return new(ResourceImportPreparationStatus.ImportNotSupported);
            return result;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return new(ResourceImportPreparationStatus.ImportNotSupported); }
    }
}
