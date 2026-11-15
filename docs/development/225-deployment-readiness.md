# Internal exact Deployment readiness

Issue #225 adds friend-only server observation and audited expired-attempt resume
after a genuinely confirmed initial Deployment CREATE. It does not register
Host/RPC/CLI wait or batch commands, admit public Workload execution, implement
UPDATE/retirement or advance AppliedGeneration. Resource-only completion is unchanged.

## Authority and saved evidence

`InternalDurableActions.ObserveRolloutAsync` accepts an exact accepted Commit and
idempotency request. It reconstructs the complete target set from the saved manifest
and Provider input set. Each submitted member requires the original intent, exact
handler/configuration/Placement, Succeeded operation, dispatched attempt, canonical
receipt output and original response or confirmed observation. Configured members
also require the exact `OwnedObjects` origin. Unconfigured CREATE has no Deployment
inventory row in the current adapter; its original action/output remains mandatory.
Runtime-account success, arbitrary IDs, caller-built receipts and a confirmed prefix
cannot establish complete submission. Unresolved writes stay in their own recovery path.

The separate `IInternalReadinessHandler` performs no execute/preparation/publication.
The Kubernetes implementation sends one GET of the pinned Deployment; it does not
read current aliases, resolve application secrets, GET the ServiceAccount or perform
TokenReview. Pinned administrative credentials are used only for the API transport.
Original publication proof remains distinct from current permission authorization.
Revoking permission neither erases a truthful rollout observation nor republishes access.

Two shared neutral tables retain observation attempt authority and request/read/expiry
records. Attempt authority is immutable. A read/request reservation closes once through
a guarded C#/EF transition; completed records never overwrite history. No Kubernetes
columns, server workflow functions, scheduler or network-spanning transaction is added.
Deployment locks protect mutations; the existing generation gate serializes local calls.
Database conditions also protect independent compositions and concurrent resumes.

The final submission transaction saves attempt authorities and an evidence-bearing
`WorkloadsSubmitted` checkpoint only when every accepted Workload is confirmed.
The last Ready read closure, complete `RolloutReady` proof and request response commit
atomically. Distinct source-generated proof codecs store the exact target set, receipt
hashes, dispatch/confirmation origins, observation attempts and read-record references.
Partial results never create a complete-set checkpoint. A complete fresh round is
required; arbitrary Ready samples from old calls/rounds cannot be assembled into success.

## Adapter-owned apps/v1 completion criteria

The adapter reuses full ownership, selector, operation marker and managed projection
validation, including configured bootstrap, service-account name, automount, token
projection and keyed mounts. A foreign UID, missing object or managed drift is Conflict.
Unrelated metadata and permitted injected fields remain outside that ownership.

| Evidence | Rule |
| --- | --- |
| Live generation | Positive integer at least the confirmed receipt generation. |
| Controller generation | Present, positive and at least both confirmed and live generation; cannot exceed live generation. Missing is incomplete, malformed is invalid. |
| Replica counts | Missing/null optional counts normalize to zero. Present counts must be nonnegative Int32 integers; strings, fractions and overflow refuse Ready. |
| Consistency | Updated and ready cannot exceed total; available cannot exceed ready; explicit unavailable must equal max(desired minus available, zero) and be no greater than desired. Never clamp contradictions. |
| Completion | Total, updated, ready and available must all equal frozen desired replicas, with zero unavailable. Surge, old active remainder, missing replicas and condition-only success are incomplete. |
| minReadySeconds | Actual controller availableReplicas is required; readyReplicas and local elapsed-time guesses do not substitute. |
| Conditions | Absent conditions are not invented. Available/Progressing False or Unknown, or ReplicaFailure True/Unknown veto Ready. Duplicate types, malformed lists/statuses refuse Ready. Unknown condition types carry no success authority. |
| ProgressDeadlineExceeded | Safe fixed diagnostic and incomplete observation; no rollback or permanent prohibition on later coherent recovery. |
| Paused/deleting | Never Ready. Missing paused means false; malformed values refuse Ready. |
| Terminating count | Optional nonnegative evidence; RolloutReady is not proof old Pods terminated or retirement completed. |

Evidence is closed version 1, bounded to 16 KiB per read. Conditions contribute only
finite flags; raw reasons/messages, SDK errors and response bodies are never saved or
returned. Safe result states separate Ready/Pending/Conflict/Unavailable/Refused from
Active/Expired budget and the reason this call stopped. Missing confirmed submission
and quota-limited members appear explicitly as NotSubmitted and NotObserved.

404 is missing-object conflict; 401/403 are non-retried access unavailability. Transport,
timeout, 429 and 5xx permit bounded read retries. Malformed status never authorizes a
write or success. Failed reads never become uncertain/failed external write attempts.

## Deadlines, replay and resume

Each Workload has its own observation budget. Attempt 1 starts at genuine durable
operation `FinalizedAt` (confirmation time, including confirmation by observation),
plus the frozen effective rolloutSeconds; default 600, admitted 1..86400 seconds.
The current Deployment projection also uses this setting for progressDeadlineSeconds.
There is no additional aggregate rollout deadline. New confirmations save attempt 1
in the confirmation transaction. Existing v2 confirmations initialize deterministically
from the original FinalizedAt, even when that deadline is already expired.

Persisted times use authoritative UTC at PostgreSQL microsecond precision. Request
elapsed time is monotonic; per-read bounds also consume the remaining persisted
budget without refreshing time through polling. Request cancellation, quota exhaustion
and Host restart do not renew the attempt. Expiry is at or after deadline and appends
history before a recovery read; an eventual Ready result retains that expiry.
Once recorded, expiry remains authoritative if UTC subsequently moves backward;
it is reread under the catalog lock and latched for the rest of the same call.
The call consumes its one recovery allowance when that read is reserved, so a
later round cannot regain active polling or repeat recovery after clock reversal.
Only explicit resume creates a new attempt. Read timers start before catalog
reservation and are checked again before API work, so a lock wait cannot refresh
the active rollout allowance. A reservation that exhausted its allowance closes
without sending GET and still consumes its finite call slot. A monotonic allowance
exhausted before authoritative UTC expiry stops that member without inventing a
new recovery-read permission or changing the saved deadline.

After expiry, one fresh read per eligible member per explicit request can recognize
actual recovery. There is no retry, polling delay or renewed rollout deadline for
that member. A later explicit request may read again. A duplicate idempotency request
never repeats network work, including when its prior reservation was interrupted.
`Replayed=true` explicitly identifies historical response replay; it is not a fresh
read. `SavedReady` is an immutable historical checkpoint. A fresh conflicting read
returns current incomplete evidence even when that historical checkpoint exists.

`ResumeRolloutAsync` requires exact generation, explicitly selected member/expected
attempt IDs, bounded actor and request identity. Only the current expired attempt
can receive one successor with the same frozen budget and explicit audit. Same request
replays the same successor; changed inputs or a different stale concurrent request
conflict. Stale in-flight results retain their original attempt and cannot create a
checkpoint for the renewed set. Resume makes no API call and executes no unsent work.
Permission, business correctness and old-recipient termination remain separate concerns.

## Internal call bounds

| Option | Default | Range |
| --- | ---: | --- |
| MaximumCalls | 64 | 1..512 |
| MaximumRounds | 32 | 1..512 |
| ReadTimeoutMilliseconds | 10000 | 100..10000 |
| PollMilliseconds | 1000 | 100..10000 |
| RequestMilliseconds | 30000 | 1..300000 |

These are validated internal composition options, not new public Host settings.
Reads are sequential. GET timeout is bounded by remaining request and active rollout
time. A finite 10-second catalog-only closure may retain the canceled result; it
grants no additional API observation time. The target set is capped at 512, consistent
with the existing preparation item bound, and response/checkpoint evidence at 2 MiB.
For N targets a full round needs at least N remaining calls and enough request time;
lower quotas return truthful partial results. Explicit calls rotate their starting
member. Every Ready member must participate in the successful fresh round. No hidden
background worker, unbounded queue, retry of a write or automatic resume is added.

## Compatibility and verification

The additive migration creates the two observation tables and expands the existing
checkpoint stage constraint. Intent/receipt v2 and Placement formats stay current;
no legacy v1 reader, reset, backfill-to-Now or Host-wide refusal is introduced.
Current-build restart and catalog backup/restore must preserve identity and history.

Fast tests cover count/generation/condition/managed-state boundaries, codecs, safe
transport outcomes and finite options. Real PostgreSQL tests exercise genuine accepted
generations, partial sets, checkpoint atomicity, cancellation, resume races, stale
results, expiry/recovery, revocation independence and current-build restore using
separate arrange/act/assert contexts. Their catalog read double is not real API evidence.

The existing configured Deployment qualification adds R01-R04: controller-produced
Ready with nonzero minReadySeconds, reconstructed observer retaining identity/deadline,
actual startup refusal/current incomplete observation despite historical Ready, and
audited observation-only resume. Existing C01-C11 and owned cleanup remain mandatory.
Only expiry arithmetic in this real scenario uses the fixture clock; positive Ready
comes from actual controller status, never a status patch or substituted Deployment.
The existing public GHCR consumer binding key f362ced2/digest 980a5434 is reused; no
consumer/client/contracts inputs or image build/publication change is needed.

Run existing solution, full PostgreSQL, API/kernel/durable/configured, dependency,
security, both published Native AOT, format and diff gates. Inventory is not passing
execution evidence. Record exact source, failures and unperformed gates separately;
no unchanged green-seeking rerun or readiness/business-health/retirement conflation.
