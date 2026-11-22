using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static class ImportMutationGate
{
    internal static async Task LockAsync(ManifestCatalogDbContext db, Guid environment, Guid application, CancellationToken token)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Import mutation requires a transaction.");
        var key = $"haby-import:{environment:N}:{application:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
        if (db.Database.CurrentTransaction?.TransactionId != transaction.TransactionId) throw new InvalidOperationException("Import mutation transaction changed.");
        db.RecordImportGate(transaction.TransactionId, environment, application);
    }
}
