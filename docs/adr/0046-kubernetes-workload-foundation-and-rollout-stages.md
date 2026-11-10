# ADR-046: Kubernetes Workload foundation and staged rollout

- Status: Accepted
- Date: 2026-11-09
- Scope: First P3 Workload decision packet; implementation follows the P2 update checkpoint
- Extends: ADR-009/012/016/018/026/033/035/036/040/041/042/043/044/045

## Context

P2 establishes Resource continuity and exact authorized configuration delivery.
P3 must run and update applications without conflating API submission, runtime
readiness and retirement. The existing materialization path requires an applied
generation, while a new Pod needs its configuration before rollout can complete.
Introduce an explicit staged deployment model rather than weakening that gate.

This is accepted future architecture, not a claim that Workloads are implemented.
Current P2 commands, AppliedGeneration behavior and materialization gates remain
unchanged until dedicated P3 implementation slices deliberately extend them.

## Decision

### Domain responsibility, identity and persistence

Resource and Workload are different domain declarations with different
responsibilities. Both participate in unified deployment planning and share
durable execution infrastructure. Separate persisted models are introduced only
for genuinely different state or invariants, not merely different terminology.
This refines ADR-009's earlier blanket separate-persisted-entity wording.

A Workload is nested within its Component. Its stable managed identity is
Environment + Application + Component local ID + Workload local ID, independent
of ApplicationVersion/image/generation. Moving it to another Component or changing
its local ID is removal plus creation unless a separate explicit migration exists.
RuntimeInstance remains a concrete observed replica, not the Workload declaration
or a required persisted inventory of every Pod.

### Kubernetes first, neutral deployment core

Kubernetes is the first and only supported Workload runtime for this scope.
Its declarations, placement settings and implementation use Kubernetes-specific
contracts. Shared deployment logic and persistence must not depend on Kubernetes
client types or fixed Kubernetes object structure. Adapter evidence uses typed
versioned payloads behind common identities; no Deployment/Service/Ingress columns
in generic tables. Introduce additional runtime abstractions only for real needs.

Keep PostgreSQL as Haby's source of truth. Kubernetes is an integration, not a
mandatory Haby hosting/storage mode. Do not invoke kubectl from habyctl or server
business logic. The CLI remains API-only; use explicit adapter/client composition.
One Haby instance and sequential durable execution suffice initially; no HA,
background reconciliation service or CRD operator is a prerequisite.

### Closed operational Workload contract

Design the initial typed closed contract around Deployment and explicitly selected
companion objects. Cover these known operational requirements, with semantics,
validation and tests; implementation arrives in sequential vertical slices:

- Main container image, ports, environment variables and resource requirements.
- Replicas, RollingUpdate/Recreate strategy and rollout parameters.
- Startup, liveness and readiness probes.
- nodeSelector and imagePullSecrets references.
- Existing ConfigMap, Secret and PVC volume/mount references, including necessary
  optional, readOnly and subPath settings.
- Ordinary Service and headless Service, permitted together.
- An individual Ingress to this Workload's Service, enabled by placement settings.

Do not begin with arbitrary Kubernetes YAML, unconditional PodSpec passthrough,
StatefulSet, Job/CronJob or arbitrary CRDs. The first executable slice may support
Deployment alone; the known contract requirements must not be lost during slicing.
Unsupported fields/shapes refuse rather than disappear.

References to existing ConfigMap, Secret, PVC, imagePullSecrets or routing
infrastructure do not imply ownership or authority to delete them. Shared product
routing is modeled separately, never independently overwritten by every Workload.

### Placement and exact planning inputs

Namespace exists beforehand and is selected administratively; an Environment
normally shares one namespace for its Applications. Do not make namespace create/
delete, ingress-controller installation or cluster-wide infrastructure implicit.

Reusable placement settings provide common values and companion-object selection
with explicit overrides. A typical Development configuration uses Recreate and
individual Ingress; Production uses RollingUpdate and may disable individual
Ingress. These are explicit configuration choices, not behavior inferred from
Environment names. Selection and bounded inheritance/override rules are accepted in [ADR-047](0047-kubernetes-workload-declarations-and-placement.md); concrete JSON contracts remain implementation work.

Plan freezes exact Workload declaration, resolved placement/namespace, Provider
configuration revision and implementation, image reference, configuration document
selection and all supported settings. Alias/placement changes never relocate an
accepted Workload dynamically. Plan creates no external object, runtime grant or
rollout.

Image updates require a new Plan/generation. Permit digest or explicit version tag;
no implicit latest or automatic newest-image selection. A digest identifies content;
a pinned tag string alone does not guarantee immutable image bytes. Automatic tag
resolution/authentication is separate work, not an unimplemented reproducibility
promise.

### Resource readiness, configuration and generation completion

For a Workload generation, AppliedGeneration means all mandatory stages of that
accepted generation completed, including its agreed readiness criterion. Keep
the established Resource-only path for Applications without Workloads.

The intended stage sequence is:

```text
Commit desired generation
  -> Execute Resource operations / validate reuse
  -> Resource stage ready checkpoint
  -> Prepare exact configuration and runtime access
  -> Apply Workload objects
  -> Observe readiness
  -> Advance AppliedGeneration
```

Resource readiness is a persisted checkpoint inside one generation, not a new
generation or proof that the application is running. Define its precise state/
storage contract with the execution slice; do not duplicate domain aggregates
without distinct invariants.

The trusted server deployment path may prepare exact documents/runtime access for
an accepted generation whose required Resource producers are confirmed ready,
before full rollout readiness. Validate exact frozen inputs, producer outputs,
owner/purpose and allowed recipient scope. Implement this as a distinct checked
preparation path, not by dropping AppliedGeneration checks from ordinary operator
materialization. Ordinary omitted-generation selection remains current fully
applied desired state; delivery still requires its independently authorized scope.

### Submit now, observe readiness separately

Support explicit execution that returns after confirmed Kubernetes API submission
without waiting for rollout. A future wait=false option is illustrative syntax,
not a frozen CLI/RPC contract. Persist the submission checkpoint and distinguish
API-accepted from runtime-ready, failure and uncertain submission.

Not waiting does NOT waive readiness or advance AppliedGeneration. A later explicit
wait/resume observes the same exact generation and can finalize it without replaying
Resource provisioning or already confirmed work. No hidden background worker or
automatic rollback is implied. Interruption/response loss does not undo effects.

An automated release may submit several Application updates first and then check
readiness as a separate aggregate CI stage. This does not require an immediate
multi-Application ChangeSet. Each wait targets exact accepted deployments/generations,
not whichever objects happen to exist in a namespace at query time. Batch wait
wire/CLI contracts and result limits need a subsequent packet.

Readiness uses bounded observation and explicit criteria. Image-pull failure,
crash loop, failed probes or timeout cannot be reported as successful rollout.
The previous AppliedGeneration remains the last completed one, even where external
state has partially changed. Probes determine the strength of the readiness signal;
product smoke tests can follow it in CI and are not automatically implied.

### Object ownership, updates and uncertainty

Manage only objects and fields owned under the agreed policy. Preserve external
identity, including Kubernetes UID in adapter-owned evidence. Existing foreign
objects are not replaced/adopted merely because names or labels match.
Object naming, field ownership, conditional updates and response-loss evidence are
accepted in [ADR-049](0049-kubernetes-object-ownership-and-uncertain-writes.md).
Concrete field/metadata/evidence contracts precede the implementation slices.

Kubernetes actions have their own uncertainty rules. After response loss, adapters
may observe and establish the result only with sufficient proof tying the object
to the accepted operation/ownership. Otherwise preserve uncertainty. Do not impose
PostgreSQL CREATE DATABASE recovery mechanics on every API, or turn a read into
implicit adoption authority. No blind replay, fabricated success or general drift
repair claim.

### Credentials and mandatory retirement cleanup

New replicas receive configuration for their exact generation. Old replicas may
still need their previous documents/access during rollout. Do not revoke access
at Commit or after the first new replica becomes ready.

Agree the initial runtime authentication/credential-transfer path in a separate
packet, considering existing static scoped delivery and ADR-012 projected-token/
TokenReview identity. Operator-admin tokens never enter Pods. Durable secure
transfer, restart, lost issuance responses, rotation and revocation are part of
that decision, not incidental Deployment fields. No mechanism is selected here.

Cleanup is required P3 behavior: explicit retirement of owned obsolete Workload
objects and obsolete runtime access at an agreed safe boundary, with partial
failure/retry handling and protection for still-running recipients. Removing a
Component/Workload/Binding from a manifest alone is not proof cleanup occurred.
Do not delete referenced external volumes/secrets or retained databases/roles.
First-slice rollout does not authorize destructive Resource deprovisioning.

## Required evidence and remaining decisions

On a real test cluster prove install v1 -> authorized configuration retrieval and
database use -> v2 image/configuration submission -> readiness wait/CI outcome ->
old replica/access retirement -> explicit Workload removal, preserving data.
Cover Host restart, partial rollout, API response loss, foreign same-name objects,
readiness failure and cleanup failure. API-only Deployment creation is not this
complete acceptance path. Keep real PostgreSQL/auth/native gates and honest source/
CI/independent execution evidence.

Before coding relevant slices, agree JSON/placement inheritance, exact stage and
wait contracts, readiness criteria/timeouts, object naming/field ownership and
uncertainty evidence, runtime access/distribution, retirement/cleanup and bounded
batch observation. This ADR does not complete those contracts, implement Workloads
or bypass the preceding P2 update checkpoint.
