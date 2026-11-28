using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class ConfiguredCorrectiveCases
{
    internal static async Task<DeploymentReceipt> Run(ServiceProvider services, ManifestOperator.ManifestOperatorClient client,
        string environment, JsonNode template, string image, CancellationToken token)
    {
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var envelope = template.DeepClone();
        envelope["manifest"]!["metadata"]!["id"] = "corrective-" + Guid.NewGuid().ToString("N")[..12];
        envelope["applicationVersion"] = "configured-initial-bad-image";
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.invalid/haby/absent:never";
        var importer = new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(),
            new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition], [KubernetesContract.Definition], [], []),
            services.GetRequiredService<TimeProvider>());
        async Task<ManifestRevisionRef> Import()
        {
            var result = await importer.ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "qualification", token);
            return result is ImportApplicationManifestResult.Succeeded succeeded ? succeeded.ManifestRevisionRef :
                throw new InvalidOperationException("Configured corrective manifest refused.");
        }
        var firstRevision = await Import();
        var change = (await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = environment,
            Reference = new() { ApplicationRecordId = firstRevision.ApplicationRecordId.ToString(), RevisionNumber = firstRevision.RevisionNumber.Value },
            Precondition = PlanningPrecondition.ExpectedAbsence
        }, cancellationToken: token)).ChangeSet;
        var plan = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSetId }, cancellationToken: token);
        if (plan.Status != PlanningStatus.Planned) throw new InvalidOperationException("Configured corrective initial plan refused.");
        var committed = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId }, cancellationToken: token);
        if (committed.Status != CommitStatus.Committed) throw new InvalidOperationException("Configured corrective initial acceptance refused.");
        var original = committed.Commit;
        var originalRequest = new GenerationRequest { DeploymentId = original.DeploymentId, Generation = original.Generation };
        if ((await client.SubmitGenerationAsync(originalRequest, cancellationToken: token)).Outcome != "submitted")
            throw new InvalidOperationException("Configured bad-image submission refused.");
        var failed = await client.WaitGenerationAsync(new()
        {
            DeploymentId = original.DeploymentId,
            Generation = original.Generation,
            RequestId = "configured-bad-image-pending",
            Settings = new() { MaximumRounds = 1 }
        }, cancellationToken: token);
        var id = Guid.Parse(original.DeploymentId);
        DeploymentReceipt previous;
        string resourceProof;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            if (failed.CurrentReady || failed.CurrentApplied ||
                (await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == id, token)).AppliedGeneration is not null)
                throw new InvalidOperationException("Bad-image first installation unexpectedly completed.");
            var action = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == original.Generation &&
                x.ActionContract == InternalDeploymentAction.Create, token);
            previous = DeploymentIntents.DecodeReceipt((await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == id &&
                x.Generation == original.Generation && x.OperationId == action.OperationId, token)).Payload);
            resourceProof = (await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == id &&
                x.Generation == original.Generation && x.Stage == ResourcesReadyCheckpoint.Stage, token)).EvidenceJson;
        }
        envelope["applicationVersion"] = "configured-corrected-image";
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = image;
        var fixedRevision = await Import();
        var correction = (await client.CreateCorrectiveChangeSetAsync(new()
        {
            EnvironmentId = environment,
            Reference = new() { ApplicationRecordId = fixedRevision.ApplicationRecordId.ToString(), RevisionNumber = fixedRevision.RevisionNumber.Value },
            DeploymentId = original.DeploymentId,
            ExpectedIncompleteDesiredGeneration = original.Generation,
            RequestId = Guid.CreateVersion7().ToString()
        }, cancellationToken: token)).ChangeSet;
        var correctivePlan = await client.CreatePlanAsync(new() { ChangeSetId = correction.ChangeSetId }, cancellationToken: token);
        if (correctivePlan.Status != PlanningStatus.Planned ||
            (await client.CheckPlanAsync(new() { PlanId = correctivePlan.Plan.PlanId }, cancellationToken: token)).Status != PlanningStatus.Current)
            throw new InvalidOperationException("Configured corrective plan refused.");
        var accepted = await client.CommitDesiredStateAsync(new() { ChangeSetId = correction.ChangeSetId, PlanId = correctivePlan.Plan.PlanId }, cancellationToken: token);
        if (accepted.Status != CommitStatus.Committed) throw new InvalidOperationException("Configured corrective acceptance refused.");
        if ((await client.SubmitGenerationAsync(new() { DeploymentId = original.DeploymentId, Generation = accepted.Commit.Generation }, cancellationToken: token)).Outcome != "submitted")
            throw new InvalidOperationException("Configured corrective continuation refused.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var iteration = 0;
        while (true)
        {
            var ready = await client.WaitGenerationAsync(new()
            {
                DeploymentId = original.DeploymentId,
                Generation = accepted.Commit.Generation,
                RequestId = "configured-corrected-" + iteration++,
                Settings = new() { MaximumRounds = 1 }
            }, cancellationToken: deadline.Token);
            if (ready.CurrentReady && ready.CurrentApplied && ready.RetirementCompleted) break;
            await Task.Delay(100, deadline.Token);
        }
        await using var verify = await contexts.CreateDbContextAsync(token);
        var current = await verify.ManagedObjectStates.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == accepted.Commit.Generation, token);
        var receipt = DeploymentIntents.DecodeReceipt(current.Payload);
        var binding = await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == current.ManagedWorkloadId, token);
        var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == accepted.Commit.Generation, token);
        if (receipt.Uid != previous.Uid || DurableOperationGraphCodec.Decode(source.GraphJson).Correction!.Document.ResourceProof != resourceProof ||
            await verify.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == id && x.Generation == original.Generation && x.Stage == "RetirementCompleted", token) ||
            await verify.ResourceManagement.AnyAsync(x => x.Deployment == id, token) ||
            (await verify.RuntimeConfigurationPermissions.AsNoTracking().Where(x => x.BindingId == binding.Id).ToArrayAsync(token))
                .Any(x => RuntimePublicationCodec.Origin(x.OriginPayload).Generation == original.Generation && x.Status != "Revoked"))
            throw new InvalidOperationException("Configured corrective identity, provenance or retirement mismatch.");
        Console.WriteLine("PASS configured-X01-bad-image-first-install-correction-exact-UID-retirement-Applied");
        return receipt;
    }
}
