using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class DurablePlanDescriptionTests
{
    [Fact]
    public async Task Exact_revision_and_validated_fields_alone_describe_intent_even_after_expiry_and_closure()
    {
        var (plan, revision) = await Setup();
        var query = new Query(revision);
        var description = await new DescribeDurablePlan(query, [new PostgreSqlPlanActionDescriber()]).ExecuteAsync(plan, default);
        Assert.Equal(PlanDescriptionStatus.Available, description.Status);
        Assert.Equal(IntendedActionKind.Create, description.Action!.Kind);
        Assert.Equal("database", description.Action.ResourceId.Value);
        Assert.Equal("postgresql.database/v1alpha1", description.Action.ResourceType.Value);
        Assert.Equal("sample_db", description.Action.Target.Name);
        Assert.Equal(plan.Inputs.Revision, query.Requested);
        Assert.Equal(description, await new DescribeDurablePlan(query, [new PostgreSqlPlanActionDescriber()]).ExecuteAsync(plan with
        { ExpiresAt = DateTimeOffset.MinValue, ClosedAt = DateTimeOffset.UtcNow }, default));
        Assert.DoesNotContain("unrelated-secret-sentinel", description.ToString());
    }

    [Fact]
    public async Task Trusted_revision_content_is_read_without_proving_historical_authenticity()
    {
        var (plan, _) = await Setup();
        var (_, replacement) = await Setup(databaseName: "other_db");
        var altered = replacement with { Reference = plan.Inputs.Revision };
        var result = await new DescribeDurablePlan(new Query(altered), [new PostgreSqlPlanActionDescriber()]).ExecuteAsync(plan, default);
        // The accepted storage contract trusts immutable revision rows. This read path
        // cannot authenticate historical content independently of that trusted source.
        Assert.Equal(PlanDescriptionStatus.Available, result.Status);
        Assert.Equal("other_db", result.Action!.Target.Name);
    }

    [Theory]
    [InlineData("missing", PlanDescriptionStatus.RevisionNotFound)]
    [InlineData("corrupt", PlanDescriptionStatus.InputCorrupt)]
    [InlineData("wrongRevision", PlanDescriptionStatus.PinnedInputMismatch)]
    [InlineData("wrongAlias", PlanDescriptionStatus.PinnedInputMismatch)]
    [InlineData("wrongImplementation", PlanDescriptionStatus.ActionDescriberUnavailable)]
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
        var result = await new DescribeDurablePlan(query, [new PostgreSqlPlanActionDescriber()]).ExecuteAsync(plan, default);
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
            await new DescribeDurablePlan(query, [new PostgreSqlPlanActionDescriber()]).ExecuteAsync(plan, cancellation.Token));
        Assert.Null(query.Requested);
    }

    [Fact]
    public async Task Generic_server_projection_dispatches_another_typed_action_without_a_PostgreSql_describer()
    {
        var (plan, revision) = await Setup(databaseName: "orders", resourceType: "queue.topic/v1alpha1",
            implementation: "example.queue/v1");
        var query = new Query(revision);
        var result = await new DescribeDurablePlan(query, [new TopicActionDescriber()]).ExecuteAsync(plan, default);
        Assert.Equal(PlanDescriptionStatus.Available, result.Status);
        Assert.Equal("queue.topic/v1alpha1", result.Action!.ResourceType.Value);
        Assert.Equal("database", result.Action.ResourceId.Value);
        Assert.Equal("orders", result.Action.Target.Name);
        Assert.Equal(plan.Inputs.Revision, query.Requested);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Missing_or_ambiguous_exact_implementation_describer_fails_closed(int count)
    {
        var (plan, revision) = await Setup();
        var registrations = Enumerable.Range(0, count).Select(_ => new PostgreSqlPlanActionDescriber());
        var result = await new DescribeDurablePlan(new Query(revision), registrations).ExecuteAsync(plan, default);
        Assert.Equal(PlanDescriptionStatus.ActionDescriberUnavailable, result.Status);
        Assert.Null(result.Action);
    }

    private sealed class TopicActionDescriber : IPlanActionDescriber
    {
        public string ImplementationId => "example.queue/v1";
        public DurablePlanDescription Describe(ManifestRevision revision, DurablePlanningInputs inputs)
        {
            var resource = revision.Declaration.Spec.Resources.Single();
            var detail = new TopicActionDetail(((ConfigurationString)resource.Value.Properties.Members["name"]).Value);
            return new(PlanDescriptionStatus.Available, new IntendedPlanAction(IntendedActionKind.Create,
                resource.Key, resource.Value.Type, new IntendedResourceTarget(detail.TopicName)));
        }
        private sealed record TopicActionDetail(string TopicName);
    }

    private static async Task<(DurablePlan Plan, ManifestRevision Revision)> Setup(bool unsupported = false, string databaseName = "sample_db",
        string resourceType = "postgresql.database/v1alpha1",
        string implementation = PostgreSqlDatabaseCheckpointCapability.ImplementationId)
    {
        var store = new InMemoryManifestImportStore();
        var definition = resourceType == PostgreSqlDatabaseContract.Id.Value ? PostgreSqlDatabaseContract.Definition
            : new ResourceTypeDefinition(ResourceTypeId.Create(resourceType), """{"type":"object"}""");
        var catalog = new ManifestContractCatalog([definition], [], [], []);
        var spec = unsupported ? "{}" : """
            {"resources":{"database":{"type":"postgresql.database/v1alpha1","providerRef":"main","properties":{"name":"sample_db"}}}}
            """;
        var envelope = """
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"description-test","displayName":"unrelated-secret-sentinel"},"spec":
            """ + spec.Replace("sample_db", databaseName, StringComparison.Ordinal).Replace("postgresql.database/v1alpha1", resourceType, StringComparison.Ordinal) + "}}";
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            store, catalog, TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes(envelope), "actor"));
        var revision = store.Find(imported.ManifestRevisionRef)!;
        var inputs = new DurablePlanningInputs(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"), revision.Reference,
            ProviderAlias.Create("main"), ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(7),
            implementation);
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
