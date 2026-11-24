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
            acquisitions.Validate(accepted.Declaration.Spec);
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
                if (!workloads || owner is null || owner.AppliedGeneration != expected.Generation.Value ||
                    owner.DesiredGeneration != expected.Generation.Value || previous is null || previous.InternalEffects ||
                    !previous.Inputs().Acquisitions.Equals(acquisitions)) return new(DurableChangeSetCreateStatus.ReferenceNotSupported);
                var old = previous.Inputs().Providers.Declaration.Spec.Resources;
                if (!old.Keys.ToHashSet().SetEquals(accepted.Declaration.Spec.Resources.Keys) ||
                    old.Any(x => x.Value.Type != accepted.Declaration.Spec.Resources[x.Key].Type ||
                        x.Value.ProviderRef != accepted.Declaration.Spec.Resources[x.Key].ProviderRef ||
                        x.Value.DeletionPolicy != accepted.Declaration.Spec.Resources[x.Key].DeletionPolicy ||
                        !x.Value.Properties.Equals(accepted.Declaration.Spec.Resources[x.Key].Properties)))
                    return new(DurableChangeSetCreateStatus.ReferenceNotSupported);
            }
            if (references && workloads && precondition is DeploymentPrecondition.ExpectedAbsence)
                return new(DurableChangeSetCreateStatus.ReferenceNotSupported);
            if (accepted.Declaration.Spec.Resources.Count > 0)
            {
                var alias = DurableActionAdmission.SingleAlias(accepted.Declaration.Spec);
                if (alias is null) return new(DurableChangeSetCreateStatus.UnsupportedShape);
                var resolved = await aliases.ResolveAsync(environment, alias, token);
                if (resolved is null) return new(DurableChangeSetCreateStatus.AliasNotResolved);
                var provider = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.EnvironmentRecordId == env && x.ProviderInstanceId == Guid.Parse(resolved.Binding.InstanceId.ToString()) &&
                    x.RevisionNumber == resolved.Binding.RevisionNumber.Value && x.IsSealed, token);
                if (provider is null) return new(DurableChangeSetCreateStatus.ProviderRevisionUnavailable);
                var naming = (await PostgreSqlResourceNamingStore.CaptureAsync(db, env, alias.Value, token)).Snapshot;
                var resources = accepted.Declaration.Spec.Resources.Select(x => new ExactProviderResolution(new ProviderInput.Resource(x.Key),
                    environment, alias, resolved.Binding.InstanceId, resolved.Binding.RevisionNumber,
                    ProviderImplementationId.Create(resolved.ImplementationId), ResourceNamingCodec.Exact(naming))).ToArray();
                var environmentRow = await db.Environments.AsNoTracking().SingleAsync(x => x.Id == env, token);
                var providers = workloads ? await CaptureInternalProvidersAsync(db, accepted.Declaration, environment, resources, token)
                    : new ExactProviderResolutionSet(accepted.Declaration, environment, resources);
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
                var kind = (await db.ProviderInstances.AsNoTracking().SingleAsync(x => x.Id == provider.ProviderInstanceId, token)).ProviderKindId;
                var graph = admission.Plan(structural.Providers.Declaration.Spec,
                    new(kind, provider.ConfigurationShapeId, provider.ImplementationId), structural, PreparedConfiguration(kind, provider));
                if (!admission.Supports(graph)) return new(references ? DurableChangeSetCreateStatus.ReferenceNotSupported : DurableChangeSetCreateStatus.ProviderCapabilityMismatch);
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
