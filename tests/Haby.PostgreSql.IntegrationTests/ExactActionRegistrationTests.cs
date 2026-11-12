using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Host;
using Haby.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class ExactActionRegistrationTests
{
    [Theory]
    [InlineData("resource")]
    [InlineData("kind")]
    [InlineData("configuration")]
    [InlineData("implementation")]
    [InlineData("action")]
    public void Every_exact_key_dimension_is_distinct(string dimension)
    {
        var expected = PostgreSqlDatabaseCreateAction.RegistrationKey;
        var handler = new Stub(expected);
        var registry = new DurableActionRegistry([handler], [handler]);
        var different = dimension switch
        {
            "resource" => expected with { Admission = expected.Admission with { ResourceContract = ResourceTypeId.Create("sample.queue/v1") } },
            "kind" => expected with { Admission = expected.Admission with { ProviderKind = ProviderKindId.Create("sample/v1") } },
            "configuration" => expected with { Admission = expected.Admission with { ConfigurationContract = ProviderConfigurationContractId.Create("sample.configuration/v1") } },
            "implementation" => expected with { Admission = expected.Admission with { ImplementationId = ProviderImplementationId.Create("sample.impl/v1") } },
            _ => expected with { ActionContract = OperationContractId.Create("postgresql.database.create/v2") }
        };
        Assert.Same(handler, registry.Find(expected));
        Assert.Null(registry.Find(different));
        if (dimension != "action") Assert.Null(registry.Find(different.Admission));
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("executor")]
    [InlineData("invalid")]
    public void Invalid_or_overlapping_current_registration_fails_Host_construction(string kind)
    {
        var key = PostgreSqlDatabaseCreateAction.RegistrationKey;
        Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"], services =>
            {
                if (kind == "executor") services.AddSingleton<IDurableActionHandler>(new Stub(key));
                else services.AddSingleton<IDurableActionAdmissionHandler>(new Stub(kind == "invalid"
                    ? key with { Admission = key.Admission with { ImplementationId = null! } }
                    : key));
            }));
    }

    private sealed class Stub(DurableActionExecutionKey key) : IDurableActionHandler, IDurableActionAdmissionHandler
    {
        public DurableActionExecutionKey Key => key;
        public bool Supports(DurableOperationNode node) => true;
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) => throw new NotSupportedException();
        public bool Validate(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) => throw new NotSupportedException();
        public ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
