# ADR-065: Explicit Resource acquisition stage before Workload advance

- Status: Accepted
- Date: 2026-11-28
- Extends: [ADR-052](0052-corrective-generations-for-incomplete-workload-rollout.md), [ADR-062](0062-existing-resource-import-and-attachment.md), [ADR-063](0063-resource-capabilities-rendering-and-provider-status.md) and [ADR-064](0064-composable-resource-acquisition-transitions.md)
- Implementation: [#325](https://github.com/hamstix/haby/issues/325), a separate P3 slice after verified completion and merge of the full #319 runtime activation (not an internal prerequisite PR) and before #320; this decision does not claim runtime support or expand #319.

## Context

An operator adopting an existing application must register its complete declaration
once, confirm protected imported and referenced dependencies, and only then
explicitly launch its Workloads. Requiring a Resource-only manifest followed by a
second revision hides the actual application identity and creates avoidable import,
credential and proof-transfer risks. The current initial Resource/Workload
composition prerequisite supports existing Provision actions; it does not silently
enable mixed ImportUseAndRetain and Reference with Workloads.

## One immutable declaration, Plan and generation

Retain ONE full immutable Application revision, ApplicationVersion and authored
bytes. Accept ONE deployment and generation with ONE full frozen Plan containing
Resources and Workloads, whose Workload phase is deferred. Do not create a stripped
manifest, hidden revision, per-installation version or parallel planner. Repeated
registration of the same bytes is idempotent; different content under the same
version conflicts under the existing immutable-import contract.

Freeze exact Environment, Application and Resource identities, Provider instances
and revisions, implementations, acquisition request membership, SecretVersion
references, configuration dependencies and Workload pins. Shared models remain
Provider-neutral under [ADR-035](0035-provider-neutral-persistence-and-name-reservations.md).
Typed adapters retain mode-specific semantics. Necessary encoding changes follow
[ADR-033](0033-development-compatibility-and-catalog-resets.md), never an automatic
schema reset or cleanup operation.

## Protected acquisition and explicit phase boundaries

Protected Import intake remains separate from the manifest. Pass material through
the existing protected adapter groups and owner/purpose checks; the accepted Plan
uses opaque import request identifiers and exact protected references. Reference
requires a legitimate explicit capability and genuine authority. Neither mode may
fall back to Provision. Do not copy credentials or generate replacement passwords.

The first explicit server execution is limited to the Resource stage. It must not
automatically launch Workloads. Successful confirmation produces durable exact
ResourcesReady proof bound to this generation and original associations and
operations. ResourcesReady is not Applied and the Resource-stage response must not
claim that the full Application succeeded.

A separate operator-admin authorized Workload advance selects the exact SAME
generation. The server resolves and validates its exact checkpoint; callers do not
invent proof links or submit internal Secret/staging handles. Revalidate current
authority, revocation and required Resource verification before Workload effects.
Historical success alone is not blanket permission. Reuse the existing stage
projection, checkpoints, Submit and deployment fences rather than a generic phase
framework. Document the actual public generated API request/response sequence and
reuse existing names where possible; this ADR does not invent endpoint spellings.

Public Plan/inspection must distinguish pending Resources, ResourcesReady,
deferred Workloads, submitted Workloads, readiness and final Applied. Existing
public Resource-only and Workload-only paths retain their contracts.

## Retry, recovery and completion

Partial group completion, cancellation, controlled response loss, retry and restart
preserve original request identities, protected material, receipts and external
identity. Confirmed groups are not reimported. Uncertain outcomes use existing
recovery; absent proof or material refuses rather than repairs. Incomplete required
proof or revoked current authority prevents Workload effects and early delivery
permission. Reject foreign scope, Provider, request or proof and changed material,
membership or stale revision without new effects or loss of prior evidence.

Applied advances only after full Workload readiness and existing retirement
obligations. Runtime bootstrap, delivery authentication and online authorization
remain unchanged. Authenticated delivery resolves original confirmed outputs.
No external Resource UPDATE, replacement, mutation, cleanup or lifecycle expansion
is authorized by this decision.

## Delivery and evidence

Implement after verified completion and merge of the full #319 runtime activation
(not an internal prerequisite PR), then complete #325 before #320 CLI and integrated
operator rehearsal. Do not interrupt #319 or duplicate its author/reviewer. Code and review
cards are in [the staged acquisition guide](../development/staged-resource-acquisition.md).
Use existing fixtures and CI venues, relevant real PostgreSQL tests and fixed
smokes, current mandatory checks and real Kubernetes evidence; no load or new
infrastructure campaign. Independent review binds conclusions to the complete
current SHA. Architecture acceptance, source review, CI, installed qualification,
architect approval and merge remain separate states.

Acceptance must exercise the actual generated authorized API with a full manifest,
real protected standard PostgreSQL pair ImportUseAndRetain, a typed fixture
Reference with genuine authority and a real Kubernetes Workload. Direct accepted
row/proof seeding, relabeled modes and internal acceptance helpers are insufficient.
Prove no Workload effects before all required Resource proof/current authority,
then advance the same generation through Submit/readiness/retirement. Cover partial
confirmation, response uncertainty, restart/replay, cross-scope/material substitution,
cancellation, operator authorization and delivery-token refusal, plus unchanged
Resource-only and Workload-only paths. Preserve the limits of any simulated or
unperformed evidence.

Parameters, expression engines, broader update/replacement, new Resource lifecycle
powers and new runtime authentication are excluded. The separate mandatory
Application manifest parameters architecture checkpoint remains before P4.
