namespace Haby.Operator.Contracts;

/// <summary>Closed completion states; historical application is distinct from fresh confirmation.</summary>
public static class GenerationCompletionWire
{
    public static void Validate(bool currentReady, bool replayed, bool currentApplied, bool originalApplied,
        string? retirement, long? applied, string reason)
    {
        if (reason is not ("not-ready" or "applied" or "inventory-conflict" or "prerequisite-incomplete" or
                "generation-superseded" or "current-authority-unavailable" or "unsupported-inventory" or "commit-unconfirmed") ||
            retirement is not null && (!Guid.TryParseExact(retirement, "D", out var id) || id == Guid.Empty) || applied is <= 0 ||
            currentApplied && (!currentReady || replayed || !originalApplied || retirement is null || applied is null || reason != "applied") ||
            originalApplied && (retirement is null || applied is null || reason != "applied"))
            throw new InvalidDataException("Invalid completion result.");
    }
    public static string NextAction(string reason) => reason switch
    {
        "applied" => "Use a new wait request for fresh readiness; replay is historical.",
        "not-ready" => "Inspect unfinished readiness; explicitly renew an expired observation attempt if needed.",
        "inventory-conflict" => "Inspect accepted inventory; nonempty retirement is unsupported by this build.",
        "prerequisite-incomplete" => "Inspect exact prerequisite evidence; completion cannot prepare or repair it.",
        "generation-superseded" => "Inspect the current accepted generation and observation attempt.",
        "current-authority-unavailable" => "Inspect current runtime authority; completion cannot restore revoked access.",
        "unsupported-inventory" => "Use a newly accepted Plan with supported frozen inventory; never backfill history.",
        "commit-unconfirmed" => "Inspect durable request history; do not assume rollback or replay external effects.",
        _ => throw new InvalidDataException("Unknown completion reason.")
    };
}
