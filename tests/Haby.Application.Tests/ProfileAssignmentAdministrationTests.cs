using System.Text;
using Haby.Application;
using Haby.Domain;
using Xunit;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Application.Tests;

public sealed class ProfileAssignmentAdministrationTests
{
    private const string Empty = """{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfileAssignment","spec":{"target":{"kind":"environment"},"decisions":[]}}""";
    private static readonly EnvironmentMetadata Environment = new(EnvironmentRecordId.New(), EnvironmentId.Create("production"));
    private static ProfileAssignmentAdministration Admin(Ports ports) => new(ports, ports, ports, ports, TimeProvider.System);

    [Fact]
    public async Task Portable_validation_invalid_context_and_untrusted_actor_never_reach_storage()
    {
        var ports = new Ports(); var admin = Admin(ports); var bytes = Encoding.UTF8.GetBytes(Empty);
        Assert.True(ProfileAssignmentAdministration.Validate(bytes).IsSuccess);
        Assert.False(ProfileAssignmentAdministration.Validate(Encoding.UTF8.GetBytes("{bad")).IsSuccess);
        Assert.Equal(AssignmentOutcome.InvalidContext, (await admin.ApplyAsync(bytes, "production", "foreign-version", new AssignmentPrecondition.ExpectedAbsence(), "actor")).Outcome);
        Assert.Equal(AssignmentOutcome.InvalidContext, (await admin.ApplyAsync(bytes, "invalid environment", null, new AssignmentPrecondition.ExpectedAbsence(), "actor")).Outcome);
        foreach (var actor in new[] { "", "invalid\nactor", new string('a', 257) })
            await Assert.ThrowsAsync<ArgumentException>(async () => await admin.ApplyAsync(bytes, "production", null, new AssignmentPrecondition.ExpectedAbsence(), actor));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await admin.ApplyAsync(bytes, "production", null, null!, "actor"));
        Assert.Equal(0, ports.Calls);
    }

    [Fact]
    public async Task Cancellation_before_IO_refuses_but_confirmed_acceptance_is_not_discarded()
    {
        var ports = new Ports(); var admin = Admin(ports); var bytes = Encoding.UTF8.GetBytes(Empty);
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await admin.ApplyAsync(bytes, "production", null, new AssignmentPrecondition.ExpectedAbsence(), "actor", before.Token));
        Assert.Equal(0, ports.Calls);
        using var after = new CancellationTokenSource(); ports.AfterCommit = after.Cancel;
        var result = await admin.ApplyAsync(bytes, "production", null, new AssignmentPrecondition.ExpectedAbsence(), "actor", after.Token);
        Assert.True(after.IsCancellationRequested);
        Assert.Equal(AssignmentOutcome.Created, result.Outcome);
        Assert.Equal("actor", result.Assignment!.Audit.TechnicalActorId);
    }

    private sealed class Ports : IEnvironmentCatalog, IProfileAssignmentScopeQuery, IConfigurationProfileRevisionQuery, IProfileAssignmentStore
    {
        internal int Calls;
        internal Action? AfterCommit;
        public ValueTask<EnvironmentMetadata?> GetAsync(EnvironmentId name, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult<EnvironmentMetadata?>(Environment); }
        public ValueTask<AssignmentResult> ApplyAsync(EnvironmentMetadata environment, ConfigurationProfileAssignment declaration, AssignmentPrecondition precondition, AssignmentAudit audit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            var accepted = new PersistedProfileAssignment(environment, declaration, AssignmentToken.New(), audit);
            AfterCommit?.Invoke();
            return ValueTask.FromResult(new AssignmentResult(AssignmentOutcome.Created, accepted));
        }
        public ValueTask<EnvironmentCreation> CreateOrGetAsync(EnvironmentId name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<EnvironmentMetadata>> ListAsync(EnvironmentId? after, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> ApplicationExistsAsync(ApplicationId application, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ManifestRevision?> FindVersionAsync(ApplicationId application, ApplicationVersion version, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AcceptedConfigurationProfile?> FindAsync(ProfileRevisionRef reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PersistedProfileAssignment?> FindAsync(EnvironmentMetadata environment, ProfileAssignmentTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
