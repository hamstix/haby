# ADR-050: Kubernetes Runtime Identity and exact configuration publication

- Status: Accepted
- Date: 2026-11-10
- Scope: Fourth P3 Workload decision packet; design acceptance, not implementation
- Extends: ADR-006/012/026/032/033/035/041/042/043/046/047/049

## Context

Managed Kubernetes Pods need exact authorized startup configuration before rollout
can become ready. P2 already provides immutable documents, scoped static delivery
grants and transient secret materialization. P3 uses projected Runtime Identity
without adding storage/transfer of static bearer credentials into Kubernetes.
A stable ServiceAccount is the Workload boundary, not a per-generation identity.
Bounded mass-start protection is required; separate load qualification is deferred
until after P4 as specified in section 14.

This ADR refines ADR-012's older milestone scheduling and separates binding
identity from per-document publication. It does not implement Workloads, Runtime
Identity, load controls or new delivery RPCs merely by accepting their design.

## Decision

### 1. Projected Runtime Identity for managed Kubernetes Workloads

Use projected ServiceAccount token -> TokenReview -> RuntimeIdentityBinding ->
exact-document authorization -> secret resolution/deferred formatting -> JSON.
Preserve P2 static delivery grants for existing external/manual clients. Do not
initially distribute static delivery bearer credentials in Kubernetes Secrets.
Operator-admin credentials never enter Pods or grant delivery authority.

### 2. One independent delivery request

Extend the separate generated Configuration Delivery contract, not the operator
service. A request supplies a non-secret runtime binding ID, exact
ConfigurationDocumentRevisionId and projected credential. Internal authentication,
mapping, authorization and materialization remain separate stages. No login,
token exchange, implicit latest or caller-selected trust endpoint is introduced.

### 3. Stable immutable binding identity

RuntimeIdentityBinding fixes Environment, ManagedWorkload, Kubernetes Provider,
authenticator, audience, namespace, ServiceAccount UID and service-account-bound
mode, with lifecycle status. It is not tied to one Application generation.
Changing those trusted identity constraints creates a new binding; ordinary image,
configuration or replica updates do not replace the binding. New/old bindings may
coexist for an explicit identity transition; retirement is separately governed.

### 4. One ServiceAccount per ManagedWorkload

Use a stable Haby-owned ServiceAccount named <baseName>-runtime, applying the
ADR-049 baseName/nameOverride rules and validating the additional suffix. All
replicas and later generations of that ManagedWorkload share its UID. Track it
in the owned object inventory, with no foreign same-name adoption.

An unexpected replacement UID requires explicit binding recovery/transition, not
silent remapping. The deletion/missing-object rules from ADR-049 still apply.
Do not create per-generation ServiceAccounts for ordinary updates.

### 5. service-account-bound trust boundary

Require successful TokenReview, exact Provider/audience, namespace and expected
ServiceAccount UID, and verified Pod-bound identity. Reject ServiceAccount-only
tokens lacking Pod binding. Do not initially verify Pod -> ReplicaSet -> Deployment
ownership or identify the requesting Pod's Haby generation.

A matching old Pod may obtain a new explicitly published document for its Workload.
This is intentional, not a generation-isolation guarantee. Principals able to
create Pods under that ServiceAccount or execute within them can exercise its
runtime access. A stricter managed-object/generation-bound mode remains future work.
RuntimeInstance remains an observed instance, not a required persisted Pod inventory.

### 6. Explicit exact-document publication

A separate runtime permission links a binding to an exact document revision.
Materialization alone never grants access. Verify the document belongs to the
Workload's Component and its accepted explicit document selection; knowledge of a
UUID cannot publish a foreign document. Server deployment preparation is a trusted,
explicit publication workflow, not an unrestricted materialization bypass.

Multiple exact revisions may be published at once. New publication neither changes
the old revision nor automatically switches a client. Clients always request exact
IDs. Old Pods may request new published IDs when they know them; no latest lookup,
automatic discovery or hot reload is implied.

### 7. Runtime permissions are independent from static grants

Provide distinct publication/read/revoke semantics. A runtime permission creates
no static bearer token and does not modify P2 grants. Revoking one permission blocks
subsequent requests for that revision through that binding; revoking the binding
blocks all its runtime permissions. Already loaded application memory is not erased.
An already authorized in-flight request may finish. Test that concurrency boundary
and require fresh authorization for later requests; no positive permission cache
may silently restore revoked authority.

### 8. Rotated file credential, explicit audience

Mount a read-only projected token volume; expose the file path rather than token
contents in environment variables. Reread the file before every request/retry.
Do not use subPath for that token mount. Request expirationSeconds=3600, without
promising Kubernetes issues exactly that lifetime. Kubelet performs issuance and
rotation; Haby never stores or recovers the raw projected token.
See [ServiceAccount projection](https://kubernetes.io/docs/tasks/configure-pod-container/configure-service-account/#serviceaccount-token-volume-projection).

Preserve the stable audience from ADR-012:

```text
urn:haby:<installation-id>:<provider-instance-id>
```

Persist installation ID in PostgreSQL. In-place restore preserves it; an
independently active clone requires an explicit identity/binding rollout procedure.
Provider ID is immutable. Do not infer installation identity from Host address,
namespace, container restart or Environment display name. Explain these mechanisms
in program/operator documentation, including backup versus active-clone behavior.
The binding ID routes to persisted trusted validation context; token claims do not
choose arbitrary credentials, API endpoints or audiences.

### 9. No implicit application Kubernetes privileges

A runtime ServiceAccount needs no RoleBinding or Secret-read privileges merely to
retrieve configuration. Disable ordinary API-token automount and add only the
explicit Haby-audience projection. Application Kubernetes API access requires
separate explicit rights. Haby's own configured administrative credentials perform
TokenReview and manage objects, with explicit required permissions documented.

### 10. Prepare before submission, without waiving readiness

The intended durable workflow is:

```text
Resource stage ready
  -> materialize exact documents
  -> confirm stable ServiceAccount and RuntimeIdentityBinding
  -> explicitly publish exact runtime permissions
  -> submit Deployment with exact document IDs
  -> application fetches configuration
  -> observe readiness
```

Publish is a separate saved step, not an automatic side effect of import, Plan or
ordinary document materialization. The trusted accepted-generation preparation
path validates frozen inputs, confirmed producer evidence, owner/purpose and
recipient scope under ADR-046. Ordinary operator materialization retains its
existing fully-applied eligibility checks. Submission is not AppliedGeneration.

### 11. Durable idempotency and explicit revocation

For the same accepted Workload/generation, repeated preparation returns the same
exact documents and confirmed stable identity when inputs match. Repeated
publication of a binding/revision pair is idempotent within its explicit publication
intent. Execute/retry/restart never reactivates a revoked permission; reopening
requires a new explicit publication intent. Define conditional publication/revoke
and durable receipt semantics before implementing the relevant slice.

Do not issue new identities because a response was lost. Observe uncertain
Kubernetes writes under ADR-049. Binding/publication metadata is non-secret;
projected bearer material is transient and never part of a saved operation result.

### 12. Non-secret Pod bootstrap

Provide delivery endpoint reachable from Pods, binding ID, exact selected document
revision IDs, projected token path and TLS trust settings. Placement supplies
endpoint/trust; Workload explicitly selects Component-local document IDs.
A loopback Host address is not an automatically usable in-cluster default.

No completed connection string, password, operator credential or projected token
is written to Plan, ordinary ConfigMap, Deployment metadata, preview or logs.
Published runtime authority still follows the exact frozen document/producer/secret
owner/purpose allow-list; it is not arbitrary SecretStore read permission.

### 13. Application startup client

Initially the application directly uses a generated delivery client and starts
primary work only after receiving every mandatory document. Each document is a
separate authorized exact request. Init containers, sidecars, disk persistence,
offline snapshots, discovery and automatic hot reload are separate capabilities.

### 14. Fail closed with bounded mass-start handling

**User-approved scheduling supersession, 2026-11-14 ([issue #217](https://github.com/hamstix/haby/issues/217)):** the separate real-cluster
mass-start rehearsal (at least 150 concurrent clients, two mandatory documents
each) is deferred until after P4. It is not a gate for P3 completion or current
runtime-delivery acceptance. Configurable protection limits and ordinary
correctness/security tests remain mandatory. Until the rehearsal is performed,
capacity at that load is unverified and must not be advertised as demonstrated.

No positive TokenReview cache or last-known-good configuration fallback initially.
Invalid identity, audience, revoked binding/permission or wrong scope fails without
configuration. Temporary Host/Kubernetes failure can be retried within a total
startup deadline; already running processes keep their loaded configuration.
Use TokenReview rather than offline-only JWT validation to check current bound
identity: [ServiceAccount validation](https://kubernetes.io/docs/reference/access-authn-authz/service-accounts-admin/).

Mass-start protection remains mandatory. Haby limits TokenReview concurrency and rate, uses a
bounded queue and returns controlled temporary overload outcomes. Absence of a
positive cache does not authorize unbounded Kubernetes API calls, tasks, memory
or credential retention. Waiting work observes cancellation/deadlines and disposes
transient credentials when it leaves the bounded request lifetime.

Runtime clients retry temporary failures with backoff AND jitter within one total
startup deadline covering requests, queue waits and delays. Permanent authorization
failure is not retried as if it were overload. No hidden unbounded retry loop.
Concrete limit scopes/defaults, queue/byte capacities, temporary error codes and
retry hints require an explicit executable contract and operator documentation.

The deferred post-P4 real load rehearsal must start at least 150 clients
simultaneously, each fetching two mandatory documents. Assert
successful authorized retrieval within the configured startup deadline under the
admitted healthy profile, bounded queue/concurrency/request rate, actual token
rotation behavior in its separate scenario and controlled overload/cancellation
under constrained limits. Record parameters, payload count, request fan-out,
latency distribution, retries/rejections and actual Kubernetes TokenReview calls.
Do not replace this with mocked TokenReview or call a run with client failures
successful merely because the server stayed alive.

[ADR-055](0055-p3-implementation-closure-and-coordination.md) sets initial operator-
configurable Host/client limits and exact permanent/temporary transport categories.
Values are validated configuration defaults, not immutable constants; no dynamic
settings API is required. Its >=150-client healthy rehearsal requests two documents
per client. Ordinary CI retains fast deterministic correctness checks and existing
gates. After P4, full real-cluster load runs are manual/periodic and are repeated
after material TokenReview/limiter/client changes. Passing ordinary CI never
substitutes for the deferred load evidence. Stable publication intents and
conditional revoke/republish semantics are also refined there.

### 15. Stable identity through rollout and retirement

An Application update keeps its ServiceAccount and binding. Old and new document
permissions may coexist. Explicit retirement decides when old permissions can be
revoked; neither Commit nor first-ready Pod is sufficient. Final Workload removal
separately revokes binding and removes the owned ServiceAccount at a safe boundary.
Exact readiness/wait/cleanup rules are the next decision packet. Do not revoke
static P2 grants as an incidental runtime-publication cleanup effect.

## Required evidence and implementation boundaries

Prove v1/v2 keep ServiceAccount UID/binding, materialized-but-unpublished refusal,
old/new Pods fetching a new published exact revision, old request stability,
foreign identity/audience/non-Pod refusal, revoke versus Execute replay, token
rotation, Pod/Host restart and operator/static-grant separation on a real cluster.
The >=150-client/two-document load rehearsal above remains pending until after P4,
with honest exact-source and independent versus CI evidence; it does not gate P3
completion or current runtime-delivery acceptance. Ordinary real-cluster identity,
rotation, revocation and API correctness evidence remains required. No
Workload/generation-bound isolation claim.

Shared authentication/mapping/permission/persistence contracts remain neutral;
Kubernetes TokenReview/client types and object interpretation remain adapter-owned.
Use source-generated serialization, C#/EF transactions, current-alpha snapshot
rules, safe diagnostics and existing PostgreSQL/Native AOT/authentication gates.
Detailed RPCs, publication preconditions, load defaults and failure codes need
implementation-ready slices; they are not implied to exist today.
