using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostCompositionTests
{
    [Theory]
    [InlineData("admission")]
    [InlineData("execution")]
    [InlineData("evidence")]
    [InlineData("identity")]
    [InlineData("admission-key")]
    public async Task Reference_registration_requires_exact_admission_execution_and_evidence_before_listening(string missing)
    {
        var port = AvailablePort();
        var owned = new AsyncResource(false);
        Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.BuildComposed(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"],
            (services, _) => ReferenceRegistrations(services, owned, missing)));
        Assert.True(owned.Disposed);
        await RequireNotListeningAsync(port);
    }

    [Fact]
    public async Task Complete_Reference_registration_for_stock_Resource_preserves_stock_and_caller_owned_lifecycle()
    {
        var port = AvailablePort();
        var owned = new AsyncResource(false);
        await using (var app = OperatorHostApplication.BuildComposed(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"],
            (services, _) => ReferenceRegistrations(services, owned, "none")))
        {
            var probe = app.Services.GetRequiredService<RegistrationProbe>();
            var registry = app.Services.GetRequiredService<DurableActionRegistry>();
            Assert.Same(probe, registry.Find(probe.Key));
            Assert.Same(probe, registry.Find(probe.Key.Admission));
            Assert.True(app.Services.GetRequiredService<ManifestContractCatalog>().TryGet(PostgreSqlDatabaseContract.Id, out _));
            Assert.False(owned.Disposed);
            await RequireNotListeningAsync(port);
        }
        Assert.True(owned.Disposed);
    }

    private static void ReferenceRegistrations(IServiceCollection services, AsyncResource owned, string missing)
    {
        services.AddSingleton(_ => owned);
        services.AddSingleton(p =>
        {
            p.GetRequiredService<AsyncResource>();
            var provider = p.GetServices<IProviderDeclarationAdapter>().Single(x =>
                x.Key.ImplementationId == PostgreSqlDatabaseCheckpointCapability.ImplementationId).Key;
            return new RegistrationProbe(new(new(PostgreSqlDatabaseContract.Id, provider) { Mode = ResourceAcquisitionMode.Reference },
                OperationContractId.Create("test.reference-registration/v1")));
        });
        if (missing != "admission") services.AddSingleton<IDurableActionAdmissionHandler>(p => missing == "admission-key"
            ? new RegistrationProbe(p.GetRequiredService<RegistrationProbe>().Key with { ActionContract = OperationContractId.Create("test.other-reference/v1") })
            : p.GetRequiredService<RegistrationProbe>());
        if (missing != "execution") services.AddSingleton<IDurableActionHandler>(p => p.GetRequiredService<RegistrationProbe>());
        if (missing != "evidence") services.AddSingleton<IExternalOperationOutputContract>(p => missing == "identity"
            ? new RegistrationProbe(p.GetRequiredService<RegistrationProbe>().Key, "other") : p.GetRequiredService<RegistrationProbe>());
    }

    // Registration metadata only: these methods must never execute during Host construction.
    private sealed class RegistrationProbe(DurableActionExecutionKey key, string output = "access") :
        IDurableActionAdmissionHandler, IReadOnlyResourceObservationHandler, IResourceReferenceOutputContract
    {
        public DurableActionExecutionKey Key => key;
        public DurableActionExecutionKey Producer => key;
        public ExternalOperationOutputIdentity Identity => new("test.reference-registration/v1", output);
        public ExternalOperationOutputIdentity OutputIdentity => new("test.reference-registration/v1", "access");
        public bool RequiresExternalOutput => true;
        public bool Supports(DurableOperationNode node) => node.Action == Key;
        public bool Validate(ManifestLocalId resource, NormalizedResource declaration, DurablePlanningInputs inputs) => throw new NotSupportedException();
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resource, NormalizedResource declaration, DurablePlanningInputs inputs) => throw new NotSupportedException();
        public ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken token) => throw new NotSupportedException();
        public bool Validate(DurableOperationNode node, ExternalOperationOutput value) => throw new NotSupportedException();
        public bool ValidateObservationEvidence(AcceptedOperation accepted, DurableActionPayload evidence) => throw new NotSupportedException();
        public bool ValidateReferenceIdentities(AcceptedOperation accepted, IReadOnlyList<ProviderResourceReservation> identities) => throw new NotSupportedException();
    }
}
