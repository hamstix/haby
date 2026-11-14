using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class PostgreSqlDurableDeploymentPlanner
{
    // Friend-only composition supplies exact handlers. Preparation is pure; these
    // preview identities are never persisted as an intent, attempt or result.
    internal async Task<bool> AdmitInternalWorkloadsAsync(ManifestCatalogDbContext db, DurablePlanningInputs inputs,
        string applicationVersion, IReadOnlyList<IInternalActionHandler> handlers, CancellationToken token)
    {
        try
        {
            inputs.Validate();
            var spec = inputs.Providers.Declaration.Spec;
            if (spec.Parameters.Count != 0 || spec.Exports.Count != 0) return false;
            if (spec.Components.Values.Any(c => c.Bindings.Count != 0 || c.Documents.Count != 0)) return false;
            var subjects = inputs.Providers.Entries.Values.Where(x => x.Input is ProviderInput.Workload).ToArray();
            if (subjects.Length == 0) return false;
            var key = new DurableOperationKey(ApplicationDeploymentId.New(), DeploymentGeneration.Create(1), OperationId.Create("admission"));
            foreach (var exact in subjects)
            {
                var subject = (ProviderInput.Workload)exact.Input;
                var contract = spec.Components[subject.Component].Workloads[subject.Id].Type.Value;
                var provider = Guid.Parse(exact.Instance.ToString());
                var environment = Guid.Parse(inputs.Environment.ToString());
                var instance = await db.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider && x.EnvironmentRecordId == environment, token);
                var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == provider &&
                    x.EnvironmentRecordId == environment && x.RevisionNumber == exact.Configuration.Value && x.IsSealed, token);
                if (instance is null || revision is null || revision.ImplementationId != exact.Implementation.Value ||
                    !await CodecAvailableAsync(db, revision, token) || exact.Placement is null) return false;
                var matches = handlers.Where(x => x.Contract.Subject == contract && x.Contract.ProviderKind == instance.ProviderKindId &&
                    x.Contract.Configuration == revision.ConfigurationShapeId && x.Contract.Implementation == exact.Implementation.Value).ToArray();
                if (matches is not [var handler]) return false;
                var context = new InternalActionContext(key, inputs, subject, exact, PreparedConfiguration(instance.ProviderKindId, revision),
                    applicationVersion, Guid.CreateVersion7(), Guid.CreateVersion7(), InternalPlacementInput.Decode(exact.Placement).Declaration);
                var payload = handler.Prepare(context);
                if (!handler.Validate(context, payload)) return false;
            }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        { return false; }
    }
}
