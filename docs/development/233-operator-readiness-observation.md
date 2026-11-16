# Exact-generation readiness observation

`habyctl deployment generation wait <deployment-id> <generation>` asks the authenticated Host to observe an already accepted, submitted initial Workload generation. It supports the same two shapes as [submission](229-operator-workload-submission.md): an initial Workload-only deployment, or the first supported Workload addition over an Applied Resource-only baseline. It never submits unfinished members or prepares documents, identities or publications.

```text
habyctl deployment generation wait <deployment-id> <generation> -o json
habyctl deployment generation wait <deployment-id> <generation> --request-id <uuid> --maximum-rounds 1
habyctl deployment generation resume <deployment-id> <generation> --member Workload/api/main=<expired-attempt-uuid> -o json
```

Each invocation makes one generated operator RPC. The CLI does not contact Kubernetes or poll. `resume` renews only the explicitly named expired observation attempts and returns without a live read. Use a separate `wait` afterward. These commands do not resume submission, recover uncertain writes, change desired inputs, retire objects or advance AppliedGeneration. [Bounded product/CI batch observation](235-batch-readiness-wait.md) uses a separate one-RPC command. Full lifecycle wait/resume remains separate work.

## Request identity and historical results

Both commands accept optional `--request-id`, a nonempty UUID generated once per invocation when omitted. Save the returned `requestId` before an explicit retry. The RPC accepts the existing durable key syntax (1..128 ASCII letters/digits, hyphen, underscore or dot). A key identifies one action, authenticated principal and canonical logical inputs for one exact generation. Changed inputs/action/principal with the same key conflict. Keys never grant authorization.

Identical replay makes no duplicate API reads or renewals, including an interrupted wait whose read reservation was already persisted. Replayed `currentReady` is false. `originalRequestReady` reports what the original request proved; `savedReadyId` and `submittedId` identify historical catalog proof, not present permission or readiness. A new wait can be Pending or Refused despite historical Ready. Responses include the complete accepted target set and its total, with each subject, current attempt, deadline, budget, outcome and safe code. Unsubmitted or uncertain members stay incomplete and require the separate submission/inspection path.

A lost/canceled response proves neither completion nor rollback. The CLI retains the request ID and advises repeating identical inputs with that ID to inspect/replay the original outcome. Resume replay is labeled historical and does not renew again. New public requests and resume attempts record the immutable authenticated operator principal; legacy observer history is unchanged. No catalog migration or backfill is required.

## Current permission and observation deadlines

Authenticated exact catalog history/replay remains readable after runtime permission revocation. Every fresh public readiness read checks current binding/publication permission using the exact original identity and document provenance before reserving the read. Revocation prevents subsequent reads and new resume transitions without republishing or granting access. An already dispatched read may finish and is reported honestly; cancellation of that read due to later revocation is not promised. Document-free Workloads require no artificial publication.

The original rollout deadline is anchored to confirmed dispatch, not the first wait. New requests, cancellation, restart and restore never renew it. A new wait after rollout expiry retains the existing finite recovery-read rule: at most one bounded read per expired submitted member, allowing observation after external repair. A request already expired on arrival or after waiting for the generation gate starts no API work. Only explicit resume of the exact current expired attempt creates a new bounded attempt using the saved rollout budget. Duplicate, stale, foreign, nonexpired or mixed invalid resume sets reject atomically across independent Hosts; all previous attempts remain recorded.

## Configuration and bounds

Host settings live under `Haby:WorkloadObservation`:

| Setting | Default | Range | Caller narrowing |
| --- | ---: | --- | --- |
| MaximumCalls | 64 | 1..512 | Fewer calls |
| MaximumRounds | 32 | 1..512 | Fewer rounds |
| ReadTimeoutMilliseconds | 10000 | 100..10000 | Shorter read timeout |
| PollMilliseconds | 1000 | 100..10000 | Longer interval |
| RequestMilliseconds | 30000 | 1..300000 | Shorter total request |

CLI overrides are `--maximum-calls`, `--maximum-rounds`, `--read-timeout-ms`, `--poll-ms`, `--request-ms`. Widening Host authority is rejected. The total request budget includes authority lookup, queue/gate and reservations, and is capped by the RPC deadline and cancellation. Transient remaining RPC time is not part of the persisted logical request identity. After cancellation, existing catalog-only closure remains bounded to 10 seconds and starts no further API work.

At most 512 Workloads and 512 unique explicit resume members are accepted. Subjects retain the existing typed local-ID bounds. Requests are bounded to 128KiB, individual observation evidence to 16KiB and results/proofs and protobuf replies to 2MiB. Results fail closed rather than silently truncate. No raw document, endpoint, SQL, token or driver exception is returned.

## CLI exit status and qualification

Wait exits 0 only for a fresh complete whole-set Ready proof. Historical/replayed Ready, Pending, refusal, conflict, unknown and unsupported/not-found outcomes exit 1. Invalid input exits 2. Cancellation/deadline/storage or transport unavailability exits 3. Resume accepted or identical replay exits 0, explicitly meaning observation renewal or its historical replay, never present readiness.

Kubernetes readiness requires the saved UID/ownership/projection and exact controller/replica/minReadySeconds criteria. It is not an application smoke test, retirement proof or Applied completion. Existing internal readiness invariants remain tested separately. New public-boundary tests cover one-RPC CLI/native behavior, both genuine Host submission shapes, actor/replay/revocation, deadlines and independent-Host atomic expired resume. Configured qualification retains C01-C11/R01-R04 and adds native public wait/resume observations, reusing the immutable f362/980a consumer image. No consumer inputs or image build change.

Test inventories are obligations, not passing evidence. Exact-source reports and CI artifacts must retain original failures, unavailable venues and owned cleanup limitations, including historical #229 UNKNOWN evidence. This slice does not claim P3 completion.
