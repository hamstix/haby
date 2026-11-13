# Bounded Deployment kernel

Issue #200 implements an adapter kernel, not a Host capability. It has no durable
dispatch journal, admission, readiness, retirement or AppliedGeneration writer.
Intent reload followed by adapter reinstantiation proves stateless observation only.
The caller must supply stable logical ownership; endpoint and object name cannot
establish it. A later slice must persist and link intent BEFORE dispatch.

## Frozen v1 field contract (before implementation)

Only an effective `apps/v1 Deployment` from the existing pure Workload planner is
accepted. Validate the complete effective input, including Placement defaults,
before secret resolution, network access or writes. No new Workload syntax exists.

| Field | Absent/default/empty | Ownership and update/removal |
| --- | --- | --- |
| Namespace/name | Exact Placement namespace and pure planner name; empty refused | Immutable; no relocation, rename, adoption or recreation |
| Container name/image | Existing declaration container default `app`; already resolved complete image required | Name immutable; one keyed container; image owned; preserve injected containers and all unowned fields of the managed container |
| Replicas | Effective default 1; current portable range 1..10000 | Owned scalar; explicit value, never remove |
| Strategy | Effective RollingUpdate, maxSurge/maxUnavailable 25% each | Owned type and bounds; Recreate removes only rollingUpdate after drift validation; switching back writes explicit bounds |
| minReadySeconds | Effective default 0 | Owned scalar; omitted API value compares as 0; explicit writes |
| progressDeadlineSeconds | Effective rollout budget default 600 | Owned scalar; omitted API value compares as 600; explicit writes; this is not a kernel action deadline |
| nodeSelector | Absent/empty means zero owned entries | Own keys in last confirmed projection; remove only formerly owned keys; preserve injected keys; target collision with an injected key refuses, even if equal |
| imagePullSecrets | Absent/empty means zero owned entries | Keyed by name; preserve injected entries/order; remove only formerly owned entries with index/name guards; target collision with an injected name refuses |
| Deployment labels | Owner and managed identity required | Own only two reserved keys; preserve other labels |
| Selector | Exactly the two stable ownership labels | Immutable exact selector; refuse drift, extra selector fields and selector replacement |
| Pod template labels | Same two stable ownership labels | Own only those keys; preserve unrelated labels and annotations |
| Deployment annotations | Stable operation ID and positive target generation | Own only two reserved keys; preserve other annotations; never put these changing values in Pod template |
| env/probes/resources/volumes/mounts/ports/routes/documents | Only absent/empty collections, absent probes/resources | Any requested value refuses, including effective default resources and configuration documents |
| Service/headless/Ingress/other companions | Only effectively disabled; one planned Deployment object | Requested or default-enabled companions refuse; no incidental objects |

The four reserved keys are `haby.dev/owner`, `haby.dev/managed-workload`,
`haby.dev/write-operation`, `haby.dev/target-generation`. Owner, managed identity
and operation ID are caller-supplied nonempty GUIDs encoded lowercase `N` (32 hex
digits). Generation is an invariant positive decimal integer. Labels carry only
owner and managed identity on Deployment, selector and template. Operation and
generation appear only in Deployment annotations. Reserved keys in caller-owned
map inputs refuse; an observed reserved key in the wrong location refuses.

API defaults normalize only the explicitly listed scalars and RollingUpdate
bounds. Missing image/container/ownership/operation markers never become defaults.
Empty and absent owned maps/lists compare equally; duplicate keyed list identities
refuse. Unknown non-owned map entries/container fields remain untouched. Drift in
last confirmed owned fields refuses. Observation requires exact current target
projection and operation marker; injected entries do not become owned evidence.

## Intent, dispatch and observation

The adapter owns versioned closed JSON DTOs with source-generated metadata.
Intent binds exact Environment, Provider/configuration/implementation, component
and Workload, Placement revision, namespace/name, logical owner/managed identity,
target generation, operation ID, target projection and (for update) saved UID plus
last confirmed projection. Secrets, endpoint, certificates, raw responses and
secret-derived hashes are absent. Receipts retain UID, observed Kubernetes
generation, projection and response-versus-observation confirmation provenance.

Create performs one named POST only after a read proves name absence. Occupancy
refuses without adoption. Update requires saved UID, owner and last confirmed
projection, then JSON Patch tests UID, just-read resourceVersion and keyed list
identities before narrow mutations. Missing objects refuse without recreation.
There are at most three conditional dispatches in a ten-second total action
budget, and another dispatch requires a positively identified JSON Patch test
rejection plus a fresh read and complete validation. Generic 409/422 is conflict.
Create never retries. No transport retry handler is installed.

Known cancellation before dispatch performs zero writes. Cancellation, timeout or
transport loss after dispatch is uncertain. A separate read-only operation accepts
the same immutable intent; it never writes or replays. Create observation may learn
UID only from exact owner, operation and projection match. Update observation also
requires the saved UID. Absence, foreign UID, missing/mismatched marker and drift
never confirm. API failure is observation unavailable, never proof of nonexecution.
Original uncertain results remain separate from later observation receipts.

The pinned API returns generic 422 for a failing JSON Patch test, without the
specific cause required to prove nonexecution. Its real RV-race case therefore
returns conflict after one PATCH. A deterministic informative-cause fixture proves
the three-dispatch ceiling and complete fresh-state validation; it is not a claim
that this API exposes that cause. See the pinned upstream
[patch handler](https://github.com/kubernetes/kubernetes/blob/v1.35.0/staging/src/k8s.io/apiserver/pkg/endpoints/handlers/patch.go)
and [status construction](https://github.com/kubernetes/kubernetes/blob/v1.35.0/staging/src/k8s.io/apimachinery/pkg/api/errors/errors.go).

Frozen codecs reject unknown/duplicate JSON properties, noncanonical suffixes,
invalid portable names/images and invalid update pairs. Decode is bounded to
1 MiB; nodeSelector and imagePullSecrets are each bounded to 256 owned entries.
These are temporary kernel limits, not new portable Workload syntax. The caller
owns one-time dispatch authority; an uncertain outcome must be observed, never
recovered by calling Execute again. No stateless API can replace the later durable
intent/dispatch linkage.

## Transport and evidence boundary

Use exactly KubernetesClient 19.0.2 behind this adapter. Explicit HTTPS and either
bearer or mTLS exact SecretRefs resolve through the scoped secret store. No
kubeconfig, ambient context, exec auth, redirect or insecure trust option exists.
Both system and custom-root trust validate hostname, chain, server-auth usage and
Online revocation fail-closed. Missing revocation evidence is a failure, not a
reason to fall back. Disposable CA `NoCheck` is an internal fixture seam, never a
Provider setting. The third-party SDK serialization/reflection remains isolated
from Haby-owned source-generated DTOs and the CLI Native AOT dependency graph.
The SDK's `KubernetesJson` handles its own `V1Deployment` DTOs at the transport
boundary. Haby writes JSON Patch and create projections with JSON DOM nodes and
serializes its intent/receipt DTOs only through `KernelJson` generated metadata.
The adapter is a managed server-side assembly; no SDK AOT qualification is claimed.

Reuse the qualified owned Docker API launcher and cleanup. Agentless API evidence
proves object behavior only, with no Pods, rollout readiness or crash recovery.
Preserve the existing 900 PostgreSQL, 1308 fast and 75-per-platform Native AOT
baseline inventory and report added cases separately. Full hosted validation and
the new adapter real-API cases are required before acceptance.

The checked-in case ledger retains 2358 pre-slice gate rows and adds 73 fast cases
and 24 named API-object scenarios (2455 rows total). Normal obligations remain
900 PostgreSQL, 1381 fast and 75 Native AOT cases per platform. API-object cases
have stable `K01`..`K24` scenario identities; their method plus scenario SHA256
is compared with the same gate ledger before publishing safe cases/gate metadata.
This is a custom API-case inventory, not a TRX execution or a readiness gate.

Run the adapter contour through the existing launcher:

```powershell
dotnet restore tools/Haby.Kubernetes.Qualification --locked-mode
./scripts/run-kubernetes-qualification.ps1 -Runtime docker -DeploymentKernel -EvidencePath .artifacts/deployment-kernel/evidence.txt
```

The hosted `Kubernetes Deployment kernel` workflow executes this mode, checks all
24 scenario identities and confirmed owned container/storage/private cleanup, then
uploads only `evidence.txt`, `cases.json` and `gate.json`. Bootstrap uses the existing
qualified fixture factory. Every adapter action uses the new production factory
with scoped exact SecretRefs; successful private-CA actions use its internal
fixture revocation seam. Separate production-mode cases prove custom-root
revocation refusal and system-trust refusal for that private CA. System-root
Online policy, certificate expiry and wrong EKU also have deterministic controls.
This does not prove successful production access to an offline/private CA or a
publicly trusted cluster. No revocation fallback is configured or promised.
