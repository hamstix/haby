# ADR-054: P3 Workload contracts, typed images and first planning slice

- Status: Accepted
- Date: 2026-11-11
- Scope: Implementation entry packet for P3; contracts and pure resolution first
- Implementation: Planned in [issue #177](https://github.com/hamstix/haby/issues/177); no runtime change in this decision commit
- Extends: ADR-018/026/033/035/046/047/049/050/051/052/053

## Context

The P3 foundation, placement, object ownership, runtime identity, waiting,
retirement and forward-correction policies are accepted. The first implementation
must establish closed contracts and exact inputs without mixing all runtime stages
into one PR. The current durable pilot pins one Resource Provider and rejects
Workloads; PostgreSQL Resources and Kubernetes Workloads require distinct exact
resolutions, not an arbitrary first alias or implicit replacement of one Provider.

This packet accepts the implementation shape and initial defaults. Exact closed
JSON field matrices and engineering bounds accompany the first contract slice.
It does not claim Workloads, Placement administration or Kubernetes effects exist.

## Decision

### 1. First executable vertical target

The initial vertical path is existing PostgreSQL Resources -> exact configuration
-> Runtime Identity/publication -> Kubernetes Deployment -> wait/readiness.
Service, headless Service, Ingress and retirement arrive through subsequent slices
without losing the accepted requirements. A slice without an executable capability
must refuse that capability in durable planning, not silently omit it.

### 2. One current closed contract, sequential implementation

Document the full initial Workload/Placement field matrix, defaults, bounds and
inheritance before implementing its fields. Distinguish accepted contract features
from implemented executor features. Do not introduce throwaway alpha variants to
avoid the known probes, mounts, services and ingress requirements. Unknown fields
and unsupported shapes refuse; there is no arbitrary PodSpec/YAML passthrough.

### 3. Existing exact Workload type

Use kubernetes.workload/v1alpha1, initially describing Deployment. Additional
execution kinds need explicit contracts; do not accept an arbitrary Kubernetes kind.
In current Application v1alpha1 rename Workload.profile to optional placementRef.
Absence uses the scoped Placement selection under ADR-047. Retain no obsolete profile
alias solely for intermediate alpha compatibility; update schema/DTO/normalized model,
equality/diff, snapshot codecs and diagnostic descriptors coherently under ADR-033.

### 4. Kubernetes-specific properties

```json
{
  "type": "kubernetes.workload/v1alpha1",
  "providerRef": "kubernetes.primary",
  "placementRef": "standard",
  "properties": {
    "containerName": "orders-service",
    "image": {
      "name": "orders",
      "tagFrom": "applicationVersion"
    },
    "configuration": { "documents": ["settings"] },
    "overrides": { "replicas": 2 }
  }
}
```

containerName is optional; nameOverride, container settings and overrides likewise
belong inside Kubernetes-specific properties, not new fixed fields/columns of the
shared Workload model. Structured extension properties retain the existing neutral
boundary; the adapter supplies closed typed interpretation and validation.

### 5. Explicit image.name and mutually exclusive image forms

image.name is statically declared in the manifest and is never derived from
Component, Workload, Deployment or Kubernetes container.name. Rename the proposed
image.repository field to image.name; no compatibility spelling is required.

The composed form uses image.name plus exactly one of:

- tagFrom: applicationVersion, meaning the exact ApplicationVersion of the selected
  revision, NEVER the manifest apiVersion or an inferred latest release;
- a statically declared tag.

Placement supplies registry address and optional repository prefix, separately
from existing imagePullSecrets references. For example, name orders, registry
registry.customer.local, prefix product and ApplicationVersion 1.4.2 resolve to:

```text
registry.customer.local/product/orders:1.4.2
```

Alternatively image.reference supplies a complete explicit image reference,
including a digest reference when desired. It is mutually exclusive with the
composed name/tag/tagFrom fields. Complete references are not prepended with the
Placement registry/prefix. Resolve and validate the final reference during planning,
then freeze it. Invalid ApplicationVersion-as-tag or invalid image syntax refuses;
no silent conversion, implicit latest, Liquid, generic templating or registry lookup.
A fixed tag string does not promise immutable image bytes under ADR-046.

### 6. Stable explicit Kubernetes containerName

Default containerName is app. An explicit value preserves existing installation
container names and is independent of all image fields and Placement selection.
Do not derive it from image.name, Component/Workload IDs or Deployment name.

All main-container settings refer to that named element, never the first array
position. Validate and freeze the name during planning. Image/configuration updates
preserve it. Initially refuse changing the main container name after management
begins; it is not silently remove/add under an image update. Preserve unrelated
external injections under ADR-049. Selecting an existing name grants no adoption
of an unmanaged Deployment. Only one user-declared main container is initially
supported; user sidecars/init containers remain separate extensions.

### 7. Placement content and initial defaults

Select a full immutable Placement revision for namespace, operational defaults,
registry/prefix, rollout, resources and companion-object selection. Match it to
Workload.providerRef; no Provider relocation through overrides. Missing explicit
selection plus missing Environment default refuses, without an implicit namespace
default. Application/Component selection remains whole-revision scoped precedence.

Initial defaults are:

- replicas: 1;
- strategy: RollingUpdate;
- RollingUpdate maxSurge: 25%, maxUnavailable: 25%;
- minReadySeconds: 0;
- rollout observation budget: 600 seconds;
- ordinary Service, headless Service and individual Ingress: disabled;
- requests/limits and probes: absent unless supplied.

Development Recreate is explicit Placement configuration, not Environment-name
inference. Recreate forbids RollingUpdate settings and maxSurge/maxUnavailable
cannot both be zero. Define exact JSON union forms, numeric ranges and timeout
semantics in the contract slice rather than relying on accidental SDK defaults.
The rollout observation budget is not client startup deadline or Kubernetes API
request timeout. Detailed retry/load limits remain governed by ADR-050/051.

### 8. Main-container and reference requirements remain

Preserve ADR-046/047 requirements for named ports, explicit env literals/ConfigMap/
Secret keys, requests/limits, startup/liveness/readiness probes, nodeSelector,
imagePullSecrets, existing ConfigMap/Secret/PVC volumes and mounts including
optional/readOnly/subPath rules. Services target declared named ports. Ingress
combines Workload routes with Placement operational policy. Publish concrete closed
shapes and boundary cases; no unknown-field dropping or generic structural merge.
Runtime-token mounts separately forbid subPath under ADR-050.

### 9. Explicit configuration document selection

configuration.documents selects unique Component-local document IDs. Order does
not introduce merge/precedence; each document is delivered separately and startup
requires the mandatory set. No automatic selection of every Component document.

An absent configuration block means the Workload does not retrieve Haby runtime
configuration. Do not automatically create RuntimeIdentityBinding or Haby token
projection merely because a Deployment exists. The exact empty-block/list policy
must be consistent and explicit in the closed contract. No document bytes or static
bearer credential is embedded in Workload declarations.

### 10. Exact Provider resolution set

The eventual deployment Plan captures a neutral set/map of exact resolutions for
used aliases. Each Resource/Workload operation refers to its own exact entry:

```text
postgresql.primary -> exact PostgreSQL configuration/implementation
kubernetes.primary -> exact Kubernetes configuration/implementation
```

Capture all relevant binding/naming/Placement inputs and freshness; irrelevant
aliases do not stale a Plan. Do not collapse to the first Provider or replace the
Resource Provider with Kubernetes. This shape is necessary for P3, not a promise
of arbitrary multi-Provider Resource combinations or multi-Application ChangeSets.
The first pure slice proves the shape; durable storage/Commit/runner integration
requires its own deliberate subsequent slice and current-alpha codec policy.

### 11. Explicit typed Kubernetes access configuration

The Kubernetes Provider uses a typed versioned contract for API endpoint, TLS trust
and exact administrative credential references. Persist it behind neutral envelopes,
not endpoint/token/CA columns in common tables. Initially use explicit one-cluster
configuration; do not search the operator's local kubeconfig or current kubectl
context implicitly. Actual credential variants/preflight and client integration
need their executable contract before those later slices.

### 12. First slice: contracts and pure effective planning

Deliver closed contract/fixture documentation, placementRef migration with real
normalization/snapshot preservation, typed immutable adapter models, pure scoped
Placement/default/override resolution, ADR-049 name calculation, image/container
resolution and exact Provider input shape. No Kubernetes client library is needed
for this pure stage. Separate extension defaults at planning from core manifest
normalization; do not rewrite imported properties to materialize operational defaults.

Do not enable existing durable Workload admission by removing pilot guards before
the runtime workflow exists. Do not combine Placement persistence/administration,
Kubernetes writes, TokenReview, publication, readiness and cleanup into this PR.
Pure output is not a claim that it is already a persisted executable DeploymentPlan.

### 13. Real-cluster prerequisites before effects

Use an isolated disposable/test Kubernetes contour, never a working namespace by
default. Before the API-adapter slice, separately investigate/select the client
library, Windows/CI cluster setup and supported Kubernetes versions. Those technical
choices do not block pure contracts. Mocks alone will not satisfy ADR-049/050/051
real API, Runtime Identity or >=150-client load evidence requirements.

## Required evidence and implementation boundaries

Prove current manifest parse/extension validation/normalization -> exact snapshot
round trip -> typed pure resolution with explicit inputs. Cover image alternatives,
ApplicationVersion versus apiVersion, invalid tags/references, default/explicit
containerName, scoped selection and absence, whole-value override behavior,
nameOverride/derived-name limits/collisions, ports/document references and exact
multiple-Provider ownership of inputs. No network, effects, credentials or dynamic
Environment resolution may enter the pure computation.

Unknown/unsupported executor capabilities remain refused. Preserve source-generated
JSON, exact-number semantics, current-alpha compatibility rules, Provider-neutral
core/storage and complete existing solution/PostgreSQL/auth/Native AOT gates. Raise
genuine product/semantic conflicts before inventing behavior; ordinary field naming,
finite bounds and schema encoding must be documented coherently in the contract.
