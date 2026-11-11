# ADR-047: Kubernetes Workload declarations and Placement resolution

- Status: Accepted
- Date: 2026-11-10
- Scope: Second P3 Workload decision packet; implementation follows dedicated slices
- Extends: ADR-018/033/035/039/045/046

## Context

ADR-046 establishes Kubernetes as the first and only supported Workload runtime,
with unified durable deployment execution and Provider-neutral core/storage.
Applications need reusable operational defaults without duplicating settings in
hundreds of manifests. Placement selection must remain distinct from application
configuration composition and resolve to reproducible accepted generation inputs.

This ADR accepts architecture, not implemented JSON schemas, operator commands or
Workload execution. Existing P2 behavior remains unchanged until explicit slices
update the relevant schema, DTOs, normalized model, snapshot codec and consumers.

## Decision

### 1. Separate declaration, Placement and Configuration Profile

A Workload declares the application runtime requirements: image, container ports,
probes and required mounts. Placement defines operational settings such as
namespace, rollout strategy, replicas and companion-object selection.
Configuration Profiles compose application configuration documents. Placement is
not a Configuration Profile and does not share its contract or lifecycle merely
because both support reusable settings.

### 2. Provider access and Environment placement

A Kubernetes Provider configuration defines cluster access and exact credential
references. Namespace and Deployment settings belong to Placement, not Provider
identity. Under [ADR-055](0055-p3-implementation-closure-and-coordination.md),
ProviderInstance stays Environment-scoped; distinct records may access the same
external cluster with different namespaces. No global Provider aggregate is implied.
Provider-specific settings use typed versioned payloads; shared deployment logic
and persistence acquire no Kubernetes client types or Kubernetes-specific columns.

Environment + Kubernetes Provider alias scopes the exact default Placement and
named/scoped selections under ADR-055. Namespace comes from that selected Placement,
not a competing mutable Environment field. Applications inherit the default without
individual assignments. Selectors and bulk assignment rules are deferred.
The existing mandatory Workload.providerRef continues to select a Provider alias;
planning verifies that resolved placement is compatible with that Provider and
Environment namespace. Placement selection is not implicit Provider rebinding.

### 3. Named immutable Placement revisions

Placement is a named entity with immutable revisions, for example
standard-runtime revision 3. Editing creates a new revision. Assignments select
an exact revision; previously accepted Plans and generations keep their exact
resolved inputs. Kubernetes-specific contents remain a typed versioned payload
behind shared identities, without fixed Deployment/Service/Ingress columns.

### 4. Scoped selection chooses a complete revision

Within Environment + Kubernetes Provider alias, without an explicit Workload
placementRef, selection is:

```text
Component assignment
  -> otherwise Application assignment
  -> otherwise Environment default
```

Each scope selects one complete Placement revision, not another partial JSON
merge layer. Environment must exist. Application assignment requires an imported
Application, not a deployed instance. Component assignment requires an explicit
ApplicationVersion and server verification that the Component exists in that
accepted ManifestRevision. The version is validation context, not assignment
identity. Assignments remain logical Application/Component bindings; absent
Components do not participate, and renames do not transfer assignments.

### 5. Replace the ambiguous Workload profile selector

In the current evolving alpha, replace Workload.profile with optional
Workload.placementRef: the name of an Environment-local Placement assignment.
An explicit name selects that assignment instead of scoped fallback; an unknown
or incompatible selection refuses planning rather than silently falling back.
Planning resolves it to an exact Placement revision. A manifest contains neither
placement credentials nor internal Placement UUIDs or an Environment namespace.

Under ADR-033, do not retain the superseded profile field solely for intermediate
alpha compatibility. The implementation slice must update schema, DTO,
normalization, snapshot codec, diagnostics and tests together, documenting any
fresh-catalog requirement. This documentation commit does not change the running
parser or claim that placementRef is accepted today.

### 6. Bounded Workload overrides

Allow explicit Workload overrides for replicas, strategy and RollingUpdate
parameters, rollout timeout, resource requests/limits, and enablement of ordinary
Service, headless Service and individual Ingress. Namespace and Provider are not
overrides. Moving between them is a separate relocation capability unsupported
by the initial P3 implementation.

Inheritance is explicit, never a general JSON merge:

- An absent field inherits its value.
- Explicit null and unknown fields are invalid.
- A scalar or array replaces the inherited value completely.
- A composite object replaces the inherited object completely unless that field
  has an explicitly defined alternative rule in its contract.
- Disabling uses an explicit value such as enabled: false, never null or a
  special interpretation of an empty object.

Plan stores completely resolved effective values and exact selected inputs, with
no remaining inherit-at-execution placeholders. Concrete schemas, field names,
per-field defaults and limits must be specified before implementing the contract.

[ADR-054](0054-p3-workload-contracts-and-typed-images.md) accepts the concrete
entry packet: typed image.name/tag/reference forms, Placement registry/prefix,
independent containerName default app, initial defaults and first pure-planning slice.
Image identity never determines Kubernetes container.name or object names.

### 7. Application-owned container interface

The Workload declares image, named container ports, environment variables,
startup/liveness/readiness probes and required volume/mount references.
Placement cannot replace the image or application environment variables.
Requests/limits are operational defaults with the bounded overrides above.

Initially support one main container. Sidecars and init containers require a
separate extension. Preserve ADR-046's nodeSelector, imagePullSecrets and existing
ConfigMap/Secret/PVC mount requirements; specify their exact fields and override
semantics in the concrete contract rather than inventing generic PodSpec merging.
No arbitrary Kubernetes YAML or unconditional PodSpec passthrough is introduced.

### 8. Explicit environment-variable sources

Initially support an ordinary string, a key reference to an existing Kubernetes
ConfigMap, or a key reference to an existing Kubernetes Secret. Referencing an
object does not confer ownership or authority to delete it. Do not initially
import entire ConfigMaps/Secrets through envFrom.

Haby configuration delivery addresses and runtime credentials require the separate
runtime-access/distribution packet. These ordinary environment-variable fields do
not authorize operator credentials in Pods or unresolved secret material in Plans.

### 9. Services use declared named ports

Ordinary Service and headless Service are enabled independently and may coexist.
Their targetPort references a named container port declared by the Workload;
a missing target is a planning error. The initial ordinary Service is ClusterIP.
NodePort and LoadBalancer require demonstrated needs and explicit contracts.

### 10. Individual Ingress combines routes and operational policy

Workload declares routes to its Service ports. Placement defines enablement,
ingress class, domain policy and references to existing TLS Secrets. If enabled
Ingress cannot resolve an unambiguous route, planning refuses it. Shared product
routing remains separate; a Workload cannot overwrite another owner's routes.
Concrete domain expansion, routing fields and TLS selection need closed schemas
before implementation; this ADR does not authorize arbitrary annotations.

### 11. Replicas and rollout strategy

Replicas are positive integers initially. Zero is not an implicit suspend command.
Placement specifies strategy explicitly; no behavior is inferred from Environment
names. Development may select Recreate and Production RollingUpdate through their
chosen Placement revisions.

Recreate forbids RollingUpdate parameters. RollingUpdate explicitly defines
maxSurge and maxUnavailable; both cannot be zero. Their precise accepted numeric
or percentage forms, bounds and rollout timeout defaults belong to the concrete
contract. Submission without waiting still does not waive readiness or advance
AppliedGeneration, as required by ADR-046.

### 12. Placement changes require a new accepted generation

Editing Placement or assignments alone changes no accepted desired state.
Applying changed effective Workload inputs requires a new Plan and Commit and
advances generation even when ManifestRevision is unchanged. This supports
replicas, strategy and resource-setting changes without a new ApplicationVersion.
Exact selected revision/input identity remains part of planning; never dynamically
resolve current Placement during execution or rewrite historical inputs.

### 13. Reject unsupported capabilities before Commit

Import validates the declaration against the registered contract. Planning checks
the selected implementation and requested capabilities. Unsupported fields or
shapes are rejected, never silently omitted. A first executor may support only
Deployment; a requested unsupported Ingress must then fail planning before Commit.

## Required evidence and subsequent decisions

Implementation slices must prove scoped fallback and explicit selection, immutable
revision pinning, Component validation against an explicit version, complete-value
override behavior, unknown/null rejection, Provider/namespace compatibility,
missing ports/routes and unsupported capabilities. Prove that assignment edits do
not mutate accepted generations, and that explicit replan/commit freezes the new
settings while previous generations retain their original inputs.

Keep source-generated serialization, exact snapshot round trips, Provider-neutral
persistence, generated operator clients and Native AOT validation. Do not add
compatibility adapters solely for superseded alpha models or silently reset data.

Before executing Workloads, agree Kubernetes object naming/field ownership,
conflicts and action-specific uncertain-result evidence, then runtime configuration
access/distribution, readiness/wait and mandatory retirement cleanup. Detailed
closed JSON schemas, Placement administration/CAS and concrete numeric/default
limits require implementation-ready decisions; this ADR does not authorize an
unbounded settings framework or replace those remaining contracts.
