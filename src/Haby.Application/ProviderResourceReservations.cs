using Haby.Domain;

namespace Haby.Application;

/// <summary>Stable, non-secret external identity reservation. Namespace and Value are opaque
/// to common code; adapters own their interpretation and all naming rules.</summary>
public sealed record ProviderResourceReservation(ProviderInstanceId ProviderInstanceId,
    EnvironmentRecordId EnvironmentRecordId, ApplicationRecordId ApplicationRecordId,
    ManifestLocalId ResourceLocalId, string Namespace, string Value);

/// <summary>Trusted internal acceptance boundary, not a public authorization API.
/// True accepts a new or identical reservation; false is a retained-owner/value conflict.
/// Storage failures must not be confused with an accepted or reassigned identity.</summary>
public interface IProviderResourceReservations
{
    ValueTask<bool> ContainsAsync(ProviderResourceReservation reservation, CancellationToken cancellationToken);
    ValueTask<bool> TryReserveAsync(ProviderResourceReservation reservation, CancellationToken cancellationToken);
}
