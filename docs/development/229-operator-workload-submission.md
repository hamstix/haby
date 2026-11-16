# Submit an accepted initial Workload generation

`habyctl deployment generation submit <deployment-id> <generation>` asks the
operator Host to submit an exact already accepted generation. The operator-admin
credential authenticates the caller; the server resolves its original Commit,
frozen Provider/Placement inputs and all preparation/action authority. No client
Kubernetes access or caller-provided receipt, action selector or capability exists.

```text
habyctl changeset create <application-record-id> <revision-number> --expected-absence
habyctl plan create <changeset-id>
habyctl plan check <plan-id>
habyctl changeset commit <changeset-id> --plan <plan-id>
habyctl deployment generation submit <deployment-id> <generation> -o json
habyctl deployment generation get <deployment-id> <generation> -o json
```

For the first Workload addition to a fully applied Resource-only deployment, use
`--expected-generation <fully-applied-generation>` when creating the ChangeSet.
Only those two #227 shapes are supported. The entire Resource set must be unchanged
with original reuse proof; no new, retained or reintroduced Resource is admitted.
Unconfigured Workloads create no Haby account, binding, publication or token mount.
Configured Workloads use the exact selected documents and frozen delivery settings.
Unsupported settings, updates, #209 static Profile secret projections and companion
objects continue to refuse.

The sequential server path establishes genuine ResourcesReady evidence, prepares
all exact documents, confirms all required stable runtime identities, publishes the
exact permissions and submits initial Deployments. A partial stage blocks every
later stage. These calls do not provision Resource producers again. Shared documents
keep their exact original artifact/recipient records and audit.

## Progress and explicit repeat

Submission replies and generation get expose safe per-Workload stage states,
operation identifiers and current runtime authority. Unvisited work is notAttempted;
pending or uncertain effects are distinct from confirmed historical results.
Complete submitted requires the saved evidence-bearing WorkloadsSubmitted checkpoint
and validated confirmation for the complete accepted target set. It never means a
successful prefix, Kubernetes readiness or business health. Unknown/invalid saved
confirmation proof stops mutation before any new preparation or effect; GET may
still return a safe unknown projection. Skipping confirmed work validates its
original immutable receipt rather than trusting a Succeeded state label. AppliedGeneration stays
null or at its previous value. Public wait/resume, retirement and batch observation
are separate future slices; no background worker continues this request.

After a lost/canceled response, inspect the exact generation. An explicit repeat
uses its saved documents, identity, publication intent and CREATE journal. A committed
dispatch marker never allows another write: the existing action-specific observation
path may confirm that original effect. Missing, foreign or conflicting observations
stop submission; name equality cannot authorize adoption or recreation. A request
failure is not proof of rollback. RequiresInspection marks an unavailable/ambiguous
result; a later catalog read is the source of durable progress.

Original publication history and current permission are independent. Revocation
cannot erase historical Submitted or confirmation of an already dispatched write.
It blocks new dependent work and is never repaired by replay, a fresh random intent
or another operator's credential. The original publication request/actor is retained
when a different authenticated operator continues partial work.

## Bounds and catalog impact

`Haby:WorkloadSubmission:RequestMilliseconds` defaults to 30000, accepts 1..300000,
and applies at Host startup. Invalid values fail startup. The RPC cancellation/
deadline can shorten the request. Queue/gate waits and all stages consume this one
budget; no later API call starts once it expires. Existing action limits remain
at most 10 seconds and consume remaining time. Finite catalog-only result closure
is limited to 10 seconds and grants no further API time.

Admission retains 512 Workloads, 512 selected recipient/document pairs and 1..64
documents per configured Workload. All cardinalities are checked before new work.
The derived maximum is 2048 distinct stage items, not 2048 network requests: adapter
validation/preflight has its own finite calls. Work runs sequentially without an
outer retry loop. Responses are capped at 2 MiB. Submit/restart never renews existing
rollout observation deadlines (default rolloutSeconds 600); submission performs no
readiness polling or audited resume.

The additive `AttributeWorkloadActionPrincipals` migration adds nullable bounded
actor columns to the existing generic action intents, operation attempts and action
observations. New operator-created transitions record the trusted grant principal
atomically with that transition. Replay cannot replace it; actors do not participate
in deterministic keys. Historical null means unattributed, never the current caller.
No old audit is backfilled and an ambiguous commit cannot claim attribution persisted.
No workflow table, trigger, data reset or Kubernetes-specific catalog field is added.
Original documents, permissions, receipts, markers and confirmation deadlines remain.
Resource-only generation execute and ordinary pre-Applied document guards are unchanged.

## Verification venues

Fast tests cover production orchestration stop/barrier/cancellation behavior,
historical completion despite revocation, generated CLI text/JSON/native output,
whole-set output refusal and immutable actor attribution. Real PostgreSQL cases use
operator Plan/Commit and production submission through independent Host compositions
for both shapes, configured/unconfigured selection, restart/response loss/revoke,
original actors and unchanged Applied; contexts remain separate for arrange/act/assert.
The existing configured qualification drives real operator acceptance and Host
submission, published CLI observation-only continuation, actual generated Pod startup,
exact two-document delivery and retained controller/TokenReview/revocation/cleanup
checks. It requires a published CLI through HABY_AOT_CLI_PATH; it reuses the fixed
f362ced2 input / sha256:980a5434 consumer binding. No image build or new cluster harness
is introduced. See the existing suite and Kubernetes guides for commands.

Test inventories describe obligations, not passing evidence. Exact-source results,
original failures, unavailable venues and owned cleanup belong in task/CI evidence.
No mass-start capacity, general lifecycle or P3-completion claim is made.
