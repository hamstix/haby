using System.Data;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

// Test-side catalog evidence, never a fabricated public GenerationProgress or new runtime authority.
internal sealed record ReferencePreparationCatalogProof(Guid Deployment, long Generation, long Desired, long? Applied,
    bool Superseded, string Fingerprint, int FormatVersion, ResourcesReadyCheckpoint Ready,
    IReadOnlyList<ResourceReuse> Reused, IReadOnlyList<ResourceReuse> Retained,
    IReadOnlyList<string> ReferenceResources, IReadOnlyList<ResourceReadyResult> OriginalResources);

internal static class ReferencePreparationCatalog
{
    internal static async Task<ReferencePreparationCatalogProof> ReadAsync(string connection, GenerationRequest exact,
        DurableCommitSummary accepted, RevisionReference revision, PublicReferenceFailureDiagnostics? diagnostic = null)
    {
        diagnostic?.Predicate(ReferencePreparationPredicate.CatalogRead);
        Assert.Equal(accepted.DeploymentId, exact.DeploymentId); Assert.Equal(accepted.Generation, exact.Generation);
        await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var deployment = Guid.Parse(exact.DeploymentId);
        var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == exact.Generation);
        diagnostic?.Predicate(ReferencePreparationPredicate.CatalogAccepted);
        Assert.True(source.InternalEffects); Assert.Equal(nameof(DesiredPresence.Present), source.Presence);
        Assert.Equal(accepted.ChangeSetId, source.ChangeSetId.ToString()); Assert.Equal(accepted.PlanId, source.PlanId.ToString());
        Assert.Equal(accepted.TechnicalActorId, source.ActorId);
        Assert.Equal(revision.ApplicationRecordId, source.ApplicationRecordId.ToString()); Assert.Equal(revision.RevisionNumber, source.RevisionNumber);
        var inputs = source.Inputs(); var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        diagnostic?.Predicate(ReferencePreparationPredicate.CatalogFingerprint);
        Assert.Equal(DurableOperationGraphCodec.Fingerprint(inputs, graph), source.Fingerprint);
        diagnostic?.Predicate(ReferencePreparationPredicate.CatalogResourceShape);
        Assert.Empty(graph.Nodes);
        Assert.Equal(new[] { "left", "right" }, graph.ReusedResources.Select(x => x.Resource.Value).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "left", "right" }, inputs.Acquisitions.Resources.Where(x => x.Mode == ResourceAcquisitionMode.Reference).Select(x => x.Resource.Value).Order(StringComparer.Ordinal).ToArray());
        diagnostic?.Predicate(ReferencePreparationPredicate.CatalogCheckpoint);
        // Existing codec/integrity reader validates target identity, version, evidence hash and every original confirmed result.
        var ready = await DurableAggregateCompletion.ReadAsync(db, source, default, internalResourceStage: true, completedRead: true);
        Assert.NotNull(ready);
        var checkpoint = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == exact.Generation && x.Stage == ResourcesReadyCheckpoint.Stage);
        var originals = new List<ResourceReadyResult>();
        foreach (var reuse in graph.ReusedResources)
        {
            diagnostic?.Predicate(ReferencePreparationPredicate.CatalogOriginalResults);
            Assert.True(reuse.SourceGeneration > 0 && reuse.SourceGeneration < source.Generation);
            var original = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == reuse.SourceGeneration);
            Assert.Equal(original.Fingerprint, DurableOperationGraphCodec.Fingerprint(original.Inputs(), DurableOperationGraphCodec.Decode(original.GraphJson)));
            var originalProof = await DurableAggregateCompletion.ReadAsync(db, original, default, completedRead: true, referenceHistory: true);
            Assert.NotNull(originalProof);
            // This is original result evidence only. It can never substitute for the mandatory target checkpoint above.
            originals.Add(Assert.Single(originalProof.Resources, x => x.Resource == reuse.Resource.Value));
        }
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == deployment);
        var superseded = await (from audit in db.ImportSupersessions.AsNoTracking()
                                join successor in db.OperatorImportRequestRecords.AsNoTracking() on audit.SuccessorRequest equals successor.Id
                                where successor.ExpectedDeployment == deployment && successor.ExpectedGeneration == exact.Generation && audit.Deployment == deployment
                                select audit).AnyAsync();
        return new(deployment, source.Generation, owner.DesiredGeneration, owner.AppliedGeneration, superseded,
            source.Fingerprint, checkpoint.FormatVersion, ready, graph.ReusedResources, graph.RetainedResources,
            inputs.Acquisitions.Resources.Where(x => x.Mode == ResourceAcquisitionMode.Reference).Select(x => x.Resource.Value).Order(StringComparer.Ordinal).ToArray(), originals);
    }
}
