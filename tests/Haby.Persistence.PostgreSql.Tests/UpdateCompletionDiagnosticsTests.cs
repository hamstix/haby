using Haby.Persistence.PostgreSql;
using Xunit;
namespace Haby.Persistence.PostgreSql.Tests;

public sealed class UpdateCompletionDiagnosticsTests
{
    [Fact]
    public void Actual_static_closure_refusals_have_fixed_predicates_and_unknown_text_is_redacted()
    {
        Assert.Equal(UpdateCompletionPredicate.RetirementProgressChanged, UpdateCompletionDiagnostics.Predicate(new("Retirement progress changed.")));
        Assert.Equal(UpdateCompletionPredicate.CurrentObjectReceiptMismatch, UpdateCompletionDiagnostics.Predicate(new("Current-object receipt mismatch.")));
        Assert.Equal(UpdateCompletionPredicate.UnretiredPublicationChanged, UpdateCompletionDiagnostics.Predicate(new("Unretired publication changed.")));
        Assert.Equal(UpdateCompletionPredicate.Unknown, UpdateCompletionDiagnostics.Predicate(new("Retirement progress changed.\nprotected-bootstrap")));
    }
    [Fact]
    public void Diagnostic_observer_fault_cannot_change_the_control_flow()
    {
        UpdateCompletionDiagnostics.Emit(_ => throw new InvalidDataException("protected"), UpdateCompletionStage.CatalogHash);
        UpdateCompletionDiagnostic? seen = null;
        UpdateCompletionDiagnostics.Emit(value => seen = value, UpdateCompletionStage.PrerequisiteIncomplete, UpdateCompletionPredicate.RetirementProgressChanged);
        Assert.Equal(new(UpdateCompletionStage.PrerequisiteIncomplete, UpdateCompletionPredicate.RetirementProgressChanged), seen);
    }
}
