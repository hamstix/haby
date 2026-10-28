using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class DurablePlanDescriptionTests
{
    [Fact]
    public async Task Exact_revision_and_validated_fields_alone_describe_intent_even_after_expiry_and_closure()
    {
        var (plan, revision) = await Setup();
        var query = new Query(revision);
        var description = await new DescribeDurablePlan(query).ExecuteAsync(plan, default);
        Assert.Equal(PlanDescriptionStatus.Available, description.Status);
        Assert.Equal(IntendedActionKind.Create, description.Action!.Kind);
        Assert.Equal("database", description.Action.ResourceId.Value);
        Assert.Equal("postgresql.database/v1alpha1", description.Action.ResourceType.Value);
        Assert.Equal("sample_db", Assert.IsType<IntendedActionDetail.PostgreSqlDatabase>(description.Action.Detail).DatabaseName);
        Assert.Equal(plan.Inputs.Revision, query.Requested);
        Assert.Equal(description, await new DescribeDurablePlan(query).ExecuteAsync(plan with
        { ExpiresAt = DateTimeOffset.MinValue, ClosedAt = DateTimeOffset.UtcNow }, default));
        Assert.DoesNotContain("unrelated-secret-sentinel", description.ToString());
    }

    [Theory]
    [InlineData("missing", PlanDescriptionStatus.RevisionNotFound)]
    [InlineData("corrupt", PlanDescriptionStatus.InputCorrupt)]
    [InlineData("wrongRevision", PlanDescriptionStatus.PinnedInputMismatch)]
    [InlineData("wrongAlias", PlanDescriptionStatus.PinnedInputMismatch)]
    [InlineData("wrongImplementation", PlanDescriptionStatus.PinnedInputMismatch)]
    [InlineData("unsupported", PlanDescriptionStatus.UnsupportedShape)]
    public async Task Unavailable_inputs_have_bounded_value_free_outcomes(string fault, PlanDescriptionStatus expected)
    {
        var (plan, revision) = await Setup(fault == "unsupported");
        if (fault == "wrongRevision") revision = revision with
        {
            Reference = ManifestRevisionRef.Create(
            revision.Reference.ApplicationRecordId, ManifestRevisionNumber.Create(2))
        };
        if (fault == "wrongAlias") plan = plan with { Inputs = plan.Inputs with { Alias = ProviderAlias.Create("other") } };
        if (fault == "wrongImplementation") plan = plan with { Inputs = plan.Inputs with { ImplementationId = "secret-sentinel" } };
        var query = new Query(fault == "missing" ? null : revision) { Corrupt = fault == "corrupt" };
        var result = await new DescribeDurablePlan(query).ExecuteAsync(plan, default);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Action);
        Assert.DoesNotContain("secret-sentinel", result.ToString());
    }

    [Fact]
    public async Task Cancellation_propagates_without_reading_or_translating_to_unavailable()
    {
        var (plan, revision) = await Setup();
        var query = new Query(revision);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new DescribeDurablePlan(query).ExecuteAsync(plan, cancellation.Token));
        Assert.Null(query.Requested);
    }

    private static async Task<(DurablePlan Plan, ManifestRevision Revision)> Setup(bool unsupported = false)
    {
        var store = new InMemoryManifestImportStore();
        var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition], [], [], []);
        var spec = unsupported ? "{}" : """
            {"resources":{"database":{"type":"postgresql.database/v1alpha1","providerRef":"main","properties":{"name":"sample_db"}}}}
            """;
        var envelope = """
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"description-test","displayName":"unrelated-secret-sentinel"},"spec":
            """ + spec + "}}";
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            store, catalog, TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes(envelope), "actor"));
        var revision = store.Find(imported.ManifestRevisionRef)!;
        var inputs = new DurablePlanningInputs(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"), revision.Reference,
            ProviderAlias.Create("main"), ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(7),
            PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        return (new DurablePlan(DeploymentPlanId.New(), DeploymentChangeSetId.New(), inputs, "unchanged-fingerprint",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "actor", null, null), revision);
    }

    private sealed class Query(ManifestRevision? revision) : IManifestRevisionQuery
    {
        internal ManifestRevisionRef? Requested { get; private set; }
        internal bool Corrupt { get; init; }
        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference, CancellationToken cancellationToken = default)
        {
            Requested = reference;
            if (Corrupt) throw new InvalidDataException("secret-sentinel corrupt payload");
            return ValueTask.FromResult(revision);
        }
    }
}
