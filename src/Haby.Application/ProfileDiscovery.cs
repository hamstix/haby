using Haby.Domain;

namespace Haby.Application;

/// <summary>Safe imported identity and original audit only; contains no declaration.</summary>
public sealed record ImportedProfileMetadata(ProfileRevisionRef Reference, ImportAuditMetadata AuditMetadata);

public sealed record ProfileDiscoveryPage(IReadOnlyList<ImportedProfileMetadata> Items, ProfileId? NextAfterProfileId);
public sealed record ProfileRevisionDiscoveryPage(IReadOnlyList<ImportedProfileMetadata> Items, int? NextAfterRevision);

/// <summary>Independent live keyset reads, without secret or declaration access.</summary>
public interface IProfileDiscoveryQuery
{
    ValueTask<ProfileDiscoveryPage> ListAsync(int pageSize = 50, ProfileId? afterProfileId = null,
        CancellationToken cancellationToken = default);
    ValueTask<ProfileRevisionDiscoveryPage> ListRevisionsAsync(ProfileId profileId, int pageSize = 50,
        int? afterRevision = null, CancellationToken cancellationToken = default);
}
