using Haby.Application;
using Haby.Kubernetes;
using k8s.Autorest;

internal static class ConfiguredRetirementDiagnostics
{
    private static readonly string[] Stages = ["Unknown", "Begin", "Recipients", "ClosureSource", "CaptureSubmitted", "ObsoleteProgress", "RetirementMarker", "CurrentRuntime", "ConfigurationComplete", "FreshSamples", "RetirementStart", "Revoke", "Publications", "CatalogHash", "SamplesHash", "AppliedCas", "CloseRequest", "PrerequisiteIncomplete"];
    private static readonly string[] Predicates = ["Unknown", "InternalUPDATECompletionRequiresAnUpdateInventory", "UPDATEInventoryRequired", "UpdateExpectedAppliedChanged", "UpdateCompletionExceedsCheckpointBound", "UpdateAppliedCompareAndSwapFailed", "UpdateInventoryMissing", "UpdateGenerationSuperseded", "UpdateAttemptsChanged", "ObsoleteInventoryChanged", "UnretiredPublicationChanged", "RetirementProgressChanged", "RetirementStartMismatch", "RetirementStartRecipientEvidenceInvalid", "ConfigurationAuthorityRequired", "RetirementNotStarted", "FrozenObsoletePermissionMismatch", "ObsoletePublicationHasAnActiveLiveReference", "RetirementProgressMismatch", "CurrentObsoletePermissionNoLongerMatchesInventory", "FrozenCompletionInventoryRequired", "CompletionProofUnavailable", "CompletionProofCorrupt", "CompletionProofOriginMismatch", "CompletionReadinessEvidenceChanged", "CompletionPrerequisiteEvidenceMismatch", "CompletionRequestUnavailable", "CompletionRequestOutcomeMismatch", "ReadinessSampleExceedsBound", "ReadinessSampleCoverageMismatch", "FreshCompleteSetReadinessRequired", "FreshReadinessSampleOriginMismatch", "CompletionCheckpointReferenceMismatch", "BindingBoundExceeded", "PublicationBoundExceeded", "CurrentPublicationPrerequisiteUnavailable", "CompletionPrerequisiteTextExceedsBound", "UnexpectedActionInventory", "CompleteActionEvidenceRequired", "ManagedInventoryBoundScopeMismatch", "PreparationInventoryBoundExceeded", "CompletionPublicationCoverageMismatch", "PublicationOriginRequired", "RetainedPublicationEvidenceMismatch", "CompletionEvidenceExceedsBound", "FrozenUpdateRequired", "UpdateCompletionCorrupt", "UpdateCompletionMissing", "UpdateCompletionOriginMismatch", "UpdateCompletionPrerequisitesChanged", "UpdateCompletionRequestMismatch", "CurrentObjectEvidenceExceedsBound", "CompleteUpdateCurrentObjectEvidenceRequired", "CurrentObjectReceiptMismatch", "RetirementStartEvidenceMismatch", "RetirementStartOriginMismatch", "ExactObsoleteRevocationProofMismatch"];
    private static readonly string[] Reasons = ["not-ready", "applied", "inventory-conflict", "prerequisite-incomplete", "generation-superseded",
        "current-authority-unavailable", "unsupported-inventory", "commit-unconfirmed", "old-recipients-present", "recipient-evidence-incomplete"];
    private static string Flag(bool value) => value ? "true" : "false";
    internal static string Closure(string stage, string predicate) => "Q243 configured retirement Closure " +
        (Stages.Contains(stage, StringComparer.Ordinal) ? stage : "Unknown") + " " +
        (Predicates.Contains(predicate, StringComparer.Ordinal) ? predicate : "Unknown");
    internal static string Recipients(DeploymentRecipientDiagnostic value) => "Q243 configured retirement Recipients " +
        Count(value.Parents) + " " + Count(value.Recipients) + " " + Count(value.Previous) + " " + Projection(value.ParentFailure) + " " + Projection(value.PodFailure);
    private static string Count(int value) => value is >= 0 and <= 16384 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    private static string Projection(RecipientProjectionFailure value) => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    internal static string Category(Exception error) => error switch
    {
        OperationCanceledException => "Canceled",
        HttpOperationException => "Api",
        InvalidDataException => "Data",
        TimeoutException => "Timeout",
        InvalidOperationException => "Condition",
        _ => "Unknown"
    };
    internal static string Failure(string phase, Exception error) => "Q243 configured retirement " +
        (phase is "WaitThrew" or "Primary" or "ReleaseCleanup" or "NamespaceCleanup" ? phase : "Unknown") + " " + Category(error);
    internal static string Returned(RolloutResult result, long expected) => "Q243 configured retirement WaitReturned " + Flag(result.Ready) + " " +
        Flag(result.Applied) + " " + Flag(result.AppliedGeneration == expected) + " " +
        (Reasons.Contains(result.CompletionReason, StringComparer.Ordinal) ? result.CompletionReason : "unknown") + " " +
        (result.Stop switch
        {
            RolloutStop.Complete => "Complete",
            RolloutStop.CallLimit => "CallLimit",
            RolloutStop.RequestDeadline => "RequestDeadline",
            RolloutStop.Canceled => "Canceled",
            RolloutStop.Interrupted => "Interrupted",
            _ => "Unknown"
        });
    internal static string Condition(string condition, bool value) => "Q243 configured retirement Condition " +
        (condition is "U11Ready" or "U11Applied" or "U11Generation" ? condition : "Unknown") + " " + Flag(value);
    internal static AggregateException Combine(Exception primary, Exception cleanup) => new(primary, cleanup);
    internal static string Finalizer(bool? present) => "Q243 configured retirement OwnedFinalizer " + (present is { } value ? Flag(value) : "unknown");
    internal static async ValueTask<RolloutResult> WaitAsync(Func<ValueTask<RolloutResult>> wait, long expected)
    {
        Console.WriteLine("Q243 configured retirement WaitStart");
        try { var result = await wait(); Console.WriteLine(Returned(result, expected)); return result; }
        catch (Exception error) { Console.WriteLine(Failure("WaitThrew", error)); throw; }
    }
}
