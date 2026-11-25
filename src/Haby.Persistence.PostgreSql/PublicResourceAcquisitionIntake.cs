using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class PostgreSqlDurableDeploymentPlanner
{
    public async ValueTask<DurableChangeSetCreateResult> CreateAcquisitionChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentPrecondition precondition,
        ResourceAcquisitionIntent acquisitions, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (environment is null || application is null || revision is null || acquisitions is null ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || actor.Any(char.IsControl)) return new(DurableChangeSetCreateStatus.InvalidInput);
        if (precondition is not (DeploymentPrecondition.ExpectedAbsence or DeploymentPrecondition.ExpectedGeneration))
            return new(DurableChangeSetCreateStatus.ExpectedGenerationUnsupported);
        if (acquisitions.Resources.Any(x => x.Mode == ResourceAcquisitionMode.ImportUseAndRetain))
            return new(DurableChangeSetCreateStatus.ImportNotSupported);
        await using var db = await contexts.CreateDbContextAsync(token);
        var invalid = await ValidateChangeSetInputAsync(db, environment, application, revision, null, false, token);
        if (invalid is { } status) return new(status);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var accepted = PostgreSqlManifestImportStore.Rehydrate(await db.Revisions.AsNoTracking().SingleAsync(x =>
            x.ApplicationRecordId == Guid.Parse(application.ToString()) && x.LocalRevisionNumber == revision.RevisionNumber.Value, token));
        try
        {
            _ = ResourceAcquisitionIntentCodec.Encode(acquisitions);
            var env = Guid.Parse(environment.ToString());
            var app = Guid.Parse(application.ToString());
            var references = acquisitions.Resources.Any(x => x.Mode == ResourceAcquisitionMode.Reference);
            var workloads = accepted.Declaration.Spec.Components.Values.Any(x => x.Workloads.Count > 0);
            if (references && precondition is DeploymentPrecondition.ExpectedGeneration expected)
            {
                var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.EnvironmentRecordId == env && x.ApplicationRecordId == app, token);
                var previous = owner is null ? null : await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.DeploymentId == owner.Id && x.Generation == expected.Generation.Value, token);
                if (owner is null || owner.AppliedGeneration != expected.Generation.Value || owner.DesiredGeneration != expected.Generation.Value ||
                    previous is null || previous.InternalEffects || !await HasAppliedBaselineProofAsync(db, owner.Id, expected.Generation.Value, token))
                    return new(DurableChangeSetCreateStatus.ReferenceNotSupported);
                if (workloads)
                {
                    if (!previous.Inputs().Acquisitions.Equals(acquisitions) || new ResourceDelta(previous.Inputs().Providers.Declaration.Spec, accepted.Declaration.Spec)
                        .Entries.Any(x => x.Kind != ResourceDeltaKind.Unchanged)) return new(DurableChangeSetCreateStatus.ReferenceNotSupported);
                }
                else
                {
                    var original = previous.Inputs();
                    var delta = new ResourceDelta(original.Providers.Declaration.Spec, accepted.Declaration.Spec);
                    if (delta.Entries.Any(x => x.Kind is ResourceDeltaKind.Changed or ResourceDeltaKind.Removed))
                        return new(DurableChangeSetCreateStatus.UnsupportedShape);
                    var added = delta.Entries.Where(x => x.Kind == ResourceDeltaKind.Added).Select(x => x.Resource).ToHashSet();
                    if (acquisitions.Resources.Any(x => original.Providers.Declaration.Spec.Resources.ContainsKey(x.Resource)))
                        return new(DurableChangeSetCreateStatus.ReferenceNotSupported);
                    if (!added.SetEquals(acquisitions.Resources.Select(x => x.Resource)) || acquisitions.Resources.Any(x => x.Mode != ResourceAcquisitionMode.Reference))
                        return new(DurableChangeSetCreateStatus.InvalidInput);
                    acquisitions = new(original.Acquisitions.Resources.Concat(acquisitions.Resources));
                }
            }
            if (references && workloads && precondition is DeploymentPrecondition.ExpectedAbsence)
                return new(DurableChangeSetCreateStatus.ReferenceNotSupported);
            acquisitions.Validate(accepted.Declaration.Spec);
            if (accepted.Declaration.Spec.Resources.Count > 0)
            {
                if (precondition is DeploymentPrecondition.ExpectedAbsence && DurableActionAdmission.SingleAlias(accepted.Declaration.Spec) is null)
                    return new(DurableChangeSetCreateStatus.UnsupportedShape);
                var captured = await CaptureResourceProvidersAsync(db, accepted.Declaration, environment, token);
                if (captured.Status != DurablePlanStatus.Planned) return new(captured.Status == DurablePlanStatus.AliasNotResolved
                    ? DurableChangeSetCreateStatus.AliasNotResolved : DurableChangeSetCreateStatus.ProviderRevisionUnavailable);
                var provider = captured.Revision!;
                var environmentRow = await db.Environments.AsNoTracking().SingleAsync(x => x.Id == env, token);
                var providers = workloads ? await CaptureInternalProvidersAsync(db, accepted.Declaration, environment, captured.Resources, token)
                    : new ExactProviderResolutionSet(accepted.Declaration, environment, captured.Resources);
                var inputs = new DurablePlanningInputs(environment, EnvironmentId.Create(environmentRow.EnvironmentId), revision, providers)
                { Acquisitions = acquisitions };
                var structural = InternalResourceStage.Structure(inputs);
                var admitted = await AdmitAsync(db, structural.Providers.Declaration.Spec, provider, structural, token);
                if (admitted != DurablePlanStatus.Planned) return new(admitted switch
                {
                    DurablePlanStatus.ReferenceNotSupported => DurableChangeSetCreateStatus.ReferenceNotSupported,
                    DurablePlanStatus.ProviderCapabilityMismatch => DurableChangeSetCreateStatus.ProviderCapabilityMismatch,
                    DurablePlanStatus.ProviderRevisionUnavailable => DurableChangeSetCreateStatus.ProviderRevisionUnavailable,
                    _ => DurableChangeSetCreateStatus.InvalidInput
                });
                // Pure graph validation also closes typed sharing/evidence contracts before a row is accepted.
                var exact = await ResourceProviderDispatch.CaptureAsync(db, structural, _codecs, token);
                var representative = exact.Values.First();
                var reused = (await ImportedProducersAsync(db, structural, token)).Select(x => x.ResourceId).ToHashSet();
                var nodes = admission.DescribeProducers(structural.Providers.Declaration.Spec, representative.Provider, structural,
                    representative.Configuration, reused, exact);
                if (!admission.Supports(new(nodes) { Acquisitions = acquisitions }))
                    return new(references ? DurableChangeSetCreateStatus.ReferenceNotSupported : DurableChangeSetCreateStatus.ProviderCapabilityMismatch);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        { return new(DurableChangeSetCreateStatus.InvalidInput); }
        var change = await AddChangeSetAsync(db, environment, application, revision, precondition, null, actor, token, acquisitions);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(DurableChangeSetCreateStatus.Created, change);
    }
}
