using Haby.Application;
using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class OperatorAdmissionRegistrationTests
{
    [Fact]
    public void Ambiguous_subject_handlers_refuse_at_composition_before_any_catalog_or_effect_call()
    {
        var handler = new Handler();
        Assert.Throws<InvalidOperationException>(() => new PostgreSqlOperatorDeploymentAdmission(null!, null!, null!, [handler, handler]));
        Assert.IsAssignableFrom<IOperatorDeploymentAdmission>(new PostgreSqlOperatorDeploymentAdmission(null!, null!, null!, []));
    }

    private sealed class Handler : IInternalActionHandler
    {
        public InternalActionContract Contract { get; } = new("sample.workload/v1", "sample", "sample.config/v1", "sample.impl/v1", "sample.create/v1", "sample.intent/v1", "sample.receipt/v1");
        public bool ObservationRecovery => false;
        public string Prepare(InternalActionContext context) => throw new InvalidOperationException();
        public bool Validate(InternalActionContext context, string payload) => throw new InvalidOperationException();
        public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation) => throw new InvalidOperationException();
        public ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token) => throw new InvalidOperationException();
    }
}
