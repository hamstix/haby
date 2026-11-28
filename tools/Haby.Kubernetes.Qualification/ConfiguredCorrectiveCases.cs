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
using Haby.PostgreSql.IntegrationTests;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class ConfiguredCorrectiveCases
{
    internal static async Task<DeploymentReceipt> Run(ServiceProvider services, ManifestOperator.ManifestOperatorClient client,
        string environment, JsonNode template, string image, PostgreSqlFixture fixture, CancellationToken token)
    {
        using var diagnostics = new ConfiguredCorrectiveDiagnostics();
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        EnvironmentRecordId environmentRecord;
        await using (var setup = await contexts.CreateDbContextAsync(token))
            environmentRecord = EnvironmentRecordId.Parse((await setup.Environments.AsNoTracking().SingleAsync(x => x.EnvironmentId == environment, token)).Id.ToString());
        // The SAME already-owned disposable PostgreSQL server and catalog are used.
        // This mirrors the existing standard Provision fixture; no new server or mode.
        var systemRole = "haby319_system_" + Guid.NewGuid().ToString("N");
        var systemPassword = "haby-test-" + Guid.NewGuid().ToString("N");
        await using (var administrator = new NpgsqlConnection(fixture.ConnectionString))
        {
            await administrator.OpenAsync(token);
            await new NpgsqlCommand($"CREATE ROLE \"{systemRole}\" LOGIN CREATEDB PASSWORD '{systemPassword}'", administrator).ExecuteNonQueryAsync(token);
        }
        var secrets = services.GetRequiredService<ISecretStore>();
        var passwordReference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var passwordBytes = Encoding.UTF8.GetBytes(systemPassword);
        try
        {
            await secrets.CreateAsync(new(new SecretOwner.Environment(environmentRecord), SecretPurpose.ProviderAdministrator),
                passwordReference, passwordBytes, token);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes); }
        var system = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = systemRole, Password = systemPassword };
        var providerConfiguration = new PostgreSqlProviderConfiguration(PostgreSqlProviderConfiguration.CurrentShapeVersion,
            system.Host!, system.Port, system.Database!, system.Username!, passwordReference,
            PostgreSqlDatabaseCheckpointCapability.ImplementationId, PostgreSqlTransportMode.Disable);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var provider = await providers.CreateInstanceAsync(environmentRecord, providerConfiguration, token);
        var providerRevision = ProviderConfigurationRevisionNumber.Create(1);
        await providers.AddRevisionAsync(environmentRecord, provider, providerRevision, providerConfiguration, token);
        diagnostics.Complete();
        diagnostics.Enter(ConfiguredCorrectiveDiagnostics.Phase.Preflight);
        var binding = new ProviderAliasBinding(provider, providerRevision);
        if ((await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environmentRecord,
            ProviderAlias.Create("postgresql.corrective"), null, binding, "qualification", token)).Outcome != ProviderAliasChangeOutcome.Changed ||
            (await services.GetRequiredService<IProviderRevisionPreflight>().CheckAsync(environmentRecord, binding, token)).Outcome != ProviderPreflightOutcome.Ready)
        { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.Preflight); throw new InvalidOperationException("Configured standard Provision fixture unavailable."); }
        diagnostics.Complete();
        diagnostics.Enter(ConfiguredCorrectiveDiagnostics.Phase.InitialPlanCommit);
        var databaseName = "haby319_" + Guid.NewGuid().ToString("N");
        var envelope = template.DeepClone();
        envelope["manifest"]!["metadata"]!["id"] = "corrective-" + Guid.NewGuid().ToString("N")[..12];
        envelope["applicationVersion"] = "configured-initial-bad-image";
        envelope["manifest"]!["spec"]!["resources"] = new JsonObject
        {
            ["db"] = new JsonObject
            {
                ["type"] = "postgresql.database/v1alpha1",
                ["providerRef"] = "postgresql.corrective",
                ["properties"] = new JsonObject { ["name"] = databaseName }
            }
        };
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
        if (plan.Status != PlanningStatus.Planned) { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.InitialPlan); throw new InvalidOperationException("Configured corrective initial plan refused."); }
        var committed = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId }, cancellationToken: token);
        if (committed.Status != CommitStatus.Committed) { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.InitialCommit); throw new InvalidOperationException("Configured corrective initial acceptance refused."); }
        var original = committed.Commit;
        var originalRequest = new GenerationRequest { DeploymentId = original.DeploymentId, Generation = original.Generation };
        diagnostics.Complete();
        diagnostics.Enter(ConfiguredCorrectiveDiagnostics.Phase.ExecuteResource);
        var resourceExecution = await client.ExecuteGenerationAsync(originalRequest, cancellationToken: token);
        if (resourceExecution.Status != GenerationExecutionStatus.Blocked || resourceExecution.Progress?.ResourcesReady is null ||
            resourceExecution.Progress.Total == 0 || resourceExecution.Progress.Completed != resourceExecution.Progress.Total ||
            resourceExecution.Progress.HasAppliedGeneration || resourceExecution.Progress.ResourcesReady.ActiveResources != 1)
        { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.ResourceProof); throw new InvalidOperationException("Actual initial Resource stage did not confirm without Applied."); }
        diagnostics.Complete();
        diagnostics.Enter(ConfiguredCorrectiveDiagnostics.Phase.BadSubmitWait);
        var originalDatabaseOid = await ReadDatabaseOid();
        if ((await client.SubmitGenerationAsync(originalRequest, cancellationToken: token)).Outcome != "submitted")
        { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.BadSubmit); throw new InvalidOperationException("Configured bad-image submission refused."); }
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
            { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.BadWait); throw new InvalidOperationException("Bad-image first installation unexpectedly completed."); }
            var action = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == original.Generation &&
                x.ActionContract == InternalDeploymentAction.Create, token);
            previous = DeploymentIntents.DecodeReceipt((await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == id &&
                x.Generation == original.Generation && x.OperationId == action.OperationId, token)).Payload);
            resourceProof = (await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == id &&
                x.Generation == original.Generation && x.Stage == ResourcesReadyCheckpoint.Stage, token)).EvidenceJson;
        }
        diagnostics.Complete();
        diagnostics.Enter(ConfiguredCorrectiveDiagnostics.Phase.CorrectivePlanCommit);
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
        { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.CorrectivePlan); throw new InvalidOperationException("Configured corrective plan refused."); }
        var accepted = await client.CommitDesiredStateAsync(new() { ChangeSetId = correction.ChangeSetId, PlanId = correctivePlan.Plan.PlanId }, cancellationToken: token);
        if (accepted.Status != CommitStatus.Committed) { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.CorrectiveCommit); throw new InvalidOperationException("Configured corrective acceptance refused."); }
        diagnostics.Complete();
        diagnostics.Enter(ConfiguredCorrectiveDiagnostics.Phase.SubmitWait);
        if ((await client.SubmitGenerationAsync(new() { DeploymentId = original.DeploymentId, Generation = accepted.Commit.Generation }, cancellationToken: token)).Outcome != "submitted")
        { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.CorrectiveSubmit); throw new InvalidOperationException("Configured corrective continuation refused."); }
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
        diagnostics.Complete();
        diagnostics.Enter(ConfiguredCorrectiveDiagnostics.Phase.Retirement);
        await using var verify = await contexts.CreateDbContextAsync(token);
        var current = await verify.ManagedObjectStates.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == accepted.Commit.Generation, token);
        var receipt = DeploymentIntents.DecodeReceipt(current.Payload);
        var runtimeBinding = await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == current.ManagedWorkloadId, token);
        var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == accepted.Commit.Generation, token);
        if (receipt.Uid != previous.Uid || DurableOperationGraphCodec.Decode(source.GraphJson).Correction!.Document.ResourceProof != resourceProof ||
            await verify.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == id && x.Generation == original.Generation && x.Stage == "RetirementCompleted", token) ||
            await verify.ResourceManagement.CountAsync(x => x.Deployment == id && x.Resource == "db" && x.SourceGeneration == original.Generation, token) != 1 ||
            await ReadDatabaseOid() != originalDatabaseOid ||
            (await verify.RuntimeConfigurationPermissions.AsNoTracking().Where(x => x.BindingId == runtimeBinding.Id).ToArrayAsync(token))
                .Any(x => RuntimePublicationCodec.Origin(x.OriginPayload).Generation == original.Generation && x.Status != "Revoked"))
        { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.RetirementProof); throw new InvalidOperationException("Configured corrective identity, provenance or retirement mismatch."); }
        diagnostics.Complete();
        diagnostics.Enter(ConfiguredCorrectiveDiagnostics.Phase.OidProof);
        var resourceSource = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == original.Generation, token);
        var resourceOperations = DurableOperationGraphCodec.Decode(resourceSource.GraphJson).Nodes.Select(x => x.Id.Value).ToArray();
        var effects = await verify.DurableOperations.AsNoTracking().Where(x => x.DeploymentId == id && x.Generation == original.Generation && resourceOperations.Contains(x.OperationId)).ToArrayAsync(token);
        if (resourceOperations.Length == 0 || effects.Length != resourceOperations.Length || effects.Any(x => x.AttemptNumber != 1 || x.State != "Succeeded") ||
            await verify.DurableOperationAttempts.CountAsync(x => x.DeploymentId == id && x.Generation == original.Generation && resourceOperations.Contains(x.OperationId), token) != resourceOperations.Length ||
            ResourcesReadyCheckpointCodec.Decode(resourceProof).Resources.Count != 1)
        { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.OriginalEffects); throw new InvalidOperationException("Original Resource effect was replayed or lost."); }
        var resourceContracts = await verify.OperationOutputs.AsNoTracking().Where(x => x.DeploymentId == id && x.Generation == original.Generation &&
            resourceOperations.Contains(x.OperationId)).Select(x => x.Contract).Distinct().ToArrayAsync(token);
        if (resourceContracts.Length == 0 || await verify.OperationOutputs.AnyAsync(x => x.DeploymentId == id && x.Generation == accepted.Commit.Generation && resourceContracts.Contains(x.Contract), token) ||
            await verify.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == id && x.Generation == accepted.Commit.Generation && x.Stage == ResourcesReadyCheckpoint.Stage, token))
        { diagnostics.Reject(ConfiguredCorrectiveDiagnostics.Result.CopiedProof); throw new InvalidOperationException("Correction copied Resource outputs or checkpoint instead of direct original proof."); }
        diagnostics.Complete();
        Console.WriteLine("PASS configured-R08-real-Resources-first-install-correction-exact-UID-no-replay-retirement-Applied");
        return receipt;

        async Task<uint> ReadDatabaseOid()
        {
            await using var read = new NpgsqlConnection(fixture.ConnectionString);
            await read.OpenAsync(token);
            await using var command = new NpgsqlCommand("SELECT oid FROM pg_database WHERE datname = @database AND pg_get_userbyid(datdba) = @owner", read);
            command.Parameters.AddWithValue("database", databaseName); command.Parameters.AddWithValue("owner", systemRole);
            return await command.ExecuteScalarAsync(token) is uint oid ? oid : throw new InvalidOperationException("Actual owned database evidence unavailable.");
        }
    }
}
