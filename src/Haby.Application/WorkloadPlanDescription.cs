using Haby.Domain;

namespace Haby.Application;

/// <summary>Allowlisted intended Workload fields; never runtime/bootstrap or secret material.</summary>
public sealed record WorkloadPlanItem(string Component, string Workload, string Contract, string TargetName, string ImageReference, IReadOnlyList<string> Documents);
public enum WorkloadPlanDescriptionStatus { NotApplicable, Available, Unavailable }
public sealed record WorkloadPlanDescription(WorkloadPlanDescriptionStatus Status, IReadOnlyList<WorkloadPlanItem> Items);
public interface IWorkloadPlanDescriber
{
    string ImplementationId { get; }
    WorkloadPlanItem Describe(ManifestRevision revision, DurablePlanningInputs inputs, ProviderInput.Workload subject);
}

public sealed class DescribeWorkloadPlan(IManifestRevisionQuery revisions, IEnumerable<IWorkloadPlanDescriber> registrations)
{
    private readonly IWorkloadPlanDescriber[] describers = registrations.ToArray();
    public async ValueTask<WorkloadPlanDescription> ExecuteAsync(DurablePlanningInputs inputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var subjects = inputs.Providers.Entries.Values.Where(x => x.Input is ProviderInput.Workload).ToArray();
        if (subjects.Length > ConfigurationApplicationLimits.MaxItems) return new(WorkloadPlanDescriptionStatus.Unavailable, []);
        if (subjects.Length == 0) return new(WorkloadPlanDescriptionStatus.NotApplicable, []);
        try
        {
            inputs.Validate();
            var revision = await revisions.FindAsync(inputs.Revision, token);
            if (revision is null || revision.Reference != inputs.Revision || !revision.Declaration.Equals(inputs.Providers.Declaration)) return new(WorkloadPlanDescriptionStatus.Unavailable, []);
            var items = new List<WorkloadPlanItem>();
            foreach (var exact in subjects)
            {
                token.ThrowIfCancellationRequested();
                var matches = describers.Where(x => x.ImplementationId == exact.Implementation.Value).Take(2).ToArray();
                if (matches is not [var describer]) return new(WorkloadPlanDescriptionStatus.Unavailable, []);
                items.Add(describer.Describe(revision, inputs, (ProviderInput.Workload)exact.Input));
            }
            return new(WorkloadPlanDescriptionStatus.Available, items.OrderBy(x => x.Component, StringComparer.Ordinal).ThenBy(x => x.Workload, StringComparer.Ordinal).ToArray());
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return new(WorkloadPlanDescriptionStatus.Unavailable, []); }
    }
}
