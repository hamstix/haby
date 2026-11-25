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
