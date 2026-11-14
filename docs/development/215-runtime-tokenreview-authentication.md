# Internal Kubernetes runtime authentication (#215)

This slice composes the persisted stable identity from #211 with online Kubernetes
TokenReview. It returns an internal authenticated context or a safe typed failure.
It opens no endpoint, issues no credential and grants no configuration permission.
Public Workload execution, readiness and AppliedGeneration remain unchanged.

## Trust and lifecycle

The non-secret binding UUID selects saved installation, Environment, ManagedWorkload,
Provider instance, exact configuration revision/implementation, authenticator,
audience and versioned validation context. The original confirmed ServiceAccount
receipt supplies its exact namespace, name and UID. The adapter cross-checks these
fields before administrator material or network access. JWT claims never choose
trust configuration; current aliases and ambient kubeconfig are not consulted.

Authentication validates original identity preparation using the accepted
generation/commit/input fingerprint/checkpoint, exact action intent, terminal
attempt/output or confirmed observation, owned inventory and immutable binding
correspondence. It does not reload configuration document bodies or traverse
recipient-document associations to prove current materializability. There is no
document-store dependency in this identity path and no caller-controlled bypass.
Normal preparation, publication and effects retain their complete configuration
checks. Authentication alone can succeed when stored document content is unusable;
that grants no publication or delivery success.

For mixed applications the origin proof retains generated credential slot/profile,
exact SecretRef version, Environment/Application/Resource owner, purpose/sensitivity,
version/protection-format metadata and original creation-audit consistency.
Its separately named authentication reader never queries application SecretPayloads,
including existence, byte length, hashes or aggregates. Missing or invalid-length
application material does not invalidate an established runtime identity. Normal
credential checks retain their existing payload presence/length and material
validation. Only the Kubernetes adapter resolves exact Provider administrative
credentials for TokenReview; this grants no application-material access.

The pinned KubernetesClient 19.0.2 uses the existing connection factory: explicit
HTTPS, exact Environment-owned ProviderAdministrator SecretRefs, strict hostname,
chain/server-auth validation, Online certificate revocation and no redirects.
Disposable fixture certificates use only the existing friend-only fixture seam;
its private-CA revocation exception is not an operator option.

Each accepted attempt sends one TokenReview with the saved audience. Success
requires authenticated=true, matching explicit response audience, exact authenticated
ServiceAccount username and UID, and singleton valid Pod name/UID extras.
No unsigned token parsing, offline authorization or positive TokenReview cache is
used. Missing, ambiguous or malformed identity data fails closed. The Provider's
API verifies the bound objects; Haby does not query Pod/ReplicaSet/Deployment owner
chains or constrain the requesting Pod's generation. Old matching Pods remain
within the accepted stable-ServiceAccount boundary.

Authentication and publication authorization remain separate. A fresh catalog
snapshot after TokenReview rechecks current binding identity/lifecycle. A binding
revoke completed before this check denies success. Authentication returns no
document ID, document body or permission. Later delivery must separately check
fresh exact #213 publication authority before materialization. It cannot turn this
authenticated context into a positive permission cache. Already authorized delivery
work retains ADR-050's in-flight boundary; this slice implements no delivery.

## Host settings

Keys have prefix `Haby:RuntimeAuthentication:`. For environment variables use the
usual double-underscore configuration separators. Values are integers, parsed and
validated when constructing the Host. Invalid values fail configuration. Settings
are a startup snapshot; restart to apply changes. There is no reload promise.

| Key | Default | Inclusive range |
| --- | ---: | --- |
| GlobalConcurrency | 32 | 1..256 |
| ProviderConcurrency | 16 | 1..GlobalConcurrency |
| GlobalRequestsPerSecond | 50 | 1..10000 |
| ProviderRequestsPerSecond | 25 | 1..GlobalRequestsPerSecond |
| QueueCapacity | 256 | 0..4096 |
| QueueWaitSeconds | 10 | 1..60 |
| CallTimeoutSeconds | 5 | 1..30 |

One singleton scheduler per Host applies both global and per-Provider limits across
all bindings and revisions. Provider means the saved instance UUID, not endpoint,
revision or binding. Each scope has a continuously refilled token bucket with
capacity equal to its requests-per-second setting, initially full. This permits
an initial burst up to that capacity; it is not a fixed-window rate guarantee.
Both buckets are charged atomically once at dispatch. Attempts do not refund rate
tokens on failure. No application retry is installed.

The single total queue includes work waiting to look up binding metadata. Metadata
lookups share global concurrency capacity. Waiting on a Provider releases the
global permit; the oldest eligible waiter progresses, allowing other Providers
to use remaining global capacity. QueueCapacity=0 allows only immediate admission.
Queue wait covers lookup and admission, without restarting its deadline between
stages. The call deadline covers connection/administrator resolution, one API call
and the final current-binding check. Cancellation propagates through each stage;
permits are released on every completion/failure/cancellation path.

At most GlobalConcurrency + QueueCapacity request lifetimes are retained, including
expired waiters until their owned credential buffers are cleared and disposed.
Each ASCII credential is at most 16,384 bytes; the default owned-buffer bound is
4.5 MiB (68 MiB at maximum settings). SDK serialization adds temporary bounded
copies; managed strings cannot be zeroed. Haby clears its owned byte buffers and
does not retain request/response/token objects after the call. Tokens never appear
in result DTOs, record ToString output, saved records or ordinary diagnostics.

Adapter validation context is capped at 16 KiB. TokenReview HTTP response bodies,
including non-success responses and bodies without Content-Length, are capped at
64 KiB before SDK buffering/deserialization. The original saved receipt and Provider
configuration retain their existing bounded codec formats.

Provider limiter state is capped at 1024 entries per Host. Entries with queued or
active work cannot be evicted. Idle entries can be evicted only after their rate
bucket has fully refilled, preventing churn from resetting spent Provider budgets.
If no entry is eligible, new Provider admission returns overload. One scheduler
timer drives refill/wakeups; there are no unbounded per-binding queues or idle
per-Provider timer loops. Host restart resets transient limiter state.

## Safe results and permissions

| Internal category | Meaning / later wire category |
| --- | --- |
| Authenticated | Checked binding context only; no document authority |
| Unauthenticated | Unknown binding, invalid/foreign identity, corrupt or unsupported saved format |
| PermissionDenied | Known revoked binding or denied final current-state check |
| ResourceExhausted | Admission/state/queue capacity or queue deadline |
| Unavailable | Host/storage/API/administrator access failure, call deadline or indeterminate TokenReview |

Caller cancellation propagates as cancellation. Raw API errors, bodies, driver
exceptions, JWTs and administrator material are never returned in these results.
Unknown bindings and direct foreign-identity mismatches share the opaque
Unauthenticated outcome. No result identifies a configuration document's existence.

Kubernetes copies authenticator errors into an unstructured `status.error`, including
some invalid/expired/deleted-identity cases and indeterminate server-side checks.
Haby never parses, matches, returns, logs or persists that text. A nonempty error
always refuses as Unavailable with the fixed `runtime-authentication-indeterminate`
code; this proves neither credential validity nor a transient cause. A clean
authenticated=false response is Unauthenticated. A success response with error or
missing/contradictory identity is never usable. Real negative cases record their
actual category and confirmed API response separately from the known invalid input;
controlled response tests explicitly cover clean negative and error branches.
Later clients may retry Unavailable only within the same finite startup deadline,
with backoff/jitter and token-file reread. No client retry loop exists in this slice.

The configured Haby administrative principal requires cluster-scoped `create` on
`tokenreviews.authentication.k8s.io`; the separate existing preparation path has
its own documented permissions. This slice does not create RBAC objects or grant
application ServiceAccounts API permissions. Operator-admin credentials never
enter Pods. Anyone able to create Pods under the trusted ServiceAccount or execute
inside those Pods can exercise this stable runtime identity. This is the accepted
service-account-bound mode, not managed-generation isolation.

## Validation boundaries

| Acceptance | Source and evidence location |
| --- | --- |
| A1 persisted real positive | `PostgreSqlRuntimeIdentityStore`, `RuntimeAuthenticationCases` initial/mixed R01 |
| A2 exact identity and unsupported/malformed refusal | `KubernetesRuntimeAuthenticator`, adapter controlled-response tests, real R03-R06/R10-R12/R15 |
| A3 current lifecycle and no publication bypass | fresh store snapshot, PostgreSQL in-flight revoke and mixed no-document/application-payload query oracles, real R07-R09 |
| A4 independent credentials and actual invalidation | real R02/R13/R14; API-only fixture limitations below |
| A5 combined limits and finite state | `RuntimeAuthenticationAdmission`, deterministic application tests and Host option tests |
| A6 safe failures, bytes and cancellation | request ownership/orchestration, bounded HTTP handler, adapter and deadline tests |
| A7 unchanged mandatory gates and ownership | existing Build and Kubernetes workflows, exact case ledger and launcher cleanup |

The ledger adds 52 fast rows, 25 PostgreSQL/Host rows and 28 API assertion rows in
the existing durable-actions job. API rows distinguish initial, mixed and replaced
identity scenarios; the existing 36 durable rows remain required. Ledger entries
declare required execution; they are not proof that a run passed.

Fast tests distinguish controlled authenticated-response faults from real API
evidence. PostgreSQL cases use separate contexts for arrangement, authentication
and verification, including final revocation and restart. Real acceptance extends
the existing qualified launcher and durable #208/#211/#213 preparation contour;
hand-seeded identity is not the only positive proof.

The pinned disposable k3s v1.35.0+k3s1 profile runs an API server without a kubelet.
Real Pod objects, Pod-bound TokenRequests and independent replacement credentials
prove API validation and fresh calls under one binding. They do not prove automatic
projected-file rotation or workload startup. Kubernetes deletion validation can
include leeway; record actual observed invalidation timing, without promising an
immediate deletion guarantee.

Mandatory solution, both full PostgreSQL suites, both published Native AOT targets,
qualification/kernel/durable, formatting and diff gates remain required. The
separate rehearsal with at least 150 simultaneous clients and two documents each
is still required before later runtime-delivery capability acceptance. This slice
does not claim that proof, deliver documents, inject projected volumes into managed
Deployments, change static P2 grants, implement #209, or change AppliedGeneration.

Sources: [ADR-050](../adr/0050-kubernetes-runtime-identity-and-configuration-publication.md),
[ADR-055](../adr/0055-p3-implementation-closure-and-coordination.md),
[pinned API validation](https://github.com/kubernetes/kubernetes/blob/v1.35.0/pkg/serviceaccount/claims.go),
[authenticated Pod extras](https://github.com/kubernetes/kubernetes/blob/v1.35.0/staging/src/k8s.io/apiserver/pkg/authentication/serviceaccount/util.go),
[client qualification](179-kubernetes-qualification.md).
