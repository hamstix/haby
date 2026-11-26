# Composable Resource transitions: implementation and review tasks

Authority: [ADR-064](../adr/0064-composable-resource-acquisition-transitions.md),
ADRs 033/035/061/062/063 and current CONTRIBUTING. Queue: #292 -> [A / #303](https://github.com/hamstix/haby/issues/303) -> [B / #304](https://github.com/hamstix/haby/issues/304) -> [C / #305](https://github.com/hamstix/haby/issues/305).
All three are required adoption work. Reference retirement #301 is independent.

## Shared coding instructions

Start from current main after the preceding slice. Inventory the existing public
intake, ReuseGraphAsync, ResourceContinuity, resource addition policies, protected
import correction and aggregate completion. Consolidate their transition analysis;
do not add a PostgreSQL-plus-Reference special case or parallel planner. Match
stable identities and resolve capabilities per Resource. Preserve supported paths,
original acquisition authority and typed unsupported outcomes for excluded changes.

Keep C#/EF workflows, generic persistence, generated JSON, CRLF and API-only CLI.
Any necessary migration follows current alpha policy without automatic resets.
Do not fabricate CREATE evidence for import/reference or leak protected material.
For a regression add a focused failing test first. Use fast checks plus relevant
real PostgreSQL and smoke under ADR-061; preserve applicable public/AOT evidence.
Full PostgreSQL regression is nightly and full qualification is required for release.
Report exact selection, SHA, results, skipped/unperformed checks and limitations.
Return a scoped draft PR with operator examples, acceptance mapping and review notes;
no autonomous merge, remote-settings changes or extra runtime campaigns.

## A: transition classification and additive Reference

Goal: fully Applied imported state -> unchanged Resource reuse plus new Reference
from another Provider -> authorized combined configuration, without Workloads.
Implement common change classification, exact per-Resource dispatch, server-derived
retained inputs and explicit new Reference inputs. Keep new Import admission outside
this slice without prejudicing B's intake design. Validate target coverage and
configuration dependencies; preserve expected-generation CAS and complete aggregate.

Acceptance: public PostgreSQL import baseline followed by a new revision with a
synthetic typed Reference; combined authorized document, unchanged PostgreSQL
identities/credentials/import authority. Cover refusal/missing capability, imported
state drift, baseline race, retry/restart and no early new-generation delivery.
Changed/removed existing Resources and Provider/credential replacement get explicit
typed refusals. No test-only access to bypass the representative public pipeline.

## B: additive Import with protected generic intake

Depends on A. Add new ImportUseAndRetain Resources to the Applied mixed target using
capability-declared typed credential slots and the real protected intake. Existing
Resources do not resubmit credentials. Implement applicable coupled-group acceptance
and staged correction at the existing atomic acceptance boundary. Support baseline
absence in the intake design; C completes initial mixed public admission.

Acceptance: public result of A -> additional synthetic imported Resource -> combined
configuration. Exercise genuine protected credential intake, owner/purpose refusal,
wrong Environment/Resource/request references, original authority preservation,
exact retry/conflict, partial-group failure, correction race and restart. Confirm
no external CREATE/repair or credential substitution and no premature delivery.
Synthetic provider tests must not seed accepted credentials/associations directly.

### Protected additive Import operator contract

Use `StageResourceImport` before `CreateChangeSet`. Staging uses the existing
`CreateResourceImportChangeSetRequest` envelope with an exact request UUID,
Environment record UUID, target revision, adapter-owned names input, declared
`credential_slot` and separate `credential_material`. A slot declares the existing
Resource-owned `ApplicationLogin` purpose. The adapter prepares the complete coupled
new group and its designated credential owner; callers cannot select an arbitrary
Environment secret. A `Pending` reply is private staging only, with no desired-state
acceptance, confirmed credential, Resource association or configuration delivery.
Repeated staging compares the exact request binding and exact material bytes.

For example, the closed verification adapter accepts `sample.import-names/v1`:

```json
{"Resource":"archive","Identity":"sample-service","Companion":"archive-proof"}
```

It declares `application-password`; this synthetic adapter performs read-only
verification and is not a production external service integration. The optional
companion demonstrates a coupled all-member acceptance group. The protected material
is sent separately, never included in the names JSON or acquisition declarations.

Create the new ChangeSet with the current Applied expected generation and one
acquisition declaration for every NEW Resource, including every coupled member:

```json
{"environment_id":"pilot","reference":{"application_record_id":"<application UUID>","revision_number":3},
 "precondition":"PLANNING_PRECONDITION_EXPECTED_GENERATION","expected_generation":2,
 "acquisitions":{"resources":[
   {"resource":"archive","mode":"ACQUISITION_MODE_IMPORT_USE_AND_RETAIN","import_request_id":"<protected request UUID>"},
   {"resource":"archive-proof","mode":"ACQUISITION_MODE_IMPORT_USE_AND_RETAIN","import_request_id":"<same protected request UUID>"}]}}
```

Other new groups use distinct protected requests in that same ChangeSet. Existing
Resources have no new acquisition declarations and never resubmit passwords. Import
members must omit `input` and `sharing_request`; the server resolves their exact
prepared inputs and protected references and validates complete new/combined coverage.
Missing, extra, duplicate, foreign-owner, wrong-revision or conflicting request
membership is refused before linkage. Exact creation replay returns the original
ChangeSet. Plan/Check/Commit revalidate the complete bound request set and current
baseline. Execute verifies each coupled group atomically; completed independent
groups remain durable, while the new generation cannot be delivered until all groups
and reused Resources are confirmed. Original Resource authority and credentials remain
unchanged. Initial mixed admission is still C, and Workloads/UPDATE/removal are excluded.

A pre-Commit staged correction names the predecessor request and explicit expected
absence of its committed attempt using `ResourceImportCorrection`. Creating/staging
or planning the successor does not transfer credential ownership. Accepting its
ChangeSet transfers all eligible new-group credential owners and records each exact
supersession inside the existing Commit/CAS transaction. One invalid group or a losing
concurrent successor rolls back the entire acceptance. A committed attempt uses durable
retry; this path does not replace already confirmed inputs or retained Resources.

### Catalog cardinality

`ComposableProtectedImportRequests` changes only the two ChangeSet indexes on
`operator_resource_import_requests` and `resource_import_supersessions` from unique
to nonunique. It preserves all rows, request/staged-credential/successor uniqueness,
foreign keys, checks and exact owner constraints. Several independent protected
requests and correction audits can therefore belong to one accepted ChangeSet. There
are no new tables, Provider-specific columns, triggers, procedures or automatic resets.
Each audit requires its own transaction-bound authorization and complete membership;
legacy whole-target import correction refuses ambiguous multi-request linkage.
Downgrade recreates the original unique indexes. If new cardinality is present,
downgrade fails transactionally instead of deduplicating or discarding history.

## C: initial mixed Application

Depends on B. Enable the same public transition path with expected absence and a
mixture of new ImportUseAndRetain and Reference Resources. Remove the initial
PostgreSQL-only shape restriction only where the composed capabilities validate it.
Keep PostgreSQL coupled-pair verification in its adapter, not generic planning.

Acceptance: one initial public ChangeSet containing PostgreSQL import plus synthetic
Reference from another Provider; protected credential intake, Plan/Check/Commit/
Execute and combined authorized delivery. Exercise failure of either group,
completed-group retry without repeated acquisition, restart, missing capability and
no partially available generation. Preserve original credentials, data and authority.
Document the initial and successive adoption commands without catalog resets.

## Independent review instructions for each slice

Review the actual PR at an exact full HEAD against its issue, ADR-064, AGENTS,
CONTRIBUTING and ROADMAP. Read-only review; report outside the repository and update
incrementally. No commits, push, GitHub comments, dispatches or merge.

Prioritize observable acceptance, provider neutrality, authority/data safety,
concurrency/retry and test effectiveness. Inspect public dispatch and actual graph
consumption, not only helper tests. Check for leftover uniform-Provider assumptions,
Provider-name combinations, reclassification as Provision, unauthorized credential
reuse, coupled-group partial publication, stale baseline acceptance and early delivery.
For B inspect protected intake/correction and purpose/owner checks; for C require
actual initial mixed admission, not separately passing initial adapters. Preserve
existing behavior; do not invent general UPDATE or retirement requirements.

Use coordinator-acquired original CI evidence once. Bind source review and checks
to current SHA; distinguish static inspection, execution and unperformed checks.
Findings require P0-P3, exact file/line, violated contract, concrete scenario and
smallest useful correction/test. No style blockers or speculative framework demands.
State merge readiness and evidence limits explicitly. Architect approval is separate.
