# Initial retirement-aware Applied completion

Issue [#237](https://github.com/hamstix/haby/issues/237) completes two bounded
initial shapes through the existing authenticated server-side wait:

- Initial Workload-only generation, with an explicitly empty Resource stage.
- First Workload addition over a fully applied Resource-only baseline, with
  exactly unchanged Resource reuse and no prior Workload ownership.

Submit remains explicit. Wait never starts unsent provisioning, prepares missing
documents, recreates identity, republishes permission or retries uncertain writes.
No Workload update/removal, nonempty retirement executor, Resource deletion,
rollback, corrective generation or product-wide transaction is introduced.

## New accepted inputs

The current alpha model uses operation graph **4**, durable input **5** and Plan
fingerprint domain **v5**. Every new internal Workload Plan freezes a closed
version-1 initial retirement inventory, including shape, expected prior Applied,
complete prior accepted Resource-only provenance, retained Resource obligations
through the graph, target subjects/contracts and exact Provider/Placement inputs,
selected document/identity obligations, and explicit empty obsolete object and
publication arrays. Object roles are validated against the exact accepted action
contracts and confirmed receipts when closing the generation.

ExpectedAbsence plus atomic first-generation acceptance establishes initial
origin. A baseline addition records every prior accepted generation and its
Plan/ChangeSet/revision/fingerprint and Resource checkpoint reference. Missing
retirement rows or a current namespace listing never establishes emptiness.
Check and Commit revalidate the frozen inventory; accepted bytes are not rewritten.

Bounds are 256 prior generations, 512 target Workloads, 512 total document
recipients, 64 documents per Workload and 512 KiB inventory JSON inside the
2 MiB graph. Prior source payloads are limited to 16,777,216 total characters;
completion prerequisite text is limited to 2,097,152 characters before loading and
16 MiB serialized evidence before hashing. Overflow refuses rather than dropping an item. Nonempty or unknown
retirement obligations remain unsupported and perform no cleanup.

This is an ADR-033 alpha format boundary. Older accepted input formats require
an explicitly initialized fresh catalog before new execution. Missing inventory
returns an unsupported/incomplete result. There is no compatibility reader,
automatic reset, migration of accepted bytes, successful-proof backfill or
inference from scalar AppliedGeneration. Resetting a catalog does not remove
external Resources or Kubernetes objects; their handling remains an explicit
operator decision. The schema extension only admits the new checkpoint stage.

## Wait, replay and completion

```text
habyctl deployment generation wait DEPLOYMENT_ID GENERATION -o json
habyctl deployment generation wait-batch --file members.json -o json
```

Both use one Host RPC. Existing exact-generation inputs, authentication,
request identity, attempt deadlines, observation limits and batch admission
budgets still apply. Explicit observation-only resume renews eligible expired
attempts before completion; it never acquires preparation/submission authority
from a completed-history read.

For every target, the final locked closure validates exact ResourcesReady,
required configuration artifacts/recipient associations, conditional confirmed
identity and CURRENT runtime rights, complete confirmed submission and a fresh
full readiness round from this request and the current attempts. A saved
RolloutReady checkpoint can be historical; the final receipt separately binds
the new complete round's sample identities and hashes. Document-free Workloads
require no synthetic document, ServiceAccount, binding or publication.

The same catalog transaction saves RetirementCompleted, advances Applied using
the expected prior generation and concurrency token, and closes the request.
It rechecks complete ownership/publication references under the aggregate lock.
No network or SecretStore material access occurs under that lock. Empty
retirement does not install a destructive-start marker and performs no DELETE,
revoke or identity cleanup. Current publications and Resource outputs remain.

Current readiness is recent exact evidence, not perpetual reconciliation,
cross-system atomicity or proof that application health checks ran beyond the
configured Kubernetes probes. Permission can be revoked after completion.

Results distinguish:

| Field | Meaning |
| --- | --- |
| `currentReady` | This invocation freshly observed the complete exact target set. |
| `retirementCompleted`, `retirementId` | Validated saved empty-retirement completion and its proof reference; replay is historical. |
| `currentApplied` | This fresh invocation confirmed Applied with valid completion evidence. |
| `originalRequestApplied` | The saved original request confirmed Applied; this alone is not fresh success. |
| `appliedGeneration` | The reported catalog generation associated with the result. |
| `completionReason`, `nextAction` | Closed unfinished reason and a safe operator action. |

Single wait exits 0 only with fresh complete readiness and confirmed Applied.
Batch exits 0 only when every required member freshly confirms Applied; it keeps
all ordered summaries within the original 200-member/2 MiB reply bound. Ready
with incomplete retirement/Applied exits incomplete. Resume's existing successful
renewal acknowledgment is distinct from application completion.

Repeating identical inputs and the same request ID returns immutable historical
results, without new observation or completion. It cannot turn an older ready
request into new lifecycle authority or exit 0 as fresh application success.
A new wait may freshly observe an already-Applied generation and confirm its
existing completion proof without another transition. Existing internal
observation-only callers can read completed evidence without gaining finalization.

Cancellation before commit admission prevents a new completion commit. After
commit dispatch, cancellation or a lost response does not establish rollback.
An unconfirmed result never claims Applied or silently retries the transition.
Inspect the saved request; a fresh request may subsequently confirm exact state.
Budgets remain cooperative with their dependencies, not a hard wall-clock promise.

## Access and evidence boundaries

Completed-history authentication and observation use a separate immutable proof
read authority. Preparation, submission, renewal, materialization and publication
retain pre-completion guards. Runtime identity validation remains separate from
current document materializability. Runtime delivery still requires current exact
publication rights; a historical completion receipt cannot restore revoked access.
The proof validates exact configured target/document publication coverage and hashes
the canonical publication references with the retained catalog evidence. This is
proof-contract validation; current delivery authorization remains a separate check.

The ordinary fully-applied configuration document selector retains its existing
InternalEffects exclusion and Resource-only eligibility. Workload runtime retrieval
continues through explicit runtime publication/recipient authority after Applied.

The ledger adds 17 fast cases, 27 functional PostgreSQL cases and 10 published-native
cases per platform. The configured consumer retains its existing image inputs
and adds A01-A03 for exact completion, retrieval after Applied/restart and retained
completion after revoke. Its renewal cases run before Applied. These are declared
obligations; only current-source executed results establish passing evidence.
Full PostgreSQL, Windows/Linux Native AOT, configured real-cluster qualification,
dependency checks and exact-SHA independent review remain required. The separate
mass-start rehearsal remains deferred until after P4.
