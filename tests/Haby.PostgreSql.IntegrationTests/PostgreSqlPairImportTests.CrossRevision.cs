using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using System.Text.Json.Nodes;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ImportStatus = Haby.Operator.Contracts.ResourceImportRequestStatus;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Fact]
    public async Task Public_precommit_import_correction_accepts_corrected_revision_after_failed_plan_and_preserves_history()
    {
        var pair = await ExistingPair();
        var setup = await Prepare(pair, Haby.PostgreSql.PostgreSqlTransportMode.Disable);
        var before = await Snapshot(pair);
        using var key = RSA.Create(2048);
        using var root = Root(key, "haby-cross-revision-" + Guid.NewGuid().ToString("N"));
        using var publicRoot = X509CertificateLoader.LoadCertificate(root.RawData);
        var (pem, privateKey) = Leaf(root, true);
        using var certificate = X509Certificate2.CreateFromPem(pem, privateKey);
        var path = Path.Combine(Path.GetTempPath(), "haby-cross-revision-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "test-only"));
        try
        {
            var application = "cross-revision-" + Guid.NewGuid().ToString("N")[..12];
            var firstId = Guid.NewGuid(); var nextId = Guid.NewGuid();
            RevisionReference firstReference;
            RevisionReference nextReference;
            ResourceImportRequestReply first;
            ResourceImportRequestReply next;
            await using (var host = await StartPublicImportHost(setup.Connection, path, null))
            {
                using var channel = PublicImportChannel(host, publicRoot, false);
                var client = OperatorTestCredentials.Client(channel);
                var bad = Declaration(application);
                // The named configuration slot has no matching Profile decision. The
                // declaration is accepted, but planning must reject unresolved placement.
                var binding = bad["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]!["use"]!.AsObject();
                binding.Remove("documentProjections"); binding["configurationSlot"] = "missing-layout";
                var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(bad)) });
                Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase);
                firstReference = imported.Created.Revision.Reference;
                first = await Create(client, firstId, firstReference, null);
                Assert.Equal(ImportStatus.Created, first.Status);
                var failed = await client.CreatePlanAsync(new() { ChangeSetId = first.Request.ChangeSetId });
                Assert.Equal(PlanningStatus.ConfigurationRefused, failed.Status);
                Assert.Null(failed.Plan);
                var good = Declaration(application); good["applicationVersion"] = "v2";
                var corrected = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(good)) });
                Assert.Equal(ImportReply.OutcomeOneofCase.Created, corrected.OutcomeCase);
                nextReference = corrected.Created.Revision.Reference;
                Assert.Equal(firstReference.ApplicationRecordId, nextReference.ApplicationRecordId);
                Assert.NotEqual(firstReference.RevisionNumber, nextReference.RevisionNumber);
                var correction = new ResourceImportCorrection { PreviousRequestId = firstId.ToString(), Reason = ResourceImportCorrectionReason.InputCorrection, ExpectedAbsence = true };
                next = await Create(client, nextId, nextReference, correction);
                Assert.Equal(ImportStatus.Created, next.Status);
                Assert.Equal(nextReference, next.Request.Reference);
                Assert.Equal(first.Request, (await client.GetResourceImportRequestAsync(new() { RequestId = firstId.ToString() })).Request);
                Assert.Equal(ImportStatus.Conflict, (await Create(client, firstId, nextReference, null)).Status);
                await using (var verify = await host.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
                {
                    var owner = await verify.ProtectedCredentialOwners.AsNoTracking().SingleAsync(x => x.Application == Guid.Parse(firstReference.ApplicationRecordId));
                    Assert.Equal(firstId, owner.Request);
                    Assert.Empty(await verify.ImportSupersessions.AsNoTracking().Where(x => x.PreviousRequest == firstId).ToArrayAsync());
                    Assert.False(await verify.DurablePlans.AsNoTracking().AnyAsync(x => x.ChangeSetId == Guid.Parse(first.Request.ChangeSetId)));
                }
                var plan = await client.CreatePlanAsync(new() { ChangeSetId = next.Request.ChangeSetId });
                Assert.Equal(PlanningStatus.Planned, plan.Status);
                Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = plan.Plan.PlanId })).Status);
                var committed = await client.CommitDesiredStateAsync(new() { ChangeSetId = next.Request.ChangeSetId, PlanId = plan.Plan.PlanId });
                Assert.Equal(CommitStatus.Committed, committed.Status);
                Assert.Equal(nextReference, (await client.GetGenerationAsync(new() { DeploymentId = committed.Commit.DeploymentId, Generation = committed.Commit.Generation })).DesiredGeneration.Reference);
                Assert.Equal(1, committed.Commit.Generation);
                var execution = await client.ExecuteGenerationAsync(new() { DeploymentId = committed.Commit.DeploymentId, Generation = committed.Commit.Generation });
                Assert.Equal(GenerationExecutionStatus.Succeeded, execution.Status);
            }
            await using (var restart = await StartPublicImportHost(setup.Connection, path, null))
            {
                using var channel = PublicImportChannel(restart, publicRoot, false);
                var client = OperatorTestCredentials.Client(channel);
                var old = await client.GetResourceImportRequestAsync(new() { RequestId = firstId.ToString() });
                Assert.Equal(ImportStatus.Superseded, old.Status);
                Assert.Equal(firstReference, old.Request.Reference);
                Assert.Equal(nextId.ToString(), old.Request.SupersededByRequestId);
                Assert.Equal(ImportStatus.Superseded, (await Create(client, firstId, firstReference, null)).Status);
                var correction = new ResourceImportCorrection { PreviousRequestId = firstId.ToString(), Reason = ResourceImportCorrectionReason.InputCorrection, ExpectedAbsence = true };
                Assert.Equal(ImportStatus.Existing, (await Create(client, nextId, nextReference, correction)).Status);
                await using var verify = await restart.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
                var owner = await verify.ProtectedCredentialOwners.AsNoTracking().SingleAsync(x => x.Application == Guid.Parse(firstReference.ApplicationRecordId));
                Assert.Equal(nextId, owner.Request);
                Assert.Equal(2, await verify.OperatorImportRequestRecords.CountAsync(x => x.Application == owner.Application));
                var stages = await verify.ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Application == owner.Application)
                    .Select(x => new { x.Request, x.ConfirmedReceipt }).ToArrayAsync();
                Assert.Null(stages.Single(x => x.Request == firstId).ConfirmedReceipt);
                Assert.NotNull(stages.Single(x => x.Request == nextId).ConfirmedReceipt);
                Assert.Single(await verify.ImportSupersessions.AsNoTracking().Where(x => x.PreviousRequest == firstId).ToArrayAsync());
            }
            Assert.Equal(before, await Snapshot(pair));

            async Task<ResourceImportRequestReply> Create(ManifestOperator.ManifestOperatorClient client, Guid id, RevisionReference revision,
                ResourceImportCorrection? correction)
            {
                var request = new CreateResourceImportChangeSetRequest
                {
                    RequestId = id.ToString(),
                    EnvironmentRecordId = setup.Owner.EnvironmentRecordId.ToString(),
                    Reference = revision,
                    InputFormat = Haby.PostgreSql.PostgreSqlImportPreparer.NamesFormat,
                    InputJson = ByteString.CopyFromUtf8(Haby.PostgreSql.PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema)).Json),
                    CredentialMaterial = ByteString.CopyFromUtf8(pair.Password),
                    Correction = correction
                };
                try { return await client.CreateResourceImportChangeSetAsync(request); }
                finally { request.CredentialMaterial = ByteString.Empty; }
            }
        }
        finally { File.Delete(path); }
    }
    private static async Task<ManifestRevisionRef> CorrectedRevision(ServiceProvider services, ManifestRevisionRef previous)
    {
        var accepted = (await services.GetRequiredService<IManifestRevisionQuery>().FindAsync(previous))!;
        var envelope = Declaration(accepted.ApplicationId.Value); envelope["applicationVersion"] = "v2";
        // A real immutable declaration change, not just a version label.
        envelope["manifest"]!["spec"]!["components"]!["worker"]!["documents"]!["settings"]!["template"] = new JsonObject { ["Corrected"] = true };
        return Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(),
            new([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [], [], [new(PostgreSqlConnectionRendering.Renderer)]), TimeProvider.System)
            .ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(envelope), "revision-author")).ManifestRevisionRef;
    }

    [Theory]
    [InlineData("before-transfer")]
    [InlineData("after-transfer")]
    [InlineData("lost-response")]
    public async Task Cross_revision_precommit_correction_rolls_back_or_replays_one_atomic_acceptance(string phase)
    {
        var pair = await ExistingPair();
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable);
        ImportDraft previous; ImportDraft replacement;
        await using (var arrange = Services(setup.Connection))
        {
            previous = await NewDraft(arrange, pair, setup.Owner.EnvironmentRecordId);
            replacement = await NewCorrection(arrange, pair, previous.Binding, null, await CorrectedRevision(arrange, previous.Binding.Revision));
        }
        var fault = new CorrectionTransferFault(phase);
        var loss = new CorrectionResponseFault(replacement.Change!.Id, phase == "lost-response");
        await using (var writer = Services(setup.Connection, configure: services => services.AddSingleton<IDbContextFactory<ManifestCatalogDbContext>>(
            new CorrectionFactory(setup.Connection, fault, loss))))
            await Assert.ThrowsAsync<IOException>(() => writer.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(replacement.Change.Id, replacement.Plan!.Id, "first-commit", default).AsTask());
        Assert.True(phase == "lost-response" ? loss.Thrown : fault.Thrown);
        await using var restart = Services(setup.Connection);
        var committer = restart.GetRequiredService<IDurableDesiredStateCommitter>();
        var saved = await committer.FindCommitAsync(replacement.Change.Id, default);
        if (phase == "lost-response") Assert.NotNull(saved);
        else
        {
            Assert.Null(saved);
            await using var verify = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Equal(previous.Binding.RequestId, (await verify.ProtectedCredentialOwners.AsNoTracking()
                .SingleAsync(x => x.Application == Guid.Parse(previous.Binding.Revision.ApplicationRecordId.ToString()))).Request);
            Assert.False(await verify.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == previous.Binding.RequestId));
            Assert.False(await verify.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.ApplicationRecordId == Guid.Parse(previous.Binding.Revision.ApplicationRecordId.ToString())));
        }
        var committed = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(replacement.Change.Id, replacement.Plan!.Id, "retry-commit", default)).Commit;
        if (saved is not null) Assert.Equal(saved, committed);
        Assert.Equal(1, committed.Generation.Value);
        await AssertCurrentCorrection(restart, previous, replacement, committed);
        Assert.IsType<DurableCommitResult.ChangeSetStale>(await committer.CommitAsync(previous.Change!.Id, previous.Plan!.Id, "old", default));
        Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(committed.DeploymentId, committed.Generation, default)).StopOutcome);
        var desired = await committer.FindGenerationAsync(committed.DeploymentId, committed.Generation, default);
        Assert.Equal(replacement.Binding.Revision, desired!.Inputs.Revision);
    }

    [Theory]
    [InlineData("post-commit")]
    [InlineData("stale-absence")]
    [InlineData("confirmed-import")]
    [InlineData("credential-rejected")]
    [InlineData("explicit-retry")]
    [InlineData("other-application")]
    [InlineData("other-environment")]
    [InlineData("other-resource")]
    [InlineData("invalid-successor")]
    public async Task Cross_revision_correction_preserves_scope_and_failed_successor_does_not_take_ownership(string fault)
    {
        var pair = await ExistingPair();
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable, operatorRequest: true);
        await using var services = Services(setup.Connection);
        var previous = await NewDraft(services, pair, setup.Owner.EnvironmentRecordId);
        var revision = await CorrectedRevision(services, previous.Binding.Revision);
        var prepared = await services.GetRequiredService<PrepareResourceImport>().ExecuteAsync(new(Guid.NewGuid(), previous.Binding.Environment, revision,
            PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema))), default);
        Assert.Equal(ResourceImportPreparationStatus.Prepared, prepared.Status);
        var target = new OperatorImportCorrectionTarget(previous.Binding.RequestId, null, null, OperatorImportCorrectionReason.InputCorrection);
        var binding = prepared.Binding! with { Correction = target };
        if (fault is "post-commit" or "stale-absence" or "confirmed-import")
        {
            var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(previous.Change!.Id, previous.Plan!.Id, "old-winner", default)).Commit;
            if (fault == "post-commit") binding = binding with { Correction = target with { Deployment = commit.DeploymentId, Generation = commit.Generation } };
            if (fault == "confirmed-import") Assert.Null((await services.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(commit.DeploymentId, commit.Generation, default)).StopOutcome);
        }
        else if (fault == "credential-rejected") binding = binding with { Correction = target with { Reason = OperatorImportCorrectionReason.CredentialRejected } };
        else if (fault == "explicit-retry") binding = binding with { Correction = target with { Reason = OperatorImportCorrectionReason.ExplicitRetry } };
        else if (fault == "other-application") binding = binding with { Revision = setup.Plan.Inputs.Revision };
        else if (fault == "other-environment") binding = binding with { Environment = EnvironmentRecordId.New() };
        else if (fault == "other-resource") binding = binding with { CredentialResource = ManifestLocalId.Create("db") };
        else binding = binding with { Input = new(binding.Input.Resources.Where(x => x.Resource == binding.CredentialResource)) };
        using var material = Material(pair.Password);
        var result = await services.GetRequiredService<OperatorImportRequests>().CreateAsync(binding, material, "refused", default);
        Assert.Contains(result.Status, new[] { OperatorImportRequestStatus.Conflict, OperatorImportRequestStatus.InvalidInput, OperatorImportRequestStatus.ImportNotSupported });
        await using var check = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(previous.Binding.RequestId, (await check.ProtectedCredentialOwners.AsNoTracking()
            .SingleAsync(x => x.Application == Guid.Parse(previous.Binding.Revision.ApplicationRecordId.ToString()))).Request);
        Assert.False(await check.OperatorImportRequestRecords.AsNoTracking().AnyAsync(x => x.Id == binding.RequestId));
        Assert.False(await check.ProtectedApplicationCredentials.AsNoTracking().AnyAsync(x => x.Request == binding.RequestId));
        Assert.False(await check.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == previous.Binding.RequestId));
    }

    [Fact]
    public async Task Cross_revision_cancelled_commit_rolls_back_owner_and_supersession_before_retry()
    {
        var pair = await ExistingPair(); var setup = await Prepare(pair, PostgreSqlTransportMode.Disable);
        ImportDraft previous; ImportDraft replacement;
        await using (var arrange = Services(setup.Connection))
        {
            previous = await NewDraft(arrange, pair, setup.Owner.EnvironmentRecordId);
            replacement = await NewCorrection(arrange, pair, previous.Binding, null, await CorrectedRevision(arrange, previous.Binding.Revision));
        }
        using var cancel = new CancellationTokenSource(); var fault = new CancelCorrectionTransfer(cancel);
        await using (var act = Services(setup.Connection, configure: services => services.AddSingleton<IDbContextFactory<ManifestCatalogDbContext>>(
            new CorrectionFactory(setup.Connection, fault))))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => act.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(replacement.Change!.Id, replacement.Plan!.Id, "cancelled", cancel.Token).AsTask());
        Assert.True(fault.Cancelled);
        await using var restart = Services(setup.Connection);
        await using (var check = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.False(await check.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == previous.Binding.RequestId));
            Assert.Equal(previous.Binding.RequestId, (await check.ProtectedCredentialOwners.AsNoTracking()
                .SingleAsync(x => x.Application == Guid.Parse(previous.Binding.Revision.ApplicationRecordId.ToString()))).Request);
        }
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await restart.GetRequiredService<IDurableDesiredStateCommitter>()
            .CommitAsync(replacement.Change!.Id, replacement.Plan!.Id, "retry", default)).Commit;
        await AssertCurrentCorrection(restart, previous, replacement, accepted);
    }

    private sealed class CancelCorrectionTransfer(CancellationTokenSource source) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        internal bool Cancelled { get; private set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE protected_application_credential_owners", StringComparison.Ordinal))
            { Cancelled = true; source.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(result);
        }
    }

}
