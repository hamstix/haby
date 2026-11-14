using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    private Task RequireConfigurationAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token) =>
        configurationPreparation?.RequireCompleteAsync(db, source, token) ?? throw new InvalidDataException("Complete configuration preparation authority unavailable.");

    private static async Task RefuseUnboundHistoryAsync(ManifestCatalogDbContext db, Guid deployment, CancellationToken token)
    {
        if (await db.ActionIntents.AsNoTracking().AnyAsync(x => x.DeploymentId == deployment && x.SubjectKind == "Workload" &&
            !db.ManagedWorkloads.Any(w => w.Id == x.ManagedIdentity && w.DeploymentId == x.DeploymentId && w.Component == x.Component && w.Workload == x.LocalId), token))
            throw new InvalidDataException("Superseded Workload identity requires an explicitly initialized fresh catalog; external resources remain unchanged.");
    }

    internal static async Task<Guid> InstallationAsync(ManifestCatalogDbContext db, CancellationToken token)
    {
        // Different deployment locks cannot serialize installation-wide first initialization.
        var gate = "haby-installation-identity/v1";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({gate}, 0))", token);
        var row = await db.InstallationIdentities.SingleOrDefaultAsync(x => x.Singleton == 1, token);
        if (row is not null) return row.Id;
        row = new() { Singleton = 1, Id = Guid.CreateVersion7() };
        db.InstallationIdentities.Add(row);
        return row.Id;
    }

    private static async Task<ManagedWorkloadRow> ManagedAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        ProviderInput.Workload workload, InternalActionContext context, IInternalActionHandler handler, bool create, CancellationToken token)
    {
        var target = handler.ManagedTarget(context) ?? new("haby.logical-workload/v1", 1, "{}");
        Bound(target.Contract, 128); Bound(target.Payload, 16384);
        if (target.Format <= 0) throw new InvalidDataException("Invalid managed target format.");
        var provider = Guid.Parse(context.Provider.Instance.ToString());
        var row = await db.ManagedWorkloads.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Component == workload.Component.Value && x.Workload == workload.Id.Value, token);
        if (row is null)
        {
            if (!create) throw new InvalidDataException("Stable managed Workload unavailable; explicit fresh catalog required.");
            row = new()
            {
                Id = Guid.CreateVersion7(),
                DeploymentId = source.DeploymentId,
                EnvironmentId = source.EnvironmentRecordId,
                ApplicationId = source.ApplicationRecordId,
                Component = workload.Component.Value,
                Workload = workload.Id.Value,
                ProviderId = provider,
                TargetContract = target.Contract,
                TargetFormat = target.Format,
                TargetPayload = target.Payload
            };
            db.ManagedWorkloads.Add(row);
        }
        if (row.EnvironmentId != source.EnvironmentRecordId || row.ApplicationId != source.ApplicationRecordId || row.ProviderId != provider ||
            row.TargetContract != target.Contract || row.TargetFormat != target.Format || row.TargetPayload != target.Payload)
            throw new InvalidDataException("Unsupported managed Workload target change.");
        return row;
    }
    private static void Bound(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || Encoding.UTF8.GetByteCount(value) > maximum)
            throw new InvalidDataException("Invalid bounded identity evidence.");
    }
    private static InternalIdentityConfirmation Confirmation(Loaded loaded, string receipt)
    {
        var value = loaded.Handler.IdentityConfirmation(loaded.Context, loaded.Intent.Payload, receipt)
            ?? throw new InvalidDataException("Identity receipt projection unavailable.");
        Bound(value.Role, 128); Bound(value.InventoryContract, 128); Bound(value.ValidationContract, 128); Bound(value.InventoryPayload, 1_048_576);
        Bound(value.Authenticator, 128); Bound(value.Audience, 256); Bound(value.ValidationPayload, 1_048_576);
        if (value.Format <= 0 || loaded.Context.InstallationId == Guid.Empty) throw new InvalidDataException("Identity authority unavailable.");
        return value;
    }
    private static async Task AcceptIdentityAsync(ManifestCatalogDbContext db, Loaded loaded, string receipt, CancellationToken token)
    {
        if (!loaded.Handler.RequiresPreparedConfiguration) return;
        var value = Confirmation(loaded, receipt);
        if (await db.OwnedObjects.AnyAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity, token) ||
            await db.RuntimeIdentityBindings.AnyAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity, token))
            throw new InvalidDataException("Existing identity cannot be replaced by a new confirmation.");
        db.OwnedObjects.Add(new()
        {
            ManagedWorkloadId = loaded.Intent.ManagedIdentity,
            Role = value.Role,
            Contract = value.InventoryContract,
            Format = value.Format,
            Payload = value.InventoryPayload,
            DeploymentId = loaded.Intent.DeploymentId,
            Generation = loaded.Intent.Generation,
            OperationId = loaded.Intent.OperationId
        });
        db.RuntimeIdentityBindings.Add(new()
        {
            Id = Guid.CreateVersion7(),
            InstallationId = loaded.Context.InstallationId,
            EnvironmentId = Guid.Parse(loaded.Context.Provider.Environment.ToString()),
            ManagedWorkloadId = loaded.Intent.ManagedIdentity,
            ProviderId = loaded.Intent.ProviderId,
            ConfigurationRevision = loaded.Intent.ConfigurationRevision,
            Authenticator = value.Authenticator,
            Audience = value.Audience,
            Status = "Prepared",
            Role = value.Role,
            Contract = value.ValidationContract,
            Format = value.Format,
            ValidationPayload = value.ValidationPayload
        });
    }
    private static async Task ValidateIdentityAsync(ManifestCatalogDbContext db, Loaded loaded, string receipt, CancellationToken token)
    {
        if (!loaded.Handler.RequiresPreparedConfiguration) return;
        var value = Confirmation(loaded, receipt);
        var inventory = await db.OwnedObjects.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity && x.Role == value.Role, token);
        var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity, token);
        if (inventory is null || binding is null || inventory.Contract != value.InventoryContract || inventory.Format != value.Format || inventory.Payload != value.InventoryPayload ||
            inventory.DeploymentId != loaded.Intent.DeploymentId || inventory.Generation != loaded.Intent.Generation || inventory.OperationId != loaded.Intent.OperationId ||
            binding.InstallationId != loaded.Context.InstallationId || binding.EnvironmentId.ToString() != loaded.Context.Provider.Environment.ToString() ||
            binding.ProviderId != loaded.Intent.ProviderId || binding.ConfigurationRevision != loaded.Intent.ConfigurationRevision || binding.Authenticator != value.Authenticator ||
            binding.Audience != value.Audience || binding.Role != value.Role || binding.Contract != value.ValidationContract || binding.Format != value.Format ||
            binding.ValidationPayload != value.ValidationPayload)
            throw new InvalidDataException("Immutable runtime identity evidence mismatch.");
        InternalRuntimePublication.ValidateBinding(binding);
    }

    // Validates the immutable original CREATE proof without network I/O. Current publication
    // authority is independently revalidated by the caller in the same transaction.
    internal async Task RequireIdentityOriginAsync(ManifestCatalogDbContext db, RuntimeIdentityBindingRow binding, CancellationToken token)
    {
        var inventory = await db.OwnedObjects.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == binding.ManagedWorkloadId && x.Role == binding.Role, token)
            ?? throw new InvalidDataException("Runtime inventory unavailable.");
        var key = new DurableOperationKey(ApplicationDeploymentId.Parse(inventory.DeploymentId.ToString()),
            DeploymentGeneration.Create(inventory.Generation), OperationId.Create(inventory.OperationId));
        var loaded = await LoadAsync(db, key, token);
        if (loaded.Operation.State != nameof(DurableOperationState.Succeeded) || loaded.Intent.ManagedIdentity != binding.ManagedWorkloadId ||
            !loaded.Handler.RequiresPreparedConfiguration) throw new InvalidDataException("Confirmed identity origin unavailable.");
        await TerminalAsync(db, loaded, token);
    }
}
