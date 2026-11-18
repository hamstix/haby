using Haby.Application;
using Haby.Kubernetes.Contracts;

// Fixture-only closed records. No exception message, payload or response text is emitted.
internal static class ConfiguredDeploymentUpdateDiagnostics
{
    internal enum Stage
    {
        Execute, ExpectedLoss, Recover, ReceiptPresence, ReceiptDecode, Readback,
        Projection, ObservationReceipt, ApiResponsePresence, ApiResponseReceipt,
        U06Outcome, U06Source, U06PatchCount, U06SavedUid, U06TargetImage,
        U06PatchPresence, U06ResourceVersion, U06PatchUid, U06ResponseUid, U06ResponseGeneration,
        DelegateExecute, DelegateObserve, ProofRuntimeAccount
    }
    internal enum Predicate { Intent, Readback, Ownership, Uid, Baseline, Target, Marker, Receipt, ResponseReceipt, RuntimeAccount }
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
            "managed-object-missing" or "action-budget" or "precondition-limit" or "object-absent" or "observation-unavailable" or
            "retained-identity-confirmed" or "retained-identity-unconfirmed"
            ? result.Code : "unknown";
        return $"Q243 configured update {stage} result {outcome} {code} {Count(updateClientPatches)}";
    }
    internal static string Delegated(Stage stage, InternalActionResult result, int updateClientPatches) =>
        Result(stage, result, updateClientPatches) + " receipt-present " + (result.Receipt is null ? "false" : "true");
    internal static string Wire(int? status, int updateClientPatches) =>
        $"Q243 configured update wire status {(status is >= 100 and <= 599 ? status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : status is null ? "no-response" : "unknown")} {Count(updateClientPatches)} {(status is null ? "false" : "true")}";
    internal static string Inspect(Predicate predicate, Action check)
    {
        try { check(); return Proof(predicate, true, null); }
        catch (WorkloadContractException error) { return Proof(predicate, false, error); }
        catch { return Proof(predicate, false, null); }
    }
    internal static string Proof(Predicate predicate, bool passed, WorkloadContractException? error) =>
        $"Q243 configured update proof {predicate} {(passed ? "passed confirmed" : "failed " + ContractCode(error))}";
    private static string ContractCode(WorkloadContractException? error) => error?.Code switch
    {
        // Source-audited fixed codes: DeploymentIntent, DeploymentObjects,
        // DeploymentResources and DeploymentPortsAndProbes. Never emit Code verbatim.
        "intent-format" => "intent-format",
        "intent-update-pair" => "intent-update-pair",
        "immutable-container" => "immutable-container",
        "immutable-runtime-mode" => "immutable-runtime-mode",
        "immutable-runtime-identity" => "immutable-runtime-identity",
        "binding" => "binding",
        "namespace" => "namespace",
        "name" => "name",
        "value" => "value",
        "projection" => "projection",
        "image" => "image",
        "runtime-projection" => "runtime-projection",
        "receipt-format" => "receipt-format",
        "object-identity" => "object-identity",
        "ownership" => "ownership",
        "reserved-location" => "reserved-location",
        "selector-drift" => "selector-drift",
        "uid-conflict" => "uid-conflict",
        "container-identity" => "container-identity",
        "container-drift" => "container-drift",
        "resource-shape" => "resource-shape",
        "resource-drift" => "resource-drift",
        "scalar-drift" => "scalar-drift",
        "strategy-drift" => "strategy-drift",
        "node-selector-drift" => "node-selector-drift",
        "pull-secret-shape" => "pull-secret-shape",
        "pull-secret-identity" => "pull-secret-identity",
        "pull-secret-drift" => "pull-secret-drift",
        "runtime-account-drift" => "runtime-account-drift",
        "runtime-list-shape" => "runtime-list-shape",
        "runtime-list-identity" => "runtime-list-identity",
        "runtime-bootstrap-drift" => "runtime-bootstrap-drift",
        "runtime-volume-drift" => "runtime-volume-drift",
        "runtime-mount-drift" => "runtime-mount-drift",
        "runtime-mount-collision" => "runtime-mount-collision",
        "runtime-token-injection" => "runtime-token-injection",
        "ports-shape" => "ports-shape",
        "port-shape" => "port-shape",
        "port-identity" => "port-identity",
        "port-drift" => "port-drift",
        "probe-drift" => "probe-drift",
        "removed-resource-present" => "removed-resource-present",
        "removed-port-present" => "removed-port-present",
        "removed-probe-present" => "removed-probe-present",
        "removed-node-selector-present" => "removed-node-selector-present",
        "removed-pull-secret-present" => "removed-pull-secret-present",
        "operation-marker" => "operation-marker",
        _ => "unknown"
    };
    // Scope: Deployment PATCH sends through this UPDATE kernel client only.
    private static string Count(int value) => value is >= 0 and <= 3 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
}
