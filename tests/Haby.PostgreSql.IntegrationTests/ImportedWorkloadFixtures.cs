using Haby.Application;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    // Reuse the existing real adapter preparation/receipt fixtures. Only Kubernetes I/O is synthetic.
    internal sealed record ImportedWorkloadFixture(IInternalActionHandler Create, IInternalActionHandler Identity,
        IInternalActionHandler Update, Func<int> CreateWrites, Func<int> UpdateWrites);
    internal static ImportedWorkloadFixture ImportedWorkloadHandlers()
    {
        var create = new ConfiguredSubmissionFixture();
        var identity = new IdentityFixture();
        var update = new UpdateSubmissionFixture();
        return new(create, identity, update, () => create.Writes, () => update.Writes);
    }
}
