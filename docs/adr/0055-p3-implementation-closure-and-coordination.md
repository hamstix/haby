# ADR-055: P3 implementation closure and coordinator decision boundaries

- Status: Accepted
- Date: 2026-11-11
- Scope: Remaining P3 semantics, configurable limits, evidence cadence and slice sequence
- Extends: ADR-024/026/030/033/035/046/047/049/050/051/052/053/054

## Context

P3 will be delivered through sequential agent-assisted slices. The coordinator needs
explicit product/security/lifecycle rules without requesting architectural approval
for ordinary implementation details. Close the remaining scope, administration,
publication, load and observation questions before later executable slices.
This ADR refines earlier P3 wording; it does not implement those capabilities or
permit current P2 safety guards to be removed prematurely.

## Decision

### 1. ProviderInstance remains Environment-scoped

Preserve the existing Environment ownership of ProviderInstance. Two Environments
may have different ProviderInstance records pointing to one physical Kubernetes
cluster. They do not acquire shared ownership or credentials merely from the same
endpoint. ADR-047's statement about one Provider serving several Environments means
one external cluster, not a new cross-Environment persisted Provider aggregate.
No global Provider catalog or endpoint-based identity merging is introduced.

### 2. Placement is the namespace authority

The selected exact Placement revision supplies namespace. Environment defaults
select Placement; do not introduce a second competing mutable Environment.namespace.
Common use is one namespace/default Placement per Environment, but exact selected
Placement is authoritative. Moving an already managed Workload to another namespace
remains unsupported relocation under ADR-049.

### 3. Resource-only, Workload-only and mixed Applications

Preserve P2 Resource-only behavior. Support Workload-only Applications by explicitly
completing an empty Resource stage, without a dummy Resource or fake successful
operation. Mixed Applications use the unified staged workflow.

The first executor may retain the current supported PostgreSQL Resource shapes
and one Kubernetes target per Application. The neutral exact resolution set remains
extensible; this is not a promise of arbitrary Resource combinations or multiple
Applications per ChangeSet. Unsupported combinations refuse before desired acceptance.

### 4. Unambiguous Placement selection scope

Named Placement selections and scoped assignments belong to Environment + Kubernetes
Provider alias. Within that scope choose explicit placementRef, else Component
assignment, else Application assignment, else Environment default. Never accidentally
apply another Provider alias's default. Provider compatibility still requires the
exact resolved Provider/configuration/implementation, not a name-prefix heuristic.

### 5. Immutable revisions, conditional selectors

Placement has a stable name and immutable positive revisions. A named selector or
scoped assignment points to an exact revision; mutable selections use expected
absence/current concurrency token and safe atomic update. Absent scope inherits;
there is no disable-Workload meaning attached to missing Placement.

Preparatory import alone switches nothing. Equivalent selection preserves token and
audit. Check stale preconditions before equality. Exact refs and logical Component
assignment validation continue under ADR-047; no implicit latest or orphan fallback.

### 6. Protect referenced administrative revisions

Do not delete exact Placement or Provider revisions needed by accepted history or
unfinished work. Erroneous unused preparatory revisions can be cleaned under the
accepted dependency/closed-Plan retention rules. Import never performs hidden cleanup
or releases ownership. Administrative deletion needs its own explicit checked action.

### 7. Closed configuration selection

Missing Workload.properties.configuration means no Haby runtime retrieval. If present,
documents is required, nonempty and unique. Reject empty object, null, duplicate IDs
and unknown members. Do not create Haby runtime binding/token projection for the
absent case. Ordinary application ServiceAccount needs are a separate explicit concern.

### 8. Registry is required only for composed images

Composed image.name + tag/tagFrom requires Placement registry; repository prefix is
optional. Complete image.reference ignores Placement registry/prefix. No implicit
Docker Hub, missing-tag completion, Liquid or silent image normalization is introduced.
imagePullSecrets references existing Kubernetes Secrets; initial P3 neither creates
nor changes those registry credential objects.

### 9. Explicit Kubernetes credential alternatives

The initial typed access configuration supports mutually exclusive bearer token
(exact SecretRef) or client certificate plus private key (exact SecretRefs). Endpoint
uses HTTPS with system trust or explicitly configured CA. Do not discover kubeconfig,
current kubectl context, cloud exec plugins or silently change trust settings.

Changing credential revision does not dynamically rebind accepted generations.
Automatic administrative credential rotation remains separate work. Keep secret
material in the dedicated secret boundary, not shared Provider columns or ordinary
configuration snapshots. Technical qualification proves the actual chosen client
supports these requirements before API-adapter implementation.

### 10. Side-effect-free Kubernetes preflight

Verify API availability/authentication, selected namespace and required permissions
for requested capabilities without creating a test Deployment, ServiceAccount or
namespace. A successful check is point-in-time evidence, not a future-effect guarantee.
Document permissions per capability; whole-cluster administrator rights are not a
mandatory operational requirement. Typed review/probe mechanisms may be used where
needed, but no application object is deployed as a hidden preflight action.

### 11. Checkpoints carry evidence

Save exact inputs and confirmed result references behind every stage transition.
A bare ResourcesReady boolean does not authorize downstream work. Initially stages
run sequentially within a generation; parallel independent external effects are not
required. Bounded concurrent delivery and observation remain supported separately.

### 12. Durable publication intent and revoke ordering

An explicit publication intent has stable request identity and a durable result.
Replay returns that intent's original result without reactivating later revoked
authority. Republishing after revocation requires a new explicit intent and current
state precondition. Serialize publish/revoke transactionally so no update loses revoke.

Automatic preparation derives stable intent identities from exact Workload/generation/
document context. It must not create a random new publication on every resume.
Keep full history and exact document authority; lifecycle is independent of P2 grants.
Concrete record/receipt encoding follows these invariants rather than inventing a
universal grants framework.

### 13. Explicit revoke can block preparation

If an administrator revokes binding/permission required by an unfinished rollout,
Execute reports missing required runtime authority and does not repair it automatically.
The operator explicitly republishes or corrects desired state. No forced completion
without mandatory configuration or implicit restoration by old workflow replay.

### 14. Operator-configurable mass-start limits

These are initial DEFAULTS, not hard-coded immutable policy:

| Setting | Default |
| --- | --- |
| Concurrent TokenReview calls per Host | 32 |
| Concurrent TokenReview calls per Provider | 16 |
| TokenReview rate per Host | 50 requests/second |
| TokenReview rate per Provider | 25 requests/second |
| Total queued waiting requests per Host | 256 |
| Maximum queue wait | 10 seconds |
| Single TokenReview timeout | 5 seconds |

Apply global and Provider limits together across all runtime requests. No unbounded
per-binding queues. Operators configure values through validated Host configuration;
invalid settings fail validation rather than silently reverting to defaults.
No dynamic administrative settings API is required. Document actual configuration
keys, valid ranges, application/restart semantics and effective non-secret settings
with implementation. Avoid a runtime-reload promise unless explicitly implemented.
Bound request/credential lifetimes, cancellation, memory and limiter burst behavior;
concrete algorithm/bounds must remain documented and tested.

Defaults are a starting admitted profile, not a proven performance SLA. The real
load rehearsal must validate them or supply evidence for a reviewed adjustment.

### 15. One client startup deadline

Initial configurable client defaults are total mandatory-document startup deadline
300 seconds, individual RPC at most 30 seconds and no longer than remaining total
time, exponential backoff with full jitter using an initial 250 ms ceiling and
maximum 5-second ceiling. Fetch one client's documents sequentially initially.

Each retry rereads the projected file. Queue waits, request time and delays count
against the same total deadline; receiving one document does not reset it. No hidden
unbounded task/retry loop or positive TokenReview cache is authorized.

### 16. Typed temporary versus permanent transport outcomes

Use ResourceExhausted for overload, Unavailable for temporary Host/Kubernetes
unavailability, Unauthenticated for invalid identity and PermissionDenied for missing
authority. Add safe reason codes/hints without credential/JWT/driver detail.
Runtime clients retry only explicitly temporary outcomes within the total deadline.
Preserve ambiguity-safe object dispatch rules: delivery retries do not imply external
write retries. Detailed wire mappings must not expose foreign document existence
or bypass authentication/authorization boundaries.

### 17. Load proof and test cadence

The healthy-profile rehearsal starts at least 150 clients simultaneously, each
fetching two mandatory documents. All complete within the configured total startup
deadline; temporary rejections with successful bounded retry are acceptable.
A separate constrained-overload scenario proves queue saturation, cancellation,
deadline expiry and actual API limits without unbounded tasks/memory.

Ordinary PR/commit CI runs fast deterministic correctness tests for limiter/queue,
cancellation, temporary outcomes, backoff/jitter and deadline semantics, alongside
existing applicable full gates. Do not require the full real-cluster load rehearsal
on every commit. Run it manually or periodically from a developer machine/dedicated
runner, obligatorily before acceptance of the P3 runtime-delivery capability and
after material changes to TokenReview, limiters or runtime-client behavior.

Record exact SHA, environment/settings, payload/fan-out, actual TokenReview calls,
latency distribution, retries/rejections, failures and omissions. A green ordinary
CI run does not substitute for missing required load evidence. Do not call client
failures a successful healthy-profile run merely because the server survived.
No existing test or security gate is removed by separating this additional load run.

### 18. Wait and resume have distinct authority

wait observes an already submitted exact generation and may complete readiness/
retirement; it does not begin unsent provisioning or replan. resume may continue
unfinished authorized workflow stages, without changing accepted inputs. Neither
command contacts Kubernetes from CLI; all observation/execution is server-side.

After an external repair a repeated wait may confirm already recovered readiness.
After observation-budget expiry, a fresh long observation budget requires explicit
resume with audit; Host/request restart never silently resets the original deadline.

### 19. Bounded batch wait

Initial batch contract allows at most 200 exact deployment/generation references;
reject duplicates. Return per-member results including timeout/error, plus overall
success only when required members complete. One failed member does not cancel the
others' observation. Bound total deadline, response size and API work.

The coordinator can split larger product releases into batches. No multi-Application
ChangeSet or atomic product rollback is implied. Concrete serialization/result limits
must preserve truthful partial outcomes under the bounded response contract.

### 20. Retirement starts before the first changing action

Persist a durable retirement-start marker BEFORE first DELETE, revoke or other
destructive retirement action. Corrective supersession is then prohibited even if
the first response is lost. Read-only cleanup calculation/observation alone does
not install that marker. Do not reinterpret uncertain deletion as an unstarted stage.

### 21. Delete outcome versus foreign replacement

Observed absence after confirmed deletion of the known UID completes that action.
Another UID under the same name is a foreign replacement/conflict, not a new Haby
object. Never delete/adopt that replacement through cleanup or resume. Preserve
inventory and conflict evidence instead of treating a name match as ownership.

### 22. Technical qualification before API effects

The coordinator may investigate client libraries and disposable cluster runtimes,
but final selection must prove conditional JSON Patch/API support, cancellation,
no hidden retry of uncertain writes, TLS/SecretRef integration, commercial-use
license suitability, reproducible Windows/CI setup and real TokenReview/UID tests.

Record pinned dependencies, supported/tested Kubernetes versions and an executable
isolated runbook. Known package names or a mocked transport are insufficient.
If no suitable option exists, return the concrete technical conflict to the
architecture discussion. No working namespace or persistent customer cluster is a
default test target.

### 23. Coordinator delegation boundaries

Within accepted semantics the coordinator may choose internal file/type names,
EF mappings/generated DTO roots, narrow issue decomposition, fixtures/disposable
infrastructure and documented finite field bounds. Follow AGENTS.md, CONTRIBUTING
and current-alpha constraints. This is implementation discretion, not permission
to bypass independent review, existing gates or explicit Git/merge authorization.

Ownership, authorization, lifecycle, Provider/Placement scopes, replay/uncertainty,
retirement and corrective admission changes require architectural approval. Return
genuine contradictions or unimplementable contracts with evidence, not routine
coding questions. Preserve API-only CLI and Provider-neutral shared models/storage.

### 24. Accepted execution sequence

```text
#177 contracts and pure resolution
  -> Kubernetes technical qualification
  -> Placement persistence and administration
  -> durable multi-Provider inputs and stage checkpoints
  -> conditional Kubernetes create/update and uncertainty
  -> runtime identity/publication/client and mass-start proof
  -> Deployment submission/readiness/batch wait
  -> retirement and removal
  -> corrective generations
  -> complete P3 operator rehearsal
```

Qualification can run alongside contract work; production slices are accepted
sequentially. Each may be decomposed into smaller coherent issues while preserving
dependencies and refusal of unimplemented capabilities. No milestone-sized PR or
premature general SDK/UI/HA work. P3 completion requires real lifecycle evidence;
contract acceptance alone is not execution support.

## Implementation and documentation

Update the affected contracts, program/operator instructions, configuration reference
and reports with these exact scope/permission/default distinctions. No runtime code
or schema changes occur in this decision commit. Current durable Workload guards
remain until implementation slices deliberately satisfy the accepted prerequisites.
