# Staged Resource acquisition before explicit Workload advance

Authority: [ADR-065](../adr/0065-explicit-resource-stage-before-workload-advance.md),
[issue #325](https://github.com/hamstix/haby/issues/325), ADRs 033/035/052/062/063/064
and current CONTRIBUTING. Queue: verified #319 merge -> #325 -> #320 CLI and
integrated operator rehearsal. The decision is accepted; runtime is not implemented
by these documents. Current #319 work continues unchanged.

## Code-agent task

Start an isolated topic branch from verified main after #319 is merged. Preserve
other agents' work and the existing exact-SHA evidence. Inventory public protected
acquisition intake, SupportsInitialObservationComposition, the PostgreSQL protected
preparer, InternalResourceStage.IsInitialCreate, initial shape admission, Commit,
durable journal, ResourceContinuity, ResourcesReady, public stage inspection and
Submit. The current Provision composition is not mixed Import/Reference support.
Change necessary adapters and shared mode-aware stage admission consistently;
simply removing the Workload guard is insufficient.

Implement one full immutable revision/version/authored-byte identity, one deployment
and generation and one full frozen Plan with deferred Workloads. Preserve exact
Environment/Application/Resource/Provider revision/implementation, acquisition
request membership, SecretVersion references, configuration and Workload pins.
Use real protected adapter intake groups and owner/purpose rules for imports,
explicit typed capability/authority for Reference, no implicit Provision. Preserve
original associations, operation identities, output receipts and credential material.
Do not strip Workloads from the registered declaration, seed accepted rows, invent
proof or copy secrets into a second installation manifest.

Expose explicit server execution limited to Resources. Complete all required group
confirmation before ResourcesReady; this is not Applied or full Application success
and must not automatically dispatch Workloads. A separate operator-admin advance
selects the exact same generation. Resolve proof server-side and validate current
authority, revocation and required observational verification before Workload effects.
Do not trust caller-provided internal handles or historical success as permission.
Use the existing projection/checkpoint/Submit/fence seams, not a parallel planner or
generic phase framework. Preserve current Resource-only and Workload-only paths.

Public Plan and inspection must distinguish pending Resources, ResourcesReady,
deferred Workloads, submitted Workloads, readiness and final Applied. Document the
exact request/response sequence from the implemented generated API, reusing current
names where possible. Do not document proposed endpoint names as existing runtime.

Partial groups, cancellation, controlled response loss, retry, replay and restart
must keep original requests, material, receipts and external identities. Reconfirm
only where existing recovery requires it; never reimport a completed group or repair
missing evidence. Absent proof/material and uncertainty fail closed through existing
recovery. Applied requires full readiness and existing retirement obligations;
delivery authentication/runtime bootstrap stay unchanged and use original outputs.

Keep Provider-neutral shared models, typed adapter semantics, asynchronous EF,
source-generated JSON/AOT and CRLF. Any required encoding change follows ADR-033
explicitly, with no silent schema reset, cleanup or Provider-specific tables.
Broader update/replacement, Resource mutation/lifecycle expansion, parameters,
expression engines, new runtime authentication, load and new infrastructure are out.

## Required acceptance and evidence

1. Through the actual generated authorized public API register the full manifest
   once. Repeating the same bytes is idempotent; different content under the same
   version conflicts. The full frozen Plan retains Resources and deferred Workloads.
2. Exercise a real protected standard PostgreSQL ImportUseAndRetain pair, a typed
   fixture Reference with genuine authority and a real Kubernetes Workload. A relabeled
   mode, direct accepted-row/proof seed or internal accepting helper is not evidence.
3. Prove explicit Resource-only execution and partial group completion. No Workload
   or early delivery effects occur before all required proof and current authority;
   ResourcesReady leaves Applied unset and responses distinguish stage completion.
4. Control lost/uncertain response, cancellation, restart and replay. Original
   request IDs, material, receipts and external identities survive without Resource
   creation/mutation, reimport, generated passwords or loss of accepted evidence.
5. Reject foreign Environment/Application/Resource/Provider/request/proof, material
   or membership substitution, stale revision and incomplete proof without new effects.
   Historical receipts do not override revoked current authority.
6. Advance the exact same generation through actual Submit, fresh readiness and
   retirement to Applied. Authenticated delivery resolves original confirmed outputs;
   operator authorization and delivery-token refusal remain enforced.
7. Preserve Resource-only and Workload-only regressions and all existing assertions.
   Use existing fixtures/CI venues, focused real PostgreSQL plus fixed smokes and
   current mandatory checks; do not add load or infrastructure campaigns. Record
   full SHA/tree, selections, RED/GREEN where applicable, cleanup and unperformed
   real Kubernetes/AOT/hosted evidence separately.

## Independent review-agent task

Wait for a concrete complete exact-SHA packet. Review the full diff against issue
#325, ADR-065, ADRs 035/052/062/063/064 and current stage/auth documentation. Read all
admission, Commit, journal, completion, continuity, publication and Submit consumers;
look for accidental mode/authority expansion and incomplete gate updates.

Verify full-manifest identity cannot escape through projection; exact Plan/proof
links remain immutable; Resource-stage success cannot become final completion;
caller-supplied scope/material/request aliases cannot substitute frozen inputs;
aliases are not re-resolved after acceptance. Inspect adapter grouping, partial
confirmation, protected owner/purpose, retry/uncertainty, current authority versus
old receipts and absence of early Workload or delivery permission.

Tests must genuinely traverse public import plus Reference intake with real protected
material and authority. Inspect actual real Kubernetes evidence and absence of
external Resource mutations. Distinguish source findings, local tests, current-SHA
CI/installed evidence, limitations and unperformed checks. Report actionable findings
and exact-SHA verdict only; do not post comments, acquire duplicate coordinator
artifacts, run duplicate campaigns or merge. Architect exact-SHA acceptance remains
separate from source review, CI, installed qualification and coordinator merge.

## Follow-ups

#320 CLI and integrated operator rehearsal consume this completed stage contract;
they must not impose a copied-manifest workaround. Before P4, the user and architect
must separately decide Application manifest parameters and required implementation
scope as recorded in ROADMAP. That checkpoint grants no parameter code or expression
engine authorization and is not a runtime-completion claim for #325.
