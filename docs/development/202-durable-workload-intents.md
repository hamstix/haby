# Internal durable action intents

[Issue #202](https://github.com/hamstix/haby/issues/202) follows the architect decision appended to the issue on 2026-11-13.
It adds an internal durable CREATE boundary around the #200 Deployment kernel.
It does not admit public Workload Plans, Host/RPC/CLI execution, readiness,
Submitted or Applied completion. Durable UPDATE admission is deferred until a
later integration slice supplies genuine accepted prior Workload evidence for
the same logical object and exact Provider binding. The stateless #200 update
kernel remains available under its existing caller obligations.

## Authority and scope

`InternalAcceptedGenerationBuilder` composes the existing ChangeSet, Plan and
Commit pipeline. A temporary internal scope is persisted on the same Plan and
generation models; no public flag or Host registration selects it. The path
requires a fully applied Resource-bearing baseline, a real imported mixed
declaration and unchanged reusable Resources. It does not support new/retired
Resources, Workload-only empty stages or configuration documents/Bindings in
this internal slice. The complete manifest, configuration, exact Provider set,
Placement payloads and fingerprint remain frozen. Resource planning uses a
derived Resource-stage view; it never creates Workload Resource nodes.

Internal Plans share aggregate/precondition locks, normal freshness checks,
configuration capture and generation row writers with public Plans. Placement
selection follows the existing Environment/alias-scoped precedence. Commit
rechecks exact selections; execution uses the saved inputs without consulting
current aliases. Accepted input rows restrict deletion of referenced Provider
and Placement revisions, including before an action is accepted.

Resource proof preparation, coverage, original attempt/output provenance and
validation are shared with normal ResourcesReady completion. Only completion
policy differs: the internal scope saves its own evidence-bearing checkpoint
and leaves AppliedGeneration at the actual prior fully applied generation.
An arbitrary checkpoint ID or generation foreign key grants no authority.
Public journal/runner admission refuses the internal scope before rehydration,
recovery or dispatch. Internal history remains readable without an installed
handler; execution requires the exact current registration and codec.

## Storage and transitions

The additive migration adds `internal_effects` with a false default, generic
`internal_plan_inputs`, immutable `durable_action_intents` and append-only
`durable_operation_observations`. Existing state, attempt and output tables and
the singleton serialization gate are reused. No new scheduler, server workflow
function or Provider-specific catalog column is introduced. Existing supported
catalogs can apply this additive migration; no reset or backfill is performed.
The current durable input version remains 4. Normal older-format refusal rules
continue to apply.

An intent pins a stable operation marker independent of dispatch attempt IDs,
the exact subject/action/handler/configuration contracts, bounded non-secret
adapter payload, exact Provider/Placement revisions and the validated generation
fingerprint/checkpoint. One request and one subject/action can each own only one
intent within the generation. Equivalent retries return that original identity;
conflicting context refuses without overwrite. Request IDs use the existing
OperationId character set and are limited to 121 characters by the `effect-`
operation prefix. The adapter owns all Kubernetes UID, namespace, projection,
operation annotation and receipt interpretation.

Preparation can resume before a committed marker. Shared conditional transitions
create a new attributable attempt, abandon any prior unsent preparation and
commit Dispatching before calling the kernel. An unconfirmed commit permits no
API call. Catalog transactions and connections end before secret resolution or
API work. A post-barrier interruption or unconfirmed result remains uncertain;
resumption for the exact opted-in action performs observation only. Negative or
unavailable observations append history and never reopen dispatch eligibility.
Ordinary PostgreSQL/manual recovery retains its existing behavior.

A confirmed response publishes its receipt, closes the dispatched attempt and
completes only the action in one guarded transaction. Observation confirmation
atomically appends its separately attributable evidence, publishes the canonical
output and completes the action while retaining the original attempt as
Uncertain. Missing/foreign markers, mismatched projections, absence and unavailable
APIs do not authorize adoption or recreation. Create has no prior UID; its UID is
learned only from exact operation/owner/projection evidence under ADR-049's
administrator trust boundary. Update does not accept caller-asserted prior UIDs.
Terminal replay validates and returns the original durable receipt without API
work. A lost completion response does not reopen the action.

The closed `haby.kubernetes.connection/v1` configuration codec pins explicit
HTTPS, trust roots and either bearer or mTLS exact SecretRefs. It has no ambient
kubeconfig, insecure trust or revocation setting. Production revocation remains
Online and fail-closed; the existing NoCheck fixture factory remains internal.
No resolved credentials or raw API response is persisted in intent/history.

## Validation and qualification

The integration tests use real PostgreSQL and independent arrange/act/assert
contexts. Their internal generation starts from an actually executed PostgreSQL
Resource baseline, then imports and accepts the mixed target. Synthetic action
coverage uses the same intent/state/barrier/observation/output path with its own
test action contract; it claims no additional production Provider.

The existing qualification project reuses these test fixtures and the qualified
API launcher. On a Linux Docker host:

```powershell
dotnet restore tools/Haby.Kubernetes.Qualification --locked-mode
$env:HABY_TEST_SOURCE_ROOT = (Get-Location).Path
./scripts/run-kubernetes-qualification.ps1 -Runtime docker -DurableActions `
  -EvidencePath .artifacts/durable-actions/evidence.txt
```

This mode exercises a real catalog plus the real API: dropped accepted response,
current-build catalog backup/restore, service restart, original UID confirmation,
terminal replay, marker-before-send absence, unavailable observations, exact
marker/projection refusals, retained uncertainty, unchanged Applied baseline and
committed marker/no catalog transaction at the actual HTTP boundary. It creates
only test-owned infrastructure through the existing fixtures/launcher. Its
inventory and cleanup are checked by the `durable-actions` hosted job. This mode
does not repeat the historical #200 K04 investigation or claim its cause resolved.

Ordinary full PostgreSQL, solution, dependency and both published Native AOT
gates remain required. A clean build or synthetic test is not real API evidence.
Recorded gate results must identify their actual source and failures; failed
hosted runs are retained and are not automatically repeated.
