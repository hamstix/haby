# ADR-052: Explicit corrective generations for incomplete Workload rollout

- Status: Accepted
- Date: 2026-11-11
- Scope: Sixth P3 Workload decision packet; design acceptance, not implementation
- Extends: ADR-016/026/032/033/035/041/042/045/046/049/050/051

## Context

External repairs such as fixing a referenced registry Secret can complete the same
accepted generation through wait/resume under ADR-051. A bad accepted image or
configuration requires different desired inputs. ADR-045's ordinary fully-applied
baseline rule must not trap such a rollout permanently, but broadly accepting new
generations over uncertain provisioning would lose ownership and effect history.

Accept a narrow explicit corrective intent after confirmed ResourcesReady and
before retirement. This decision does not implement that path, weaken current P2
admission or authorize generic cancellation, rollback or manifest replacement.

## Decision

### 1. External repair and desired change differ

External repair with unchanged accepted inputs uses wait/resume of the SAME
generation. A different image, manifest or exact Profile/configuration selection
requires a new immutable corrective generation. Resume never edits accepted inputs.

### 2. Ordinary updates retain fully-applied baseline

Normal ChangeSet/Plan/Commit still requires a fully applied current baseline.
Corrective supersession is a separate explicit immutable intent with the exact
expected incomplete desired generation. Never fall back to it automatically when
an ordinary update fails. Concrete wire/CLI syntax must distinguish supersession
from replacing an ApplicationId + ApplicationVersion or editing ManifestRevision.

### 3. Admission only after ResourcesReady and before retirement

Require all of the following:

- The Resource stage is confirmed complete with its exact original evidence.
- No external write has an unresolved uncertain outcome.
- Managed-object identity/ownership remains provable.
- Retirement has not started.
- The old server executor can be safely stopped at a confirmed action boundary.

Failed/uncertain database or login creation is not made recoverable through this
exception. Resolve uncertainty through its accepted action policy first; do not
relabel a marker or missing output as successful ResourcesReady.

### 4. Bounded Workload/configuration changes

Initially permit image, supported existing-Workload settings and exact configuration
inputs/document selection to change. Preserve the Resource set and properties,
exact Provider/naming inputs and logical Workload identity set of the CURRENT
accepted generation being corrected. Resource/Workload additions, removal, rename,
relocation, rotation or Provider rebinding are not admitted by this special intent.

This is not a comparison solely against the last applied manifest. Any supported
Resources added and confirmed by the incomplete generation remain in its successor.
Ordinary significant-input/no-op rules continue; request/Plan IDs alone do not
justify another generation when observation of the same desired state suffices.

### 5. Supersede intent, preserve history

```text
generation 1: Applied
generation 2: image/config rollout incomplete
generation 3: explicitly accepted correction
```

Keep generation 2 and its immutable ManifestRevision, Plan, operations, outputs,
documents and actual observations. Record supersession and SupersededBy=3 without
rewriting old results or pretending generation 2 completed. Generation 3 is a new
accepted desired state, not a replacement import or mutation of generation 2.

### 6. Plan from confirmed incomplete-generation inventory

Capture the exact incomplete baseline and its confirmed state, not just the last
AppliedGeneration. It may already have created Resources, published documents,
updated some Deployments and created companion objects. Record those results and
remaining obligations explicitly in the corrective Plan; never drop them because
readiness failed. Missing/inconsistent inventory/evidence refuses safe planning.

### 7. Reuse confirmed Resource-stage evidence directly

If a Resource was successfully created in generation 2 before rollout failed,
generation 3 reuses its ORIGINAL producer/evidence from generation 2. Keep stable
management, names/reservations, slots/SecretRefs, credentials and confirmed outputs.
No repeated CREATE, password generation, privilege replay, copied outputs, fake
successful attempts or fabricated applied checkpoint for generation 2.

Implement a separately checked path accepting confirmed ResourcesReady provenance.
Do not globally remove AppliedGeneration eligibility checks from ordinary operator
materialization, delivery or unrelated reuse. Validate exact frozen inputs,
owner/purpose, source and consumer authority under the corrective contract.

### 8. Atomic corrective Commit

Commit verifies the exact current desired generation, corrective intent/Plan
freshness, complete Resource checkpoint and lack of uncertain/in-flight work.
Atomically accept the next desired generation, record supersession/linkage and
remove the old intent's permission to execute further effects. Concurrent corrective
commits have one winner. Preserve immutable receipt replay and existing stale
ChangeSet versus stale Plan distinctions; never silently replan.

### 9. Fence the old executor before new writes

The server verifies generation execution authority before every new external action.
Corrective Commit cannot race an unfinished old dispatch: serialize the handoff at
a safe action boundary and require unresolved writes to be observed/resolved first.
For the initial single Host, coordinated serialization and durable authority checks
suffice; no HA/worker-lease framework is introduced merely for this path.

After supersession, old Execute/resume returns the recorded superseded state and
does not dispatch writes or advance AppliedGeneration. Read-only history remains
available. Test stale executor instances and restart rather than trusting a CLI flag
or cancellation of an HTTP request to fence the previous server workflow.

### 10. Ownership/conflict checks remain effective

Correct the same confirmed Kubernetes UIDs under ADR-049. Foreign objects, unexpected
UIDs and manual managed-field edits remain conflicts. Corrective mode grants no
force override, adoption, missing-object recreation or delete-and-create rename.

### 11. Stable runtime identity and explicit publication

Keep the Workload ServiceAccount and RuntimeIdentityBinding under ADR-050. Prepare
and explicitly publish new exact documents for the corrective generation. Preserve
old publications while needed by running recipients. Replaying an old workflow
never restores revoked access. Static P2 grants retain their independent lifecycle.

### 12. Carry complete retirement obligations

The corrective Plan inherits objects and publications left by the incomplete
rollout, not only differences from the last successful generation. Preserve the
whole confirmed inventory and references through successive corrective attempts.
Delete only owned obsolete objects after safe readiness/recipient boundaries;
retain external Resources and do not incidentally revoke static grants.
Supersession cannot forget effects or erase unfinished cleanup obligations.

### 13. AppliedGeneration may skip superseded generations

After generation 3 completes its mandatory stages:

```text
DesiredGeneration = 3
AppliedGeneration = 3
generation 2 = Superseded, never Applied
```

AppliedGeneration is the latest fully completed generation, not a contiguous count
of successful generations. Previous applied state remains unchanged until the new
one fully completes. Do not fabricate intermediate application to satisfy equality
or monotonicity checks; source-evidence eligibility uses its explicit checkpoints.

### 14. Failed first installation is supported

If generation 1 reached ResourcesReady but its first Workload cannot start, explicit
correction may accept generation 2 under the same prerequisites. AppliedGeneration
stays unset until successful full completion. Preserve and reuse already confirmed
databases/logins/credentials without provisioning them again.

### 15. Selecting an old image is still a new generation

An operator may explicitly choose an earlier image and compatible configuration in
a corrective Plan. This is not rollback of data, schema migrations or external
effects. Keep the current accepted Resource set even if the historical manifest
selected for inspiration did not contain a Resource already created by the failed
rollout. Do not claim whole-world restoration to the older manifest.

### 16. No corrective supersession after retirement starts

Initially an incomplete generation already deleting objects or revoking access
must resume/repair its own retirement under ADR-051. Do not supersede halfway through
destructive cleanup. Wider partial-retirement replacement requires a separate
accepted policy. Neither this rule nor supersession authorizes stripping finalizers,
force cleanup or redefining observed deletion as an unstarted stage.

### 17. Required evidence

On real disposable infrastructure prove bad image -> correction -> completion,
failed first installation, successful Resources originating in the superseded
generation with direct provenance/no replay, partially updated Kubernetes objects,
competing corrective commits, old Execute versus handoff, restart after corrective
acceptance, unresolved write blocking, retirement-start refusal and complete
inventory/access/data preservation through eventual cleanup. Verify exact old/new
document authority and revoked-permission replay behavior.

All orchestration belongs to Haby. CLI sends explicit intent, reviews Plan/results
and never contacts Kubernetes or external resource systems directly.

## Implementation boundaries

Define exact corrective intent/precondition/status/receipt contracts, evidence
eligibility, executor handoff/locking and immutable accumulated inventory in dedicated
implementation slices before enabling the exception. Keep shared persistence,
action state and policy boundaries Provider-neutral; Kubernetes-specific observation
and effect details remain adapter-owned. Use C#/EF transactions, generated JSON
metadata, current-alpha codecs and all existing authentication/PostgreSQL/Native AOT
gates. No stored workflow, broad compatibility scaffolding, silent reset/history
backfill or universal recovery is introduced. Accepted design is not implemented
support; ordinary P2 guards remain unchanged until a deliberate P3 slice adds it.
