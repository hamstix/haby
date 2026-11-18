using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;

namespace Haby.PostgreSql.IntegrationTests;

// Shared by the actual configured-contour caller and its three-Deployment catalog regression.
internal static class ConfiguredDrainInventory
{
    internal static async Task<DeploymentReceipt[]> ReadAsync(IDbContextFactory<ManifestCatalogDbContext> contexts,
        IReadOnlyList<DeploymentReceipt> confirmed, CancellationToken token)
    {
        if (confirmed.Count is < 1 or > 16 || confirmed.Select(Identity).Distinct().Count() != confirmed.Count ||
            confirmed.Select(x => x.Uid).Distinct(StringComparer.Ordinal).Count() != confirmed.Count)
            throw new InvalidOperationException("Configured drain inventory refused.");
        await using var read = await contexts.CreateDbContextAsync(token);
        // Read every Deployment receipt, including malformed/extra contracts: no filtering an obligation away.
        var rows = await read.OwnedObjects.AsNoTracking().Where(x => x.Role == "deployment").OrderBy(x => x.ManagedWorkloadId).Take(17).ToArrayAsync(token);
        if (rows.Length != confirmed.Count || rows.Any(x => x.Contract != "haby.kubernetes.deployment-receipt/v4" || x.Format != 4))
            throw new InvalidOperationException("Configured drain inventory refused.");
        var receipts = rows.Select(x => DeploymentIntents.DecodeReceipt(x.Payload)).ToArray();
        if (receipts.Select(Identity).Distinct().Count() != receipts.Length ||
            !receipts.Select(Identity).ToHashSet().SetEquals(confirmed.Select(Identity)))
            throw new InvalidOperationException("Configured drain inventory refused.");
        return receipts;
    }
    private static (KubernetesBinding, string, string, Guid, Guid, string) Identity(DeploymentReceipt value) =>
        (value.Binding, value.Namespace, value.Name, value.Owner, value.ManagedWorkload, value.Uid);
}
