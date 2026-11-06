using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>An exact immutable declaration and its trusted first-import audit.</summary>
public sealed record AcceptedConfigurationProfile(ConfigurationProfileRevision Declaration, ImportAuditMetadata AuditMetadata)
{
    public ProfileRevisionRef Reference => Declaration.Reference;
}

public sealed record ProfileCompareAndCreateResult(AcceptedConfigurationProfile Revision, bool Created);

/// <summary>Atomically accepts one validated declaration or returns the existing full revision.</summary>
public interface IConfigurationProfileImportStore
{
    ValueTask<ProfileCompareAndCreateResult> CompareAndCreateAsync(ConfigurationProfileRevision candidate,
        ImportAuditMetadata auditMetadata, CancellationToken cancellationToken);
}

/// <summary>Exact lookup only; unsupported or corrupt stored data must fail visibly.</summary>
public interface IConfigurationProfileRevisionQuery
{
    ValueTask<AcceptedConfigurationProfile?> FindAsync(ProfileRevisionRef reference, CancellationToken cancellationToken = default);
}

public abstract record ImportConfigurationProfileResult
{
    private ImportConfigurationProfileResult() { }
    public enum ImportDisposition { Created, ExistingEquivalent }
    public sealed record Succeeded(AcceptedConfigurationProfile Revision, ImportDisposition Disposition) : ImportConfigurationProfileResult;
    public sealed record InvalidDeclaration(ProfileWireResult<ConfigurationProfileRevision> Report) : ImportConfigurationProfileResult;
    public sealed record RevisionContentConflict(ProfileRevisionRef Reference) : ImportConfigurationProfileResult;
}

/// <summary>Side-effect-free Profile intake: parsing and catalog acceptance never resolve references.</summary>
public sealed class ImportConfigurationProfile(IConfigurationProfileImportStore store, TimeProvider clock)
{
    public async ValueTask<ImportConfigurationProfileResult> ExecuteAsync(ReadOnlyMemory<byte> payload, string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actorId);
        if (actorId.Length is 0 or > 256 || actorId.Any(char.IsControl))
            throw new ArgumentException("Actor identity must be bounded and nonempty.", nameof(actorId));
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = ValidateConfigurationProfile.Execute(payload, cancellationToken);
        if (!parsed.IsSuccess) return new ImportConfigurationProfileResult.InvalidDeclaration(parsed);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await store.CompareAndCreateAsync(parsed.Declaration!, new(actorId, clock.GetUtcNow()), cancellationToken);
        // The store's return confirms durable acceptance; late cancellation cannot discard that result.
        return result.Created || result.Revision.Declaration.Equals(parsed.Declaration)
            ? new ImportConfigurationProfileResult.Succeeded(result.Revision, result.Created
                ? ImportConfigurationProfileResult.ImportDisposition.Created
                : ImportConfigurationProfileResult.ImportDisposition.ExistingEquivalent)
            : new ImportConfigurationProfileResult.RevisionContentConflict(result.Revision.Reference);
    }
}
