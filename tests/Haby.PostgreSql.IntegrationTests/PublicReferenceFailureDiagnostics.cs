using System.Globalization;
using Grpc.Core;
using Haby.Operator.Contracts;

namespace Haby.PostgreSql.IntegrationTests;

internal enum PublicReferenceCheckpoint
{
    PublishedSample, Catalog, Build, Issuance, Start, Authentication, Setup, InvalidIntake, ConsumerConfirmation,
    PeerConfirmation, Reuse, Delivery, Bindings, ClosedUpdate, Preparation, ProviderPreflight, PlanCommit,
    Submit, PreparationResult, PreparationPostconditions, Revocation, Stop, Unknown
}
internal enum PublicReferenceChild { None, Issuance, First, Restart }

// One non-parameterized fact owns this state. No identifiers, exception text or child output enter the CSV.
// Columns: method, case, startup, event, phase, type, outcome, stage, members, prepared documents, writes, child, exit.
// Phase is an explicit last checkpoint, never an inferred cause.
internal sealed class PublicReferenceFailureDiagnostics(Action<string>? capture = null)
{
    internal const string Method = "Haby.PostgreSql.IntegrationTests." + nameof(OperatorHostIntegrationTests) + "." +
        nameof(OperatorHostIntegrationTests.Published_Reference_adapter_uses_stock_authenticated_intake_confirmation_delivery_and_restart);
    internal const string Case = "93bbc15d29e759674f243fbd6fb51d99ddbe999309113b17276a0b9a43516c8c";
    private readonly HashSet<Exception> recorded = new(ReferenceEqualityComparer.Instance);
    private PublicReferenceCheckpoint checkpoint;
    private int startup = -1, members = -1, documents = -1, writes = -1;
    private string outcome = "Missing", stage = "Missing";

    internal void At(PublicReferenceCheckpoint value) => checkpoint = value;
    internal void Startup(int value) => startup = value is 0 or 1 ? value : -1;
    internal Task RunAsync(Func<Action<Func<ValueTask>>, Task> body) =>
        HostFailureDiagnostics.RunAsync(body, () => HostFailurePhase.Catalog, () => null, Record);

    internal void Observe(WorkloadSubmissionReply reply, int externalWrites)
    {
        outcome = reply.Outcome switch
        {
            "submitted" => "Submitted",
            "stopped" => "Stopped",
            "cancelled" => "Cancelled",
            "unavailable" => "Unavailable",
            "" => "Missing",
            _ => "Other"
        };
        stage = reply.Stage switch
        {
            "Inspection" => "Inspection",
            "Recovery" => "Recovery",
            "Documents" => "Documents",
            "Identity" => "Identity",
            "Publication" => "Publication",
            "Deployment" => "Deployment",
            "" => "Missing",
            _ => "Other"
        };
        members = Bound(reply.Progress?.Members.Count ?? -1);
        documents = Bound(reply.Progress?.Members.Count(x => x.Documents == "prepared") ?? -1);
        writes = Bound(externalWrites);
        Emit("observation", null);
    }

    internal void ChildExit(PublicReferenceChild child, int? code) => Emit("child-exit", null, child, code);
    private static int Bound(int value) => value is >= 0 and <= 65536 ? value : -1;
    internal void Record(HostFailurePhase phase, Exception error, StatusCode? _)
    {
        if (!recorded.Add(error) && phase != HostFailurePhase.Cleanup) return;
        Emit(phase == HostFailurePhase.Cleanup ? "cleanup" : "primary", error);
    }

    internal string Render(string kind, Exception? error, PublicReferenceChild child = PublicReferenceChild.None, int? code = null)
    {
        // Even direct test calls cannot make the formatter copy arbitrary strings or type names.
        var entry = kind switch { "primary" => "primary", "cleanup" => "cleanup", "child-exit" => "child-exit", "observation" => "observation", _ => "unknown" };
        var phase = entry == "cleanup" ? "Cleanup" : Enum.IsDefined(checkpoint) ? checkpoint.ToString() : "Unknown";
        var type = error switch
        {
            null => "None",
            Xunit.Sdk.XunitException => "XunitException",
            RpcException => "RpcException",
            OperationCanceledException => "OperationCanceledException",
            IOException => "IOException",
            InvalidOperationException => "InvalidOperationException",
            _ => "Other"
        };
        return string.Join(',', Method, Case, startup.ToString(CultureInfo.InvariantCulture), entry, phase, type,
            outcome, stage, members.ToString(CultureInfo.InvariantCulture), documents.ToString(CultureInfo.InvariantCulture),
            writes.ToString(CultureInfo.InvariantCulture), Enum.IsDefined(child) ? child.ToString() : "Unknown",
            (code ?? -1).ToString(CultureInfo.InvariantCulture));
    }

    private void Emit(string kind, Exception? error, PublicReferenceChild child = PublicReferenceChild.None, int? code = null)
    {
        try
        {
            if (capture is null) HostFailureDiagnostics.RecordPublicReference(this, kind, error, child, code);
            else capture(Render(kind, error, child, code));
        }
        catch (Exception) { Console.Error.WriteLine("Allowlisted public Reference diagnostic unavailable; primary failure preserved."); }
    }
}
