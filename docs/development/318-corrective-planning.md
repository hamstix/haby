# Explicit incomplete-generation corrective planning

Issue [#318](https://github.com/hamstix/haby/issues/318) implements the contracts and
catalog capture portion of [#317](https://github.com/hamstix/haby/issues/317).
It does not accept a corrective desired generation or execute any effect.
[ADR-052](../adr/0052-corrective-generations-for-incomplete-workload-rollout.md) and
[ADR-058](../adr/0058-bounded-deployment-image-updates.md) remain the authority.

## Explicit request and immutable retry

The authenticated operator-admin generated RPC
`CreateCorrectiveChangeSet(CreateCorrectiveChangeSetRequest)` returns `PlanningReply`.
It requires a logical `environment_id`, exact application/revision reference,
`deployment_id`, positive `expected_incomplete_desired_generation`, and a nonempty
UUID `request_id`. There is no latest/current fallback, expected-absence mode,
Resource import correction reuse, implicit ordinary-update fallback or new credential.
The deployment must belong to that Environment/Application and currently be incomplete.
A repeated request UUID with identical identities/target returns the original immutable
ChangeSet and its original actor/time. A conflicting use returns `InvalidRequest`.
This retry does not grant execution permission or repeat a desired acceptance.

`CreatePlan`, `CheckPlan`, `GetPlan` and `ClosePlan` retain their separate meanings.
Planning captures the exact incomplete generation and optional last Applied, rather
than substituting a successful baseline. The ChangeSet exposes explicit correction
intent. The Plan includes the original Plan/ChangeSet, ResourcesReady checkpoint and
fingerprint, old/new image, each confirmed-owned or positively never-confirmed initial
member, confirmed external identity when present, original operation/attempt pointers,
selected document obligations and every inherited publication status. Generic frozen
inventory retains original owned objects, current object projections, bindings,
all prepared-generation document associations and prior inventory fingerprints.
No successful receipt is invented for an unconfirmed member. Read-only catalog capture
cannot determine whether a formerly confirmed external UID has disappeared; it retains
that UID as confirmed identity and never changes the member into a CREATE candidate.

## Bounded capture and refusal

Only image/ApplicationVersion input may differ. The adapter reuses the existing
image-only comparator to preserve the full Resource/Component/Workload identity set,
Resource properties, exact Providers, naming/Placement/Profile/configuration,
renderer/default pins and exact secrets. Current Provider/Resource metadata and
source/consumer authority remain required. Original ResourcesReady is read and
validated directly; Resource reuse points at its original producer generation and
management/evidence. No copied output, fake attempt or Applied checkpoint is created.

Unknown or inconsistent input/evidence, no-op desired input, unresolved or in-flight
dispatch, retirement-start/completion, revoked identity, changed nonimage input and
unproven member history refuse planning. Stale expected desired identity refuses
intake. A current Plan is only a snapshot: progress, publication/revocation or selected
input changes cause CheckPlan to refuse/stale it without rewriting it. Capture uses
bounded queries and a source-generated, detached inventory capped at 1 MiB; it acquires
the existing catalog deployment synchronization and shared aggregate row lock to
coordinate publication writers. This is NOT the deployment-wide executor handoff.

## Closed execution boundary and remaining sequence

All Commit entry points refuse corrective ChangeSets as `UnsupportedShape`. Existing
ordinary UPDATE admission retains its Applied=Desired and public runtime guard.
There is no alternate accepting Commit, test-only acceptance algorithm, production
capability switch, generic registry or temporary execution-unavailable framework.
Old generation authority is unchanged by creating or checking a Plan.

[#319](https://github.com/hamstix/haby/issues/319) owns inseparable atomic accepting
Commit, deployment-wide fencing of every consequential transition and complete
supported authenticated continuation through configuration/publication, exact safe
member mutation, target readiness, recipient retirement and Applied. It must prove
never-confirmed pending initial CREATE versus missing/replaced formerly confirmed UID
refusal, races/restart/uncertainty and successive accumulated obligations.
[#320](https://github.com/hamstix/haby/issues/320) owns API-only CLI wiring and final
real operator lifecycle evidence. Existing ordinary UPDATE may open when its own
complete path qualifies, without waiting for unrelated broader configuration changes.
[#321](https://github.com/hamstix/haby/issues/321) explicitly tracks the accepted
mandatory broader ADR-052 settings/exact configuration deltas. Image-only completion
will not mean full ADR-052 or P3 completion. #275 remains open, #301 deferred and #263 frozen.

## Catalog and evidence limits

The migration adds one nullable Provider-neutral `corrective_intent_json` column to
immutable ChangeSets; existing records remain ordinary, without reset or backfill.
The optional current graph envelope contains the typed corrective inventory. No
Provider-specific shared column or table is introduced. Current-build persistence,
request replay and Plan reload are covered separately from external runtime.

Focused adapter tests prove comparison without a fabricated receipt. Real PostgreSQL
fixture tests cover failed first installation with Applied unset, mixed member
progress, direct Resource proof/reuse, immutable request and Plan reload, closed
Commit and stale publication observations. Refusal fixtures include no-op/nonimage,
missing proof, uncertain/preparing and retirement-start states. These catalog tests
use simulated external Workload receipts; they do not prove real correction effects,
cluster readiness, executor fencing or operational CLI. Existing mandatory hosted
checks remain required. No load/new infrastructure campaign or historical failure
reclassification is included.
