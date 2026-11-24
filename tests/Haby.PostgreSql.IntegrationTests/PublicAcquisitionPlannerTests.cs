using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ResourceReferenceKernelTests
{
    [Fact]
    public async Task Public_acquisition_planner_refuses_direct_invalid_sets_and_Import_without_partial_rows()
    {
        var connection = await CreateDatabaseAsync("public_acquisition");
        var boundary = new Boundary();
        var item = await PrepareAsync(connection, boundary, mixed: false, secondReference: true);
        await using var services = Services(connection, boundary);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        int before;
        await using (var read = await contexts.CreateDbContextAsync()) before = await read.DurableChangeSets.CountAsync();
        await using (var missing = Services(connection, boundary, referenceEvidence: false))
        {
            var unsupported = await missing.GetRequiredService<IDurableDeploymentPlanner>().CreateAcquisitionChangeSetAsync(item.Environment,
                item.Plan.Inputs.Revision.ApplicationRecordId, item.Plan.Inputs.Revision, new DeploymentPrecondition.ExpectedAbsence(),
                item.Plan.Inputs.Acquisitions, "operator", default);
            Assert.Equal(DurableChangeSetCreateStatus.ReferenceNotSupported, unsupported.Status);
        }
        foreach (var (intent, status) in new (ResourceAcquisitionIntent, DurableChangeSetCreateStatus)[]
        {
            (new([]), DurableChangeSetCreateStatus.InvalidInput),
            (new(item.Plan.Inputs.Acquisitions.Resources.Select(x => x with
            { Input = new("test.capability-input/v1", new string('\u20ac', 65536)), SharingRequest = new("test.sharing/v1", new string('\u0001', 65536)) })), DurableChangeSetCreateStatus.InvalidInput),
            (new([new(Referenced, ResourceAcquisitionMode.ImportUseAndRetain, Payload("service"))]), DurableChangeSetCreateStatus.ImportNotSupported),
            (new([new(Referenced, ResourceAcquisitionMode.Reference, new("unknown/v1", "{}"))]), DurableChangeSetCreateStatus.InvalidInput),
            (new([new(ManifestLocalId.Create("foreign"), ResourceAcquisitionMode.Reference, Payload("service"))]), DurableChangeSetCreateStatus.InvalidInput)
        })
        {
            var result = await planner.CreateAcquisitionChangeSetAsync(item.Environment, item.Plan.Inputs.Revision.ApplicationRecordId,
                item.Plan.Inputs.Revision, new DeploymentPrecondition.ExpectedAbsence(), intent, "operator", default);
            Assert.Equal(status, result.Status);
            Assert.Null(result.ChangeSet);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.Equal(before, await verify.DurableChangeSets.CountAsync());
            Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        }
    }
}
