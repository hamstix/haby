namespace Haby.Domain;

/// <summary>An exact local revision reference within a persisted Application.</summary>
public sealed record ManifestRevisionRef
{
    private ManifestRevisionRef(ApplicationRecordId applicationRecordId, ManifestRevisionNumber revisionNumber)
    {
        ApplicationRecordId = applicationRecordId;
        RevisionNumber = revisionNumber;
    }

    public ApplicationRecordId ApplicationRecordId { get; }

    public ManifestRevisionNumber RevisionNumber { get; }

    public static ManifestRevisionRef Create(ApplicationRecordId applicationRecordId, ManifestRevisionNumber revisionNumber)
    {
        ArgumentNullException.ThrowIfNull(applicationRecordId);
        ArgumentNullException.ThrowIfNull(revisionNumber);
        return new ManifestRevisionRef(applicationRecordId, revisionNumber);
    }
}
