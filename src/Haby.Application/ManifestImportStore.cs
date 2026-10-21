using Haby.Domain;
using Haby.Manifest;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Application;

/// <summary>Stable catalog identity, created together with its first revision.</summary>
public sealed record ApplicationRecord(ApplicationId Id, ApplicationRecordId RecordId);

/// <summary>Trusted metadata captured only on the first successful import.</summary>
public sealed record ImportAuditMetadata(string ActorId, DateTimeOffset ImportedAt);

/// <summary>An immutable accepted declaration and its original import history.</summary>
public sealed record ManifestRevision(
    ManifestRevisionId Id,
    ManifestRevisionRef Reference,
    ApplicationId ApplicationId,
    ApplicationVersion ApplicationVersion,
    string InputApiVersion,
    int NormalizedModelVersion,
    NormalizedApplicationManifest Declaration,
    ManifestValidationReport HistoricalWarnings,
    ImportAuditMetadata AuditMetadata);

/// <summary>The atomic store outcome for a fully validated candidate.</summary>
public sealed record ManifestCompareAndCreateResult(ManifestRevision Revision, bool Created);

/// <summary>Atomically creates a revision or returns the accepted revision for the version.</summary>
public interface IManifestImportStore
{
    ValueTask<ManifestCompareAndCreateResult> CompareAndCreateAsync(
        ValidatedManifestCandidate candidate, ImportAuditMetadata auditMetadata, CancellationToken cancellationToken);
}

/// <summary>An in-memory catalog with one atomic boundary for Application and revision creation.</summary>
public sealed class InMemoryManifestImportStore : IManifestImportStore, IManifestRevisionReader
{
    private readonly object _gate = new();
    private readonly Dictionary<ApplicationId, ApplicationRecord> _applications = [];
    private readonly Dictionary<(ApplicationId, ApplicationVersion), ManifestRevision> _byVersion = [];
    private readonly Dictionary<ApplicationId, long> _lastRevision = [];

    /// <summary>A catalog snapshot. Enumeration order is unspecified.</summary>
    public IReadOnlyList<ApplicationRecord> Applications
    {
        get { lock (_gate) return Array.AsReadOnly(_applications.Values.ToArray()); }
    }

    /// <summary>A revision snapshot. Enumeration order is unspecified.</summary>
    public IReadOnlyList<ManifestRevision> Revisions
    {
        get { lock (_gate) return Array.AsReadOnly(_byVersion.Values.ToArray()); }
    }

    public ManifestRevision? Find(ManifestRevisionRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_gate) return _byVersion.Values.FirstOrDefault(item => item.Reference == reference);
    }

    public ValueTask<ManifestCompareAndCreateResult> CompareAndCreateAsync(
        ValidatedManifestCandidate candidate, ImportAuditMetadata auditMetadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(auditMetadata);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (candidate.ApplicationId, candidate.ApplicationVersion);
            if (_byVersion.TryGetValue(key, out var existing))
                return ValueTask.FromResult(new ManifestCompareAndCreateResult(existing, false));

            var application = _applications.TryGetValue(candidate.ApplicationId, out var record)
                ? record : new ApplicationRecord(candidate.ApplicationId, ApplicationRecordId.New());
            var number = _lastRevision.TryGetValue(candidate.ApplicationId, out var last) ? checked(last + 1) : 1;
            var revision = new ManifestRevision(ManifestRevisionId.New(),
                ManifestRevisionRef.Create(application.RecordId, ManifestRevisionNumber.Create(number)),
                candidate.ApplicationId, candidate.ApplicationVersion, candidate.Declaration.InputApiVersion,
                candidate.Declaration.NormalizedModelVersion, candidate.Declaration, candidate.Warnings, auditMetadata);
            _applications.TryAdd(application.Id, application);
            _byVersion.Add(key, revision);
            _lastRevision[candidate.ApplicationId] = number;
            return ValueTask.FromResult(new ManifestCompareAndCreateResult(revision, true));
        }
    }
}
