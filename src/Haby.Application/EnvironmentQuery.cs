using Haby.Domain;

namespace Haby.Application;

/// <summary>Resolves a registry-local logical Environment ID without creating operator state.</summary>
public interface IEnvironmentQuery
{
    ValueTask<EnvironmentRecordId?> FindAsync(EnvironmentId id, CancellationToken cancellationToken);
}
