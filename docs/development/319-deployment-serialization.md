# Deployment serialization foundation for corrective runtime

This document records the prerequisite slice. Its closed public guards are superseded
by the coupled [corrective runtime integration](319-corrective-runtime.md); its original
validation statements describe that slice only.

This first internal #319 slice changes the existing single-Host
`DurableOperationSerialization` gate from generation identity to Deployment
identity. Existing `RunAsync(DurableOperationKey, ...)` callers keep their exact
operation and generation inputs, but two generations of the same Deployment
cannot enter their consequential callbacks together. Different Deployments remain
independent. `RunDeploymentAsync` uses the same dictionary and semaphore for an
explicit Deployment transition; it is not a second gate or an execution capability.

## Ownership and lock order

Acquire the singleton Deployment gate before starting catalog transactions or
performing external calls. A top-level owner retains it through its existing
bounded operation/submission/observation call and releases it in `finally`.
Waiter cancellation cannot enter the callback or release the active owner's gate.
Callback exceptions release only the acquired gate. Existing transaction and
current-authority checks remain mandatory.

The gate is intentionally non-reentrant. A caller holding it invokes existing
`CoreAsync`/`UnderLockAsync` methods rather than another `RunAsync` wrapper.
Current generation runner -> exact executor core -> registered adapter under-lock
path, submission -> preparation/action core path, and rollout -> completion core
path follow this ownership rule. Direct adapter execution acquires the same gate.
Journal-only operation observation remains outside it and remains available while
an external dispatch is in flight. Do not acquire this gate inside a catalog
transaction or invert the gate-before-catalog ordering in future Commit wiring.

## Deliberately closed boundary

This is serialization infrastructure in the existing execution mechanism, not a
complete ADR-052 fence, durable supersession or accepting Commit. The current
corrective `Commit` remains `UnsupportedShape`; ordinary public UPDATE guards are
unchanged. No capability switch, accepting test-only algorithm, new acceptance
receipt or alternate workflow is introduced.

Direct configuration/publication preparation and durable authority transitions
still need their complete runtime integration under #319. The future accepting
Commit must acquire this same Deployment boundary, revalidate its exact frozen
inventory/current authority under catalog locks, and atomically revoke the old
generation's effect authority. Successful public acceptance must remain inseparable
from executable authenticated continuation, mixed confirmed/pending member handling,
readiness, accumulated publication retirement and Applied completion. A semaphore
alone does not prove these obligations or restart safety.

#319 stays open. #320 owns API-only CLI and final operator evidence; #321 remains
the mandatory broader ADR-052 settings/configuration follow-up. This foundation
claims neither operational correction nor full ADR-052/P3 completion.

## Focused evidence

Three deterministic fast cases cover cross-generation exclusion, cancelled-waiter
isolation with an independent Deployment, and the explicit Deployment entry sharing
the operation gate plus exception release. Entry/release signals decide ordering;
10-second cancellation budgets only prevent a hung test. Two original cases fail
against the prior generation-key implementation and pass with the Deployment gate.
The existing durable executor tests retain same-generation and journal-observation
coverage. Exact-source full qualification is reported separately.
