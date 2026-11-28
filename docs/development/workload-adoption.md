# Workload adoption implementation and independent review cards

Authority: [ADR-066](../adr/0066-workload-adoption-and-explicit-resource-authority.md),
ADRs 026/033/035/049/050/051/053/061/062/064/065, current CONTRIBUTING and ROADMAP P3.
Tracking: [#327](https://github.com/hamstix/haby/issues/327). Slices: [A / #328](https://github.com/hamstix/haby/issues/328), [B / #329](https://github.com/hamstix/haby/issues/329), [C / #330](https://github.com/hamstix/haby/issues/330), [D / #331](https://github.com/hamstix/haby/issues/331). These are implementation tasks, not claims of completed behavior.

## Dependencies and boundaries

Keep current #319 execution and review unblocked. Full #319 completion and merge
precedes #325; completed #325 precedes adoption A-D, followed by #320 final CLI and
integrated rehearsal. Reuse the completed paths without expanding their criteria.
The coordinator owns issue deduplication, sequencing and execution-agent assignment.
Use one parent tracking issue and the four bounded slices below. Resource authority
is an explicit additional prerequisite, not a hidden consequence of Workload adoption.

Existing generated API and CLI remain Host-only clients, never direct Kubernetes or
database administration. No shared product Ingress, per-object names, workload
replacement, Resource rotation/ALTER/delete implementation, arbitrary YAML, expression
engine, new Provider, catalog reset or broad test-infrastructure campaign.

For every slice inventory actual source/contracts before editing. Reuse existing
typed capabilities, provider-neutral rows, asynchronous EF transactions, generated
serialization and durable effects. Record any fresh-catalog requirement explicitly
under ADR-033 without reset/backfill. Preserve production redaction and CRLF.

## A. Explicit Resource management authority

Goal: an operator can request capability-checked management authority for imported
Resources in the migration Plan, while import remains observational and deletion
stays separately restricted. Absent an explicit transition retains current behavior.

Implement exact typed intent, Plan representation, capability validation, durable
confirmation/audit and enforcement in existing consequential consumers. Preserve
original acquisition mode, import provenance and credential owner/purpose. Do not
implement new rotation, grants, ownership changes or deletion as part of this slice.
Unsupported capabilities refuse, and Reference cannot gain mutation rights through
generic plan participation. Same-Plan acceptance must not publish authority before
required attachment and exact current input validation.

Acceptance: supported transition and unchanged default through public application
services/API; unsupported transition, wrong scope, stale inputs and deletion refusal;
retry, concurrent conflict and restart; original import outputs/provenance remain
usable; no external database/role/password/ACL changes. Use a typed fixture capability
where real mutation capabilities do not yet exist; never advertise unsupported
production operations as enabled.

Review focus: every authority consumer, bypass through reuse/removal or ordinary
execution, forged capability, accidental provenance rewrite, default upgrade or
credential-purpose change. Inspect transaction boundaries and real persisted tests
with separate arrange/act/assert DbContexts.

## B. Typed adoption intent and migration planning

Depends on A for authority integration. Goal: freeze an installation-specific typed
adopt/create inventory and managed baseline/target for a full portable Application.
Do not enable public accepting execution before its continuation is implemented.

Use baseName/nameOverride only, with standard runtime/headless suffixes. Resolve
exact Provider/namespace and server-observed UID; no UID or installation credentials
in the portable manifest. Validate existing Deployment, Service and optional
individual Ingress, new ServiceAccount and optional Headless Service. Inventory
current typed companion contracts and extend only required adoption fields.

Plan exposes supported managed diffs and rollout effects without raw secret-bearing
bodies. Freeze explicit old-source or Haby-delivery selection; old-source is not a
fallback on authorization failure. Preserve UID, immutable selector, containerName,
network identity, routing and unrelated supported fields. Name mismatch, foreign
Haby ownership, unsupported shape, missing adopt target and occupied create target
refuse without external mutation. Required existing objects cannot be silently
excluded as exceptions to obtain a successful Plan.

Acceptance: deterministic public planning/check and snapshot round-trip for both
modes; same full manifest/version and generation inputs; negative identity, names,
scope, secrets and shape cases. No Plan/Check mutation. Pure planning tests plus
focused real API observation where Kubernetes defaulting matters.

Review focus: identity versus names, portable/installation boundary, codec retention,
closed typed inventory, drift projections, no create/adopt fallback, no new parallel
planner, and unsupported runtime admission staying closed.

## C. Durable Workload adoption and dependency creation

Depends on B. Goal: adopt existing objects and create the declared missing
dependencies through the existing durable journal and Kubernetes adapter.

Require explicit old-manager handoff. Confirm adoption of all selected existing
objects before Deployment Pod-template changes. Use conditional JSON Patch with
UID/current resourceVersion and managed baseline checks. Persist intent before
effects, exact markers with writes, and imported/adopted evidence rather than CREATE
receipts for existing objects. Keep unrelated fields and all original UID/network
identities. Prepare stable ServiceAccount and Headless Service with normal create
safety; do not replace ordinary Service or change routing silently.

Acceptance: real Kubernetes success, same-UID preservation, baseline drift,
resourceVersion-only churn, replaced UID, foreign ownership, collision, controlled
lost response and restart at dispatch/confirmation boundaries. Partial adoption
resumes without repeat takeover, recreate, rollback or blind dependency cleanup.
No template rollout from bookkeeping alone. Missing evidence refuses, not repairs.

Review focus: atomic markers/preconditions, observation evidence, stale executor
fencing, all-object dependency order, external field preservation, no blanket force
or delete/recreate, honest partial progress and correct inventory retention.

## D. Public integrated Resource and Workload migration

Depends on A-C and completed #325. Goal: one full revision and one generation
import/reference Resources, explicitly pause at ResourcesReady, then adopt the
Workload and reach truthful completion. Provide the exact generated API sequence
and API-only CLI operator instructions without inventing undocumented endpoint names.

Direct mode prepares exact configuration and authorized runtime access before new
Pods start. Two-stage mode first adopts the old image/source; explicitly planned
Pod-template changes, including serviceAccountName, may cause rollout and require
target readiness. Preserve the old source across that rollout, without implicitly
injecting Haby configuration bootstrap or granting delivery access. A subsequent
ordinary Application update changes
image and supported startup settings to Haby delivery. This explicitly extends the
necessary bounded update contract for that transition, not arbitrary PodSpec UPDATE.
Coordinate this work with #321's settings/configuration seam; implement/reuse the
needed bounded transition explicitly and leave broader #321 scope separate. Do not
claim that #319's image-only correction already implements this transition. #320's
final operator integration consumes the completed migration path.
The old configuration source remains operational until consumers move; Haby does
not administer it. Do not treat legacy-source readiness as Haby-delivery evidence.

Acceptance through actual public generated APIs: real PostgreSQL imported pair with
preserved data/credentials/authority, typed genuine Reference, existing Deployment,
Service and individual Ingress, new ServiceAccount/headless; stage pause without
Kubernetes effects; explicit advance; exact authorized delivery and target readiness.
Prove both modes and subsequent update, unchanged external UID/Service network
identity, failures/drift/revocation before advance, restart/replay/partial progress,
no premature Applied or delivery grant and ordinary create/update regressions.
Use a supported controlled old-source fixture without introducing a production
legacy-source integration. Document rollout strategy and no unconditional downtime
guarantee. Prove old-source adoption with a ServiceAccount-induced rollout as well
as no unnecessary rollout for operational metadata alone. Selecting that account
must not by itself publish documents or grant Haby configuration access. Preserve
existing source connectivity across the approved Pod-template change. Complete
required retirement before Applied; unsupported correction is
explicit rather than silently widening image-only correction.

Review focus: full public path rather than accepted-row seeding, actual standard
import and authority, exact pin/proof continuity, mode transitions, publication before
startup, readiness/retirement, no bypass via legacy mode and honest runtime evidence.

## Common validation, deliverables and independent review instructions

Code agent delivers scoped draft PR, linked issue, full HEAD/tree, focused regression
RED/GREEN where applicable, tests and operator/contract documentation. State schema
impact, remaining limits and unperformed checks. Run applicable fast tests, focused
real PostgreSQL plus fixed smoke, relevant real Kubernetes tests, formatting,
diff-check and current mandatory CI/AOT/installed qualification. Follow actual routing;
do not manually bypass a full fallback or replace runtime evidence with green source
tests. Full PostgreSQL regression remains nightly and full release qualification.

Reviewer independently reviews exact full SHA against its issue, this card, ADR-066,
AGENTS, CONTRIBUTING and ROADMAP. Inspect actual diff/consumers/tests and current CI,
not only the author's narrative. Review acceptance/correctness first, then architecture,
negative/boundary/regression tests, secrets/errors/concurrency and unrelated scope.
Explicitly check Provider neutrality in Host, CLI, core and persistence; typed adapter
semantics must not become shared Kubernetes/PostgreSQL fields or special combinations.

Read-only repository/GitHub: no edits, commits, comments, push or merge. Create the
external report at review start and update incrementally at
`D:/Projects/hamstix/tmp/code-reviews/PR<number>_<slice-name>.md`. Findings P0-P3 need
exact file/line, violated requirement, concrete failure and smallest useful correction
or test. Separate confirmed defects, questions and unverified risks; no style blockers.
Bind final verdict to full SHA, list checks not performed and say explicitly whether
blockers remain. Do not duplicate coordinator-owned artifact downloads or runtime
campaigns. Architect exact-SHA approval and coordinator merge remain separate.
