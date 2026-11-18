using Haby.Application;

// Fixture-only closed records. No exception, payload or response text is accepted.
internal static class ConfiguredDeploymentUpdateDiagnostics
{
    internal enum Stage
    {
        Execute, ExpectedLoss, Recover, ReceiptPresence, ReceiptDecode, Readback,
        Projection, ObservationReceipt, ApiResponsePresence, ApiResponseReceipt,
        U06Outcome, U06Source, U06PatchCount, U06SavedUid, U06TargetImage,
        U06PatchPresence, U06ResourceVersion, U06PatchUid, U06ResponseUid, U06ResponseGeneration
    }
    internal enum State { Start, Completed, Returned, Observed, Passed, Failed }

    internal static string Progress(Stage stage, State state) => $"Q243 configured update {stage} {state}";
    internal static string Failure(Stage stage, int updateClientPatches) =>
        $"Q243 configured update {stage} failure unknown {Count(updateClientPatches)}";
    internal static string Result(Stage stage, InternalActionResult result, int updateClientPatches)
    {
        var outcome = result.Outcome switch
        {
            InternalActionOutcome.Confirmed => "Confirmed",
            InternalActionOutcome.Uncertain => "Uncertain",
            InternalActionOutcome.Refused => "Refused",
            InternalActionOutcome.Unavailable => "Unavailable",
            InternalActionOutcome.NotConfirmed => "NotConfirmed",
            _ => "unknown"
        };
        var code = result.Code is "confirmed" or "not-dispatched" or "manual-recovery-required" or "terminal-failure" or
            "managed-identity-conflict" or "managed-object-conflict" or "intent-invalid" or
            "response-confirmed" or "observation-confirmed" or "evidence-not-confirmed" or "response-unconfirmed" or
            "invalid-input" or "connection-unavailable" or "dispatch-uncertain" or "occupied-name" or
            "owned-state-conflict" or "uid-conflict" or "api-conflict" or "api-access-denied" or "action-canceled" or
            "managed-object-missing" or "action-budget" or "precondition-limit" or "object-absent" or "observation-unavailable"
            ? result.Code : "unknown";
        return $"Q243 configured update {stage} result {outcome} {code} {Count(updateClientPatches)}";
    }
    // Scope: Deployment PATCH sends through this UPDATE kernel client only.
    private static string Count(int value) => value is >= 0 and <= 3 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
}
