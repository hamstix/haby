using Haby.Application;
using Haby.Kubernetes;
using k8s.Autorest;
using k8s.Models;

internal static class ConfiguredRetirementDiagnostics
{
    // One instance belongs to the configured UPDATE caller, across reconstructed actions.
    internal sealed class Output(Action<string>? readinessReport = null)
    {
        private bool readinessSeen;
        internal bool ReadinessActive { get; set; }
        internal void Readiness(ReadinessInvalidDiagnostic value)
        {
            if (!ReadinessActive || readinessSeen) return;
            readinessSeen = true;
            (readinessReport ?? Console.WriteLine)(ReadinessRecord(value));
        }
        private bool recipientSeen, recipientFailureSeen, completionFailureSeen;
        internal void Recipient(DeploymentRecipientDiagnostic value)
        {
            var failed = value.ParentFailure != RecipientProjectionFailure.None || value.PodFailure != RecipientProjectionFailure.None;
            if (!recipientSeen || (failed && !recipientFailureSeen)) Console.WriteLine(Recipients(value));
            recipientSeen = true;
            recipientFailureSeen |= failed;
        }
        internal void Completion(string stage, string predicate)
        {
            // Successful closure steps repeat during bounded polling; U09-U12
            // and returned complete member sets carry the outcome proof. Retain
            // the first failed prerequisite, even after every successful poll.
            if (stage != "PrerequisiteIncomplete" || completionFailureSeen) return;
            completionFailureSeen = true;
            Console.WriteLine(Closure(stage, predicate));
        }
    }
    internal static string ReadinessRecord(ReadinessInvalidDiagnostic value)
    {
        var parts = value.Scalars.Split(' ');
        var valid = parts.Length == 10 && parts.All(x => x is "M" or "X" ||
            long.TryParse(x, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) &&
            n.ToString(System.Globalization.CultureInfo.InvariantCulture) == x);
        return "Q243 configured retirement ReadinessInvalid " + (Enum.IsDefined(value.Stage) ? value.Stage.ToString() : "Unknown") + " " +
            (valid ? value.Scalars : "X X X X X X X X X X");
    }
    private static readonly string[] Stages = ["Unknown", "Begin", "Recipients", "ClosureSource", "CaptureSubmitted", "ObsoleteProgress", "RetirementMarker", "CurrentRuntime", "ConfigurationComplete", "FreshSamples", "RetirementStart", "Revoke", "Publications", "CatalogHash", "SamplesHash", "AppliedCas", "CloseRequest", "PrerequisiteIncomplete"];
    private static readonly string[] Predicates = ["Unknown", "InternalUPDATECompletionRequiresAnUpdateInventory", "UPDATEInventoryRequired", "UpdateExpectedAppliedChanged", "UpdateCompletionExceedsCheckpointBound", "UpdateAppliedCompareAndSwapFailed", "UpdateInventoryMissing", "UpdateGenerationSuperseded", "UpdateAttemptsChanged", "ObsoleteInventoryChanged", "UnretiredPublicationChanged", "RetirementProgressChanged", "RetirementStartMismatch", "RetirementStartRecipientEvidenceInvalid", "ConfigurationAuthorityRequired", "RetirementNotStarted", "FrozenObsoletePermissionMismatch", "ObsoletePublicationHasAnActiveLiveReference", "RetirementProgressMismatch", "CurrentObsoletePermissionNoLongerMatchesInventory", "FrozenCompletionInventoryRequired", "CompletionProofUnavailable", "CompletionProofCorrupt", "CompletionProofOriginMismatch", "CompletionReadinessEvidenceChanged", "CompletionPrerequisiteEvidenceMismatch", "CompletionRequestUnavailable", "CompletionRequestOutcomeMismatch", "ReadinessSampleExceedsBound", "ReadinessSampleCoverageMismatch", "FreshCompleteSetReadinessRequired", "FreshReadinessSampleOriginMismatch", "CompletionCheckpointReferenceMismatch", "BindingBoundExceeded", "PublicationBoundExceeded", "CurrentPublicationPrerequisiteUnavailable", "CompletionPrerequisiteTextExceedsBound", "UnexpectedActionInventory", "CompleteActionEvidenceRequired", "ManagedInventoryBoundScopeMismatch", "PreparationInventoryBoundExceeded", "CompletionPublicationCoverageMismatch", "PublicationOriginRequired", "RetainedPublicationEvidenceMismatch", "CompletionEvidenceExceedsBound", "FrozenUpdateRequired", "UpdateCompletionCorrupt", "UpdateCompletionMissing", "UpdateCompletionOriginMismatch", "UpdateCompletionPrerequisitesChanged", "UpdateCompletionRequestMismatch", "CurrentObjectEvidenceExceedsBound", "CompleteUpdateCurrentObjectEvidenceRequired", "CurrentObjectReceiptMismatch", "RetirementStartEvidenceMismatch", "RetirementStartOriginMismatch", "ExactObsoleteRevocationProofMismatch"];
    private static readonly string[] Reasons = ["not-ready", "applied", "inventory-conflict", "prerequisite-incomplete", "generation-superseded",
        "current-authority-unavailable", "unsupported-inventory", "commit-unconfirmed", "old-recipients-present", "recipient-evidence-incomplete"];
    private static readonly string[] MemberCodes = ["ready", "rollout-pending", "progress-deadline", "status-invalid", "owned-state-conflict", "object-absent",
        "api-access-denied", "observation-unavailable", "api-rejected", "input-invalid", "runtime-authority-unavailable", "attempt-superseded",
        "clock-budget-elapsed", "call-stopped", "read-timeout", "not-submitted", "not-observed"];
    internal static string[] Members(RolloutResult result)
    {
        // Fixture diagnostic bound only. Unexpected larger inventories fail explicitly; never publish a partial set.
        if (result.Members.Length > 16) return ["Q243 configured retirement MembersOverflow"];
        var records = new List<string> { "Q243 configured retirement WaitMembers " + result.Members.Length + " " +
            result.Members.Count(x => x.Attempt is not null) + " " + result.Members.Count(x => x.Budget == RolloutBudget.Expired) + " " + Flag(result.Replayed) + " Unknown" };
        for (var index = 0; index < result.Members.Length; index++)
        {
            var member = result.Members[index]; var observation = member.Observation;
            records.Add("Q243 configured retirement Member " + index + " " + (observation.Outcome switch
            {
                RolloutOutcome.Ready => "Ready",
                RolloutOutcome.Pending => "Pending",
                RolloutOutcome.Conflict => "Conflict",
                RolloutOutcome.Unavailable => "Unavailable",
                RolloutOutcome.Refused => "Refused",
                RolloutOutcome.NotSubmitted => "NotSubmitted",
                RolloutOutcome.NotObserved => "NotObserved",
                _ => "Unknown"
            }) + " " + (MemberCodes.Contains(observation.Code, StringComparer.Ordinal) ? observation.Code : "Unknown") + " " +
                (member.Budget switch { RolloutBudget.Active => "Active", RolloutBudget.Expired => "Expired", _ => "Unknown" }) + " " +
                Flag(member.Attempt is not null) + " " + Flag(observation.Temporary) + " Unknown");
        }
        // Returned attempts describe submitted inventory, not HTTP admissions. Consumed recovery is not returned: Unknown.
        return records.ToArray();
    }
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
        (phase is "WaitThrew" or "Primary" or "ReleaseCleanup" or "NamespaceCleanup" or "DrainCleanup" or "HeldReadThrew" ? phase : "Unknown") + " " + Category(error);
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
        (condition is "U09Ready" or "U09NotApplied" or "U09OldRecipients" or "U09HeldUid" or "U09Terminating" or "U11Ready" or "U11Applied" or "U11Generation" ? condition : "Unknown") + " " + Flag(value);
    internal static AggregateException Combine(Exception primary, Exception cleanup) => new(primary, cleanup);
    internal static string Finalizer(bool? present) => "Q243 configured retirement OwnedFinalizer " + (present is { } value ? Flag(value) : "unknown");
    internal static async Task<V1Pod> ReadHeldAsync(Func<Task<V1Pod>> read)
    {
        Console.WriteLine("Q243 configured retirement HeldReadStart");
        try { var result = await read(); Console.WriteLine("Q243 configured retirement HeldReadReturned"); return result; }
        catch (Exception error) { Console.WriteLine(Failure("HeldReadThrew", error)); throw; }
    }
    internal static async ValueTask<RolloutResult> WaitAsync(Func<ValueTask<RolloutResult>> wait, long expected)
    {
        Console.WriteLine("Q243 configured retirement WaitStart");
        try
        {
            var result = await wait(); Console.WriteLine(Returned(result, expected));
            foreach (var record in Members(result)) Console.WriteLine(record);
            return result;
        }
        catch (Exception error) { Console.WriteLine(Failure("WaitThrew", error)); throw; }
    }
}
