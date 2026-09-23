# ADR-012: Runtime Identity authentication and Kubernetes TokenReview

- Status: Accepted
- Date: 2026-09-23

## Context

Haby must authenticate a running process before authorizing delivery of
Configuration documents or another runtime operation. Client-supplied
Application, Component, Workload, document or revision identifiers are request
data and cannot establish identity or broaden access.

The industry term *workload identity* conflicts with Haby's existing `Workload`
domain entity. In Haby, Workload is a portable declaration of how a Component
runs. A concrete Pod or process is produced only after that declaration becomes
Environment-specific managed state.

Kubernetes projected ServiceAccount tokens and the TokenReview API provide the
first validation mechanism. The public contract must nevertheless remain
transport-neutral, persistence-neutral and independent from Kubernetes client
types. Authentication, Haby identity mapping, authorization and configuration
delivery have different responsibilities and milestone boundaries.

## Decision

### Runtime terminology is distinct from Workload declaration

Haby uses these terms:

- `Workload` is a portable declaration nested in one Component;
- `ManagedWorkload` is its Environment-specific desired and observed state;
- `RuntimeInstance` is a concrete replica, task or process such as one Pod;
- `RuntimeIdentityEvidence` is transient sensitive credential material;
- `AuthenticatedRuntimeSubject` is an externally verified identity;
- `RuntimeIdentityBinding` maps a verified external subject to one
  ManagedWorkload security boundary.

RuntimeInstance is not a required persisted aggregate in M2. Later delivery may
append redacted per-instance audit events without creating a durable replica
inventory.

### Authentication, mapping, authorization and delivery remain separate

The pipeline is:

```text
RuntimeIdentityEvidence
  -> IRuntimeIdentityAuthenticator
  -> AuthenticatedRuntimeSubject
  -> RuntimeIdentityBinding and Haby mapping
  -> ManagedWorkload
  -> operation or ConfigurationSnapshot authorization
  -> delivery
```

An authenticator verifies external identity only. It never returns a
ManagedWorkload ID, selects Configuration documents, materializes secrets or
grants application permissions.

`RuntimeAuthenticationContext` is built by Haby from trusted state and contains
the authenticator, Provider instance, expected audience and validation policy.
Untrusted evidence cannot select arbitrary credentials, endpoints or trust
policy. Credentials are request-scoped, non-serializable, redacted and never
persisted or retained by an authenticator.

### Authenticated subjects expose a typed security core

`AuthenticatedRuntimeSubject` contains:

- stable authenticator ID;
- authority with Provider-instance ID and optional external issuer;
- stable external subject ID;
- verified audiences;
- an optional typed bound-object identity with kind, namespace, name and UID;
- optional issued-at and expiry timestamps;
- non-authoritative display attributes.

Only the typed security core participates in mapping and authorization. Display
attributes and arbitrary claims cannot grant access. Adding another
authoritative subject shape requires an explicitly versioned contract.

For Kubernetes, the ServiceAccount UID is the stable subject ID, the configured
Kubernetes Provider is the primary authority and the Pod is the bound object.
Haby core does not parse Kubernetes JWT claims or depend on TokenReview response
types.

### Runtime Identity bindings are immutable Environment state

RuntimeIdentityBinding is not portable manifest content. It contains an
immutable binding ID, Environment and ManagedWorkload IDs, authenticator and
Provider IDs, expected audience, subject constraints, validation mode and
lifecycle status.

The binding ID is a non-secret routing selector. Haby resolves it to trusted
persisted state and never tries arbitrary providers based on credential content.
Changing Provider, audience, authenticator, validation mode or subject
constraints creates a new binding and Pod-template rollout. Old and new bindings
may coexist during rollout; the old binding is revoked after the previous
ReplicaSet no longer needs it.

### Kubernetes uses installation-and-Provider audience scope

Haby generates a stable audience:

```text
urn:haby:<installation-id>:<provider-instance-id>
```

The installation ID is persisted in PostgreSQL, shared by Haby replicas and
preserved by in-place backup and recovery. An independently active clone requires
a new installation ID and binding rollout. Provider-instance ID is immutable.
A shared global audience is not the default.

The baseline `service-account-bound` mode requires:

- the configured Provider and audience;
- namespace and a dedicated ServiceAccount UID for the ManagedWorkload boundary;
- a Pod-bound token;
- normalized Pod name and UID from TokenReview.

ServiceAccount-only tokens without a Pod binding are rejected. A future
`managed-object-bound` mode may additionally verify the Pod to ReplicaSet to
Deployment owner chain against ManagedWorkload observed state.

The projected token is mounted as a rotated file, not copied into an environment
variable. A client rereads the file before each request or retry. Haby submits
the token to TokenReview, requires `status.authenticated` and verifies the
expected audience intersection.

### The first implementation fails closed

The first implementation has no positive TokenReview cache and no automatic
last-known-good configuration fallback. Provider unavailability and timeouts are
retryable. Invalid credentials, audience mismatch, revoked bindings and subject
constraint failures are not.

If Haby or the Kubernetes API is unavailable, a new Pod cannot obtain startup
configuration and must not begin primary work. Existing Pods continue with their
already loaded configuration. Signed offline snapshots and publisher-based
fallback require a separate explicit delivery decision.

### One runtime request may compose the separate stages

The initial pull UX uses one request containing a Runtime Identity credential,
non-secret binding ID and exact ConfigurationSnapshot reference. Haby performs
authentication, mapping, authorization, secret materialization and delivery as
separate internal stages. A second authentication endpoint and one-time Delivery
grant are deferred until a multi-request protocol requires them.

ConfigurationSnapshot and its transport contract belong to later delivery work,
not to `IRuntimeIdentityAuthenticator`.

### Local development uses a different identity path

A local developer process is not a RuntimeInstance created for a ManagedWorkload
and does not use RuntimeIdentityBinding or Kubernetes TokenReview. Post-M2 work
may introduce short-lived user-delegated DeveloperDeliverySessions. Development
mode alone never enables remote access; the user must explicitly select remote
mode and a target Environment.

### Delivery is staged across milestones

- M2.2 defines the transport-neutral contracts, build-time registration, fake
  authenticator, minimal TokenReview adapter and a real-cluster contract test.
- M4 defines RuntimeIdentityBinding persistence, ConfigurationSnapshots,
  authorization, pull APIs, audit, rate limiting and optional Delivery grants.
- M6 productionizes Kubernetes reconciliation, binding rotation, ServiceAccount
  and projected-token injection, application/init-container clients and delivery
  channels.

The minimal M2 adapter does not add a delivery endpoint, persist bindings or
reconcile ManagedWorkloads.

## Consequences

### Positive

- Haby domain code remains independent from Kubernetes response and hosting
  types.
- Workload declaration and concrete runtime identity are unambiguous.
- Credentials cannot select trust configuration or grant authorization by
  themselves.
- Binding rotation supports old and new ReplicaSets without mutable policy.
- Exact audiences and Pod-bound tokens reduce cross-service and
  cross-installation replay.
- A real Kubernetes test validates the abstraction before delivery APIs are
  implemented.
- Local developer access can evolve without weakening Runtime Identity.

### Negative

- New Pods depend on both Haby and Kubernetes TokenReview availability unless a
  later explicit fallback is configured.
- Safe binding changes require rollout and temporary coexistence.
- Dedicated ServiceAccounts increase the number of Kubernetes objects.
- Production delivery requires additional binding, snapshot, authorization and
  audit models beyond the authenticator contract.

## Rejected alternatives

### Treat Haby Workload as the authenticated runtime subject

A Workload is a portable declaration and can produce many concrete runtime
instances. Authentication verifies an external runtime subject and maps it to
ManagedWorkload separately.

### Let evidence select Provider credentials or audience

Untrusted routing values could otherwise select arbitrary trust configuration.
Haby resolves only persisted bindings and constructs trusted authentication
context itself.

### Pass projected ServiceAccount tokens in environment variables

Environment variables do not follow kubelet token rotation and are more likely
to leak through diagnostics. The token remains in a projected file and the
client rereads it.

### Use one global audience for every installation

This permits a token intended for one Haby installation to be replayed to
another installation connected to the same cluster. Audience scope therefore
includes installation and Provider identity.

### Cache successful TokenReview results in the first implementation

Caching weakens Pod-deletion and binding-revocation behavior before acceptable
staleness has been designed. Availability fallback is a separate explicit
delivery policy.

### Authenticate local developer processes as Kubernetes runtime instances

Local processes are user-delegated clients, not Pods produced for a
ManagedWorkload. They require a separate short-lived development-session model.

## Follow-up work

- define the concrete source-generated contracts and stable failure codes in
  M2.2;
- define sensitive credential ownership and disposal semantics in the CLR API;
- add the minimal TokenReview adapter and real-cluster contract test;
- define RuntimeIdentityBinding persistence and rollout transitions in M4;
- create a Configuration Delivery proposal for ConfigurationSnapshot,
  DeveloperDeliverySession, secret materialization and offline behavior;
- specify public error disclosure and HTTP status mapping with the M4 API.
