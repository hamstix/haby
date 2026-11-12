# Evidence-bearing Resource completion

Issue #187 adds the persisted `ResourcesReady` checkpoint for the currently
admitted Resource-only generations. It records why Resource work completed before
advancing `AppliedGeneration`. It does not implement Workload execution,
configuration preparation/publication, submission, readiness, retirement or
corrective generations. Ordinary configuration materialization still requires an
eligible fully applied generation and its existing current-use checks.

## Saved identity and evidence

`generation_stage_checkpoints` has one key per exact deployment, generation and
stage, an independent UUIDv7, format version, accepted input fingerprint, UTC
confirmation time, bounded evidence document and its SHA-256 fingerprint. The
current stage and evidence format are `ResourcesReady` and **1**. Shared columns
contain no Provider-specific state or secret payload. C#/EF owns completion;
there is no new runtime SQL workflow, trigger or database-role requirement.

The detached, source-generated evidence document references each stable Resource
management record, its **original** source generation and accepted fingerprint,
and its original operations, exact attempts and output contract/key/fingerprints.
Generated credentials additionally reference the original SecretId/VersionId.
It copies neither output payloads nor credential material. Retained Resources
remain explicit retained obligations, outside the target's active coverage.
Reintroduction preserves the original source rather than the immediately previous
generation. The existing adapter policy and pair-closure checks remain required.

Coverage is checked against the frozen declaration, exact Provider input set and
accepted graph. Every actual journal node must match; every mandatory Resource
must have creation or direct reuse evidence. Terminal state counts alone are
insufficient. The accepted graph is now format **3**, including the adapter's
mandatory-output requirement and exact producer/contract/key, so missing registration cannot change an accepted
producer into a no-output action. The typed registered planner supplies that
obligation, cross-checked against exact action and output registration metadata
while planning. It is never recomputed as false after registration disappears.
Marker presence alone cannot satisfy readiness: the exact validator, accepted
context, original producer/attempt and required output must all agree. Durable input format **4** and the `v4:` Plan
fingerprint retain their meaning and include the current graph bytes.

The document limit is 2 MiB, with at most 512 combined Resource identities (up to
256 creation-node Resources plus up to 256 reuse/retention requirements), preserving
the existing 256-node graph and 256-declared-Resource bounds. Exact operation/output identities retain their existing
128-character bounds. Unsupported, corrupt or conflicting stored proof refuses
without overwrite or automatic repair. EF writes reject checkpoint updates and
deletion. Privileged direct catalog mutation remains within the existing
administrator trust boundary; readers check identity, document hash, exact
generation bindings and original result references before using a checkpoint.

## Completion and transactions

All durable completion writers use `DurableAggregateCompletion`. They take the
deployment row lock before updating the terminal operation. Attempt closure and
mandatory output publication precede proof calculation. Eligible checkpoint
creation and Applied advancement use the caller's transaction and one conditional
completion boundary. Concurrent completion/replay returns the original record;
it does not issue another attempt or repeat a confirmed effect.

Confirmed operation/output completion is not rolled back merely because the
aggregate lacks sufficient readiness evidence. Mixed generations preserve new
confirmed effects while unavailable reused-peer evidence blocks aggregate
completion. The runner can retry the completion boundary after that evidence is
available, without redispatching successful nodes. A savepoint starts after the
confirmed operation/output/attempt writes and encloses all checkpoint and Applied
changes. Recoverable server-reported database errors roll back that aggregate
boundary and detach the staged receipt before committing the confirmed result.
Connection, cancellation, savepoint and commit failures retain the existing
uncertain-write barriers; no transaction failure is assumed to have committed.

Recovery inserts the real accepted decision before calculating the proof, in the
same transaction as operation completion, checkpoint and Applied advancement. Its
proof references the decision and original **Uncertain** attempt. It neither
changes that attempt to success nor requires an output deliberately absent from
accepted standalone database recovery. Existing pair/restrict eligibility
restrictions remain unchanged.

Adapter reuse validation receives the transaction's exact reservation snapshot
through a synchronous metadata-only contract. It cannot rent another connection
through that seam. Generated producers check dependencies before material
resolution and recheck them under the lock. Material validation precedes the lock;
inside the boundary only exact catalog metadata is checked. There is no Provider
or network I/O or SecretStore resolution under that lock.

Historical checkpoint reads do not require continued secret-material availability.
They are evidence of completion at the saved time, not a promise of indefinite
external-object or secret availability. Existing reuse, rendering and delivery
paths retain their current-use material/ownership checks.

## Writer and consumer inventory

| Path | Completion or eligibility rule |
| --- | --- |
| Ordinary journal success | Close exact dispatched attempt, then shared completion |
| External output completion | Publish exact output and close attempt, then shared completion |
| Generated credential completion | Validate material before lock; publish reference and close local attempt, then shared completion |
| Standalone manual recovery | Save exact accepted decision, then shared completion; atomic commit |
| Runner, all-reuse and mixed completion | `TryCompleteGenerationAsync` uses the same boundary |
| Progress and generated Host success | Read actual proof; bare Applied cannot report generation success |
| CLI success | Require the persisted checkpoint summary as well as existing node/Applied conditions |
| Plan/Check/Commit baseline | Require the prior generation's checked checkpoint in addition to existing fully-applied rules |
| Continuity and original-source selection | Require checked original checkpoint and existing original operation/output/credential evidence |
| Configuration materialization/read selection | Keep ordinary fully-applied checks and require checked proof |
| In-memory checkpoint executor/store | Test-only pre-durable pilot; not registered as a durable completion writer |

The only durable `AppliedGeneration` update is in the shared completion boundary.
Recovery's audit field records the actual transaction outcome; it is not a second
deployment pointer. Desired-state acceptance still initializes Applied to null
and cannot pre-advance it.

## Operator observation and empty proof

Existing generation get/execute responses include the actual persisted checkpoint
ID, format, accepted input fingerprint, confirmation time and safe counts of
active/retained Resources and original results. CLI text/JSON show the same
summary. `ResourcesReady` does not claim Workload readiness. No stage, wait or
resume commands are added; the CLI remains API-only and Native AOT.

The pure coverage policy permits an empty Resource proof only for zero declared
Resources, zero Resource operations and zero reuse requirements. This is a pure
preparatory rule. Empty executable graphs remain refused, and the central
completion boundary explicitly refuses declarations containing any Workload.
There is no dummy Resource, synthetic successful operation or Workload-only/mixed
production admission.

## Current-alpha catalog boundary

`PersistResourcesReadyCheckpoints` is migration 30. Startup and the migration
itself refuse earlier catalogs with any durable Plan or generation before schema
changes. The migration locks both history tables, so a concurrent old-format
writer cannot pass between the check and DDL. Empty durable history can upgrade;
an explicit downgrade is also limited to empty history. No checkpoint is
backfilled from Applied or inferred terminal counts.

Under ADR-033, incompatible history requires an explicitly initialized fresh
catalog. Preserve needed backups separately. Haby never resets a catalog
automatically. External databases, roles and data survive catalog replacement;
a new catalog grants no authority to recreate or adopt them. Current-build
restart/retry and dump/restore preserve saved proof and original identities.

## Acceptance evidence

| Requirement | Production boundary and focused evidence |
| --- | --- |
| Exact install and immutable replay | `ResourcesReadyCheckpointTests`: independent persisted management/attempt/checkpoint queries, missing-adapter restart, concurrent equivalent replay |
| No terminal-count or missing-output shortcut | Focused pre-fix failing tests for succeeded rows without confirmed attempts and missing output registration |
| Reuse/mixed/retained/reintroduced sources | Existing synthetic and PostgreSQL lifecycle tests with added direct proof/source/reference assertions |
| Configuration-only generations | Existing frozen-default and generated Host update paths; explicit execution and original Resource evidence |
| Recovery authority | Existing actual Host recovery races/restart/restore; proof references actual decision and unchanged uncertain attempt without fabricated output |
| Corrupt proof | Persisted conflict refusal, no rewrite/repair/effect replay |
| Empty proof and closed Workload admission | Pure zero/nonzero/mismatch checks plus existing Workload planning refusals; empty operation graph remains unsupported |
| Operator surfaces | Existing generated Host, CLI loopbacks and published-binary gates with persisted summary assertions |
| Catalog safety | Fresh migration, empty upgrade, older nonempty refusal, current-build restart and dump/restore |

Per-SHA commands, counts, original failures, safe hashes and NOT RUN gates belong
in task-owned evidence outside tracked product source. Execute all applicable
[local validation gates](local-validation.md); historical results are not current
evidence. Independent exact-SHA review and architect acceptance precede any
coordinator merge. This document provides no merge authorization.
