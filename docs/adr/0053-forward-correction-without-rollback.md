# ADR-053: Forward correction without a dedicated rollback operation

- Status: Accepted
- Date: 2026-11-11
- Scope: Desired-state correction policy for Application deployment and P3 rollout
- Extends: ADR-015/016/033/045/049/050/051/052

## Context

Haby records immutable manifest revisions, exact Plans, accepted generations and
confirmed external evidence. Restoring a previous declaration would not reverse
database contents, schema migrations, generated credentials, ownership or partial
external effects. Operators can correct a bad target through a newly accepted
desired state, or repair external prerequisites and resume unchanged intent.
A separate historical-state rollback mechanism is not required for the initial
operational path and would imply recovery semantics that are not implemented.

## Decision

### 1. Forward correction is the deployment policy

Haby uses forward correction through explicit desired-state generations. It
provides no automatic or dedicated historical-state rollback. Do not introduce a
rollback command, rollback Plan type, implicit restore of historical snapshots or
failure-triggered return to an old image/configuration.

Keep Import -> ChangeSet -> Plan -> Commit -> Execute and exact preconditions,
freshness, ownership and effect policies. Audit/observation changes and retry are
not new desired generations. Correcting accepted content never overwrites an
existing ApplicationId + ApplicationVersion or changes an immutable Plan.

### 2. Choose correction according to what changed

- A manifest error is corrected in a new ApplicationVersion/ManifestRevision.
- A Profile error uses a new exact Profile revision or an explicit selection of
  a suitable existing revision, accepted through the supported configuration path.
- An external prerequisite error with unchanged desired inputs, such as invalid
  credentials in a referenced registry Secret, is repaired externally and followed
  by wait/resume of the same generation under ADR-051.

All new desired inputs pass normal supported planning/execution or the explicit
bounded corrective intent under ADR-052. A prior failure is not permission for
silent replan, relaxed validation, data reversal or replay of uncertain effects.

### 3. History supports audit and provenance, not inverse execution

Retain revisions, Plans, operation results and source evidence for comparison,
audit and confirmed Resource continuity. Their existence does not mean Haby can
reconstruct every external state from that time or execute inverse operations.
No database downgrade, compensation, password restoration, ownership reversal or
revival of revoked runtime authority is inferred from selecting an older document.
Backup/disaster recovery and action-specific evidence recovery remain separate
accepted capabilities, not this deployment rollback mechanism.

### 4. Historical revisions remain explicitly selectable

ApplicationVersion is not assumed to be an ordered release number. Do not guess
which version is newer from semantic-version parsing, import time, revision number
or a naming convention. Do not reject a revision merely because it was imported
before another selected revision.

An explicit historical selection is a NEW desired-state request evaluated against
CURRENT supported rules and managed state. It may be refused by Resource identity,
retention, naming, Provider, configuration or rollout constraints. It gains no
special deletion, adoption, renaming, password-reset or migration-reversal rights.
A future release-management policy may explicitly model release order and prevent
accidental downgrades; it is not added here or implied by ApplicationVersion.

### 5. An earlier image may be the target of a new correction

Do not require image tags or versions to increase monotonically. A new corrected
manifest/generation may intentionally select a previously working image with
compatible current Resource and configuration inputs. This remains explicit forward
acceptance of new intent; it does not restore the whole historical infrastructure.
Under ADR-052 preserve Resources already confirmed by the failed generation even
when the historical application shape being used as a reference lacked them.

### 6. Keep the bounded incomplete-rollout escape

ADR-052 remains necessary: ordinary updates require a fully applied baseline,
while a bad accepted image may never reach readiness. Its explicit corrective
generation is permitted only after confirmed ResourcesReady, without uncertain
writes, before retirement and with safe executor handoff and unchanged admitted
Resource/Workload identity sets. Preserve direct original evidence and all inventory.

No rollback feature is needed to support this path. Resume cannot change an image;
corrective Commit cannot invent prior completion or authorize blind effect replay.
No new exception to those prerequisites is introduced by this ADR.

## Consequences and required documentation

Operator/program instructions must distinguish external repair/resume, a new
manifest release, explicit configuration-only correction and bounded correction of
an incomplete rollout. Describe historical selection as a request subject to current
rules, not a guarantee of restoration. Explain that AppliedGeneration records a
completed accepted target, not a reversible transaction across external systems.

Implementation slices must prove immutable revision conflict behavior, current
admission rules for historical targets, safe reuse/provenance, failed-rollout
correction, no automatic rollback and no incidental restoration of revoked access.
Earlier image selection must not be mistaken for a bypass of current Resource or
identity validation. Do not add compatibility solely for superseded alpha models.

This documentation decision changes no running API, schema, deployment workflow or
backup behavior. Future work requiring actual inverse external effects needs its
own explicit accepted contract rather than calling historical selection rollback.
