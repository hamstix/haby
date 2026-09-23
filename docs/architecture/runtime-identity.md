# Runtime identity authentication

- Status: Accepted for M2
- First implementation: Kubernetes TokenReview
- Related proposal: [Application manifest](application-manifest.md)
- Decision record: [ADR-012](../adr/0012-runtime-identity-authentication.md)

## Decision

Haby uses **Runtime Identity** for the industry concept commonly called workload
identity. The distinct term prevents collision with Haby's `Workload` domain
entity, which is a declaration of how a Component runs rather than a concrete
running process.

Haby will introduce a transport-neutral Runtime Identity authentication
abstraction in M2. Kubernetes TokenReview is the first intended implementation.

Configuration publishers remain a separate, later capability. M2 does not need
HTTP push, ConfigMap/Secret materialization, Consul publication, a public
configuration-delivery endpoint or one-time delivery grants in order to define
and test Runtime Identity correctly.

The separation is intentional:

- an authenticator proves which external runtime subject presented an identity;
- Haby core maps that verified subject to a persisted `ManagedWorkload` and
  decides what the subject may access;
- a delivery use case selects and returns an exact Configuration-document
  revision;
- a publisher pushes or materializes that revision when a pull protocol is not
  used.

An authenticator must never decide which Application, Component, Workload or
document the caller may access.

## Terminology and lifecycle

The relevant entities form this lifecycle:

```text
Application
  `- Component
       `- Workload
            |
            | applied to an Environment
            v
        ManagedWorkload
            |
            | realized by a runtime provider
            v
        RuntimeInstance [0..N]
            |
            | presents identity evidence
            v
        AuthenticatedRuntimeSubject
```

- `Workload` is the portable declaration nested inside a Component. It describes
  how that Component should run.
- `ManagedWorkload` is the Environment-specific desired and observed state
  produced from one Workload declaration.
- `RuntimeInstance` is a concrete running replica, task or process produced for a
  ManagedWorkload, such as a Kubernetes Pod identified by its UID.
- `RuntimeIdentityEvidence` is transient sensitive evidence presented by a
  RuntimeInstance.
- `AuthenticatedRuntimeSubject` is the external identity verified by a trusted
  runtime platform or identity provider.
- `RuntimeIdentityBinding` is an authorization-owned rule that maps a verified
  external subject to a ManagedWorkload identity.

`RuntimeInstance` is a conceptual and observed entity, not a required persisted
aggregate in M2. The authentication contract must not introduce an inventory of
replicas. A later M6+ delivery implementation may append an audit event when it
issues a Configuration-document revision, including a normalized runtime subject
or Pod UID where appropriate, without persisting credentials.

An `AuthenticatedRuntimeSubject` is not itself a `RuntimeInstance`.
Authentication first verifies an external subject; Haby then maps that subject
to a ManagedWorkload and, when needed, establishes which concrete
RuntimeInstance it represents.

## Responsibility pipeline

```text
RuntimeIdentityEvidence
        |
        | IRuntimeIdentityAuthenticator
        v
AuthenticatedRuntimeSubject
        |
        | RuntimeIdentityBinding and Haby mapping
        v
ManagedWorkload [+ observed RuntimeInstance]
        |
        | delivery authorization
        v
Allowed Configuration-document revision or runtime operation
```

The stages have independent responsibilities:

1. The transport adapter captures transient evidence without logging or
   persisting it.
2. The Runtime Identity authenticator verifies the external identity and returns
   a normalized subject without granting Haby permissions.
3. Haby core maps the subject through trusted bindings to a ManagedWorkload.
4. The target use case authorizes an exact operation or Configuration-document
   revision.
5. The delivery layer returns or publishes data and records a redacted audit
   event when that capability is implemented.

## M2 contract boundary

The public abstraction should express the following conceptual operation:

```csharp
public interface IRuntimeIdentityAuthenticator
{
    ValueTask<RuntimeAuthenticationResult> AuthenticateAsync(
        RuntimeAuthenticationContext context,
        RuntimeIdentityEvidence evidence,
        CancellationToken cancellationToken);
}
```

The final type names may change during implementation, but the contract has
these invariants:

- it does not reference ASP.NET Core, gRPC, EF Core or Kubernetes types;
- the credential is transient input and cannot be serialized or persisted by
  the contract;
- trusted Haby configuration selects the authenticator, Provider instance and
  expected audience; untrusted evidence cannot select arbitrary credentials,
  endpoints or trust policy;
- cancellation is mandatory for all external validation;
- success returns a typed authenticated subject, not application permissions;
- failures use stable reason codes and sanitized diagnostics;
- raw credentials, reviewed tokens and confidential claims never appear in
  logs, traces, exceptions or audit records.

`RuntimeAuthenticationContext` conceptually contains trusted values resolved by
Haby before the adapter runs:

- the stable authenticator ID;
- a Provider-instance reference;
- the expected audience and validation policy;
- optional trusted context required by that adapter.

`RuntimeIdentityEvidence` contains only transient sensitive credential material
and optional non-secret transport evidence. It must be represented by a
non-serializable redacting abstraction rather than an ordinary DTO or persisted
string. Managed runtimes cannot guarantee erasure of every credential copy, but
the contract must prevent accidental serialization, logging and retention.

`AuthenticatedRuntimeSubject` is a transport-neutral, typed security value:

```text
AuthenticatedRuntimeSubject
|- authenticatorId
|- authority
|  |- providerInstanceId
|  `- externalIssuer?
|- subjectId
|- verifiedAudiences[]
|- boundObject?
|  |- kind
|  |- namespace?
|  |- name
|  `- uid
|- issuedAt?
|- expiresAt?
`- displayAttributes
```

`subjectId` is the stable external principal ID. `boundObject` is a typed
identity rather than a plugin-defined claims dictionary. Mapping and
authorization may use only the authenticator, authority, stable subject ID,
verified audiences and typed bound object. Display attributes are non-authoritative
diagnostic data and cannot grant access. New authoritative subject shapes require
an explicitly versioned contract rather than ad-hoc claim names.

For Kubernetes, `subjectId` is the ServiceAccount UID, the Provider instance is
the primary cluster authority, and `boundObject` identifies the Pod. Namespace
and ServiceAccount name are retained as display and validation context. Issuer,
issued-at and expiry remain optional because TokenReview does not guarantee that
all underlying token claims are returned.

Plugin-specific response objects must be normalized inside the adapter. Haby
application and domain code must not parse Kubernetes JWT claims or depend on a
TokenReview response. An authenticator returns no `ManagedWorkloadId` and cannot
perform the subsequent Haby mapping or authorization.

## Runtime Identity binding lifecycle

`RuntimeIdentityBinding` is Environment-specific Haby state, not portable
Application-manifest content. It contains at least:

```text
RuntimeIdentityBinding
|- immutable binding ID
|- Environment ID
|- ManagedWorkload ID
|- authenticator ID
|- ProviderInstance ID
|- expected audience
|- subject constraints
|- validation mode
`- lifecycle status
```

The planner and ManagedWorkload reconciler create bindings. A binding ID is an
opaque non-secret routing selector; receiving it from a caller never makes the
referenced Provider or policy trusted. Haby resolves only an existing binding
and then uses its persisted Provider, audience and constraints to build
`RuntimeAuthenticationContext`. It never tries arbitrary clusters based on
untrusted evidence.

Bindings are immutable after activation. Changing Provider, audience,
authenticator, validation mode or subject constraints creates a new binding and
updates the Pod template, producing a rollout. The previous binding may remain
active for the old ReplicaSet until rollout completion, then transitions through
retiring to revoked. One ManagedWorkload may therefore have several bindings
during safe rotation. A revoked binding cannot authenticate even if its external
credential has not expired.

## Kubernetes implementation

The Kubernetes adapter validates a projected ServiceAccount token by submitting
it to the configured cluster's TokenReview API. It supplies the expected Haby
audience and requires both an authenticated result and a compatible returned
audience.

### Deployment integration

The preferred self-configuring application shape uses a dedicated ServiceAccount
for each independently authorized ManagedWorkload boundary:

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: example-api
  namespace: production
automountServiceAccountToken: false
```

The Workload reconciler projects a narrow-audience token explicitly and mounts it
only into the container that performs configuration delivery:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: example-api
  namespace: production
spec:
  replicas: 3
  selector:
    matchLabels:
      app: example-api
  template:
    metadata:
      labels:
        app: example-api
      annotations:
        haby.io/managed-workload-id: "01JXYZ..."
        haby.io/configuration-snapshot-id: "01JABC..."
    spec:
      serviceAccountName: example-api
      automountServiceAccountToken: false
      containers:
        - name: api
          image: example/example-api:3.4.6
          env:
            - name: HABY_ENDPOINT
              value: "https://haby.example.internal"
            - name: HABY_RUNTIME_BINDING_ID
              value: "01JDEF..."
            - name: HABY_CONFIGURATION_SNAPSHOT_ID
              value: "01JABC..."
            - name: HABY_IDENTITY_TOKEN_FILE
              value: "/var/run/secrets/haby/token"
          volumeMounts:
            - name: haby-runtime-identity
              mountPath: /var/run/secrets/haby
              readOnly: true
      volumes:
        - name: haby-runtime-identity
          projected:
            defaultMode: 0400
            sources:
              - serviceAccountToken:
                  path: token
                  audience: "urn:haby:installation-123:kubernetes-primary"
                  expirationSeconds: 600
```

The token itself is never copied into an environment variable. Environment
variables contain only non-secret routing and pinning metadata plus the token
file path. Kubelet rotates a projected ServiceAccount token before expiry, so a
client reads the file for each request or retry instead of caching its startup
contents indefinitely.

Only the self-configuring application, Haby init container or another explicitly
authorized configuration client mounts this volume. Other containers in the Pod
do not receive the identity token.

Kubernetes requires `expirationSeconds` to be at least 600 seconds. If a later
delivery protocol needs a credential valid for only one minute, Haby may
exchange the validated Kubernetes identity for a hashed, one-time Delivery
grant. That grant belongs to the delivery protocol, not to
`IRuntimeIdentityAuthenticator`.

The audience is stable, non-secret and scoped to the Haby installation and
Kubernetes Provider instance:

```text
urn:haby:<installation-id>:<provider-instance-id>
```

`installationId` is generated once, persisted in PostgreSQL and shared by all
replicas of one Haby installation. Backup and in-place disaster recovery preserve
it. Creating an independent installation from a backup requires a new
installation ID and binding rollout if both installations could reach the same
cluster. `providerInstanceId` is the immutable persisted Provider identity.

Haby generates the audience rather than accepting an arbitrary manifest value.
Changing it creates a new RuntimeIdentityBinding and rolls the ManagedWorkload.
Several ManagedWorkloads may share the installation-and-Provider audience;
binding subject constraints and dedicated ServiceAccount UIDs provide the next
authorization boundary. A shared global value such as `haby-config` is not
allowed as the default because it enables cross-installation replay against the
same cluster.

### Pinned configuration snapshot

The Pod template pins an immutable `ConfigurationSnapshot` rather than asking
for `latest`. A snapshot is a delivery-layer view containing the exact
Configuration-document revisions authorized for one ManagedWorkload generation:

```text
ConfigurationSnapshot
|- ManagedWorkload ID and generation
|- exact ConfigurationDocumentRevision references
|- materialization policy
`- deterministic digest
```

Pinning the snapshot in the Pod template keeps old and new ReplicaSets
consistent during a rollout. Restarting a Pod from an old ReplicaSet cannot
silently load a configuration produced for a newer container or ManagedWorkload
generation.

`ConfigurationSnapshot` belongs to the later delivery contract and is not part
of the M2 Runtime Identity authenticator. The delivery milestone must define its
transport representation, secret materialization rules and digest semantics.

### Single-request pull flow

The initial production flow uses one request. A conceptual route is:

```http
GET /api/v1/runtime/configuration-snapshots/01JABC...
Authorization: Bearer <projected-service-account-token>
X-Haby-Runtime-Binding: 01JDEF...
```

The exact REST route remains an API-contract decision, but its semantics are
fixed: the caller presents one Runtime Identity credential and requests one
exact immutable snapshot. The binding ID and snapshot ID are not credentials.

Haby executes distinct internal stages within that request:

```text
resolve RuntimeIdentityBinding
  -> build trusted RuntimeAuthenticationContext
  -> validate the token through Kubernetes TokenReview
  -> normalize AuthenticatedRuntimeSubject
  -> map the subject to ManagedWorkload
  -> authorize the exact ConfigurationSnapshot
  -> materialize authorized secret values
  -> return documents
  -> append a redacted delivery audit event
```

A successful response may conceptually contain several documents:

```json
{
  "snapshotId": "01JABC...",
  "documents": [
    {
      "id": "settings",
      "format": "json",
      "content": {}
    }
  ]
}
```

Sensitive responses require TLS and `Cache-Control: no-store`. Credentials,
materialized secrets and complete configuration payloads must not appear in
access logs, traces or audit events.

A separate authentication endpoint and one-time Delivery grant are deferred. A
two-request exchange may be added when multiple delivery calls, a separate
delivery service or strict one-time access justifies the additional credential
lifecycle. It is not required for an application that fetches its startup
configuration directly from Haby.

### Local developer access boundary

A process started directly by a developer is not a RuntimeInstance produced for
a ManagedWorkload. It must not impersonate a Kubernetes Pod, reuse a
RuntimeIdentityBinding or present a long-lived shared Haby token from a committed
development settings file.

Post-M2 delivery work will define a separate user-delegated
`DeveloperDeliverySession` flow. A CLI will authenticate the developer, select an
explicit Haby Environment and target, resolve an exact ConfigurationSnapshot and
issue a short-lived scoped session credential. The local process will use that
credential through a developer delivery endpoint, while the common delivery use
case continues to enforce snapshot authorization, secret-materialization policy
and redacted audit.

The two authentication paths remain distinct before converging on delivery:

```text
Kubernetes Runtime Identity ---\
                                +--> authorized ConfigurationSnapshot delivery
Developer Delivery Session ----/
```

Development mode alone must never activate remote access. A client must require
an explicit remote-configuration mode and target Environment so that
`DOTNET_ENVIRONMENT=Development` or an equivalent local runtime setting cannot
silently connect to Staging or Production.

Session credentials must not be stored in committed `appsettings` files or
ordinary environment variables. The future CLI may use an operating-system
credential store, a protected short-lived token file or a loopback helper. The
details belong to a separate Configuration Delivery proposal together with
client configuration precedence, offline behavior and secret access.

`IRuntimeIdentityAuthenticator` does not authenticate developers or their local
processes, and M2 does not define DeveloperDeliverySession contracts or a local
configuration client.

### Required validation

The complete request pipeline must:

1. Resolve `RuntimeIdentityBinding` from the supplied non-secret binding ID.
2. Resolve cluster access, expected audience and subject constraints from the
   trusted Provider and binding rather than from evidence supplied by the
   caller.
3. Submit TokenReview with the configured audience.
4. Require `status.authenticated` and the expected audience intersection.
5. Normalize namespace, ServiceAccount identity, Pod name and Pod UID.
6. Map the subject to the binding's ManagedWorkload without allowing request
   Application, Component, Workload or document IDs to broaden access.
7. Authorize the exact requested ConfigurationSnapshot and ManagedWorkload
   generation before materializing any secret.
8. Return an authentication failure if required bound-object information is not
   available from a supported cluster version.
9. Preserve cancellation and classify cluster unavailability separately from
   an invalid credential.
10. Dispose of token material after the request and never include it in
    diagnostics.

TokenReview is preferred to offline JWT verification because the Kubernetes API
server checks the current bound object. A token bound to a deleted Pod can
therefore be rejected before its encoded expiry.

### Cluster permissions

Each Kubernetes Provider instance owns credentials for its cluster. The Haby
identity adapter needs permission to create TokenReview requests. Authorization
may additionally use narrowly scoped Pod reads when it must verify Haby-managed
workload metadata.

The application ServiceAccount does not need Kubernetes API, Secret or ConfigMap
permissions merely to authenticate to Haby. A dedicated ServiceAccount should
be used for each independently authorized ManagedWorkload boundary.

### ManagedWorkload ownership validation

The first supported and default mode is `service-account-bound`. It maps a
Provider, namespace and dedicated ServiceAccount UID to exactly one
ManagedWorkload and always requires a Pod-bound token plus normalized Pod name
and UID from TokenReview. ServiceAccount-only tokens without a bound Pod are not
accepted in this mode. Any principal allowed to create a Pod that uses that
ServiceAccount can act as that security boundary, so cluster RBAC must restrict
that privilege.

A stronger `managed-object-bound` mode additionally reads the Pod and follows
its owner chain through ReplicaSet to the Deployment UID recorded in
ManagedWorkload observed state. This proves that the Pod was created by the
Haby-managed Deployment rather than merely using the expected ServiceAccount.
This mode requires narrowly scoped read access to Pods, ReplicaSets and
Deployments and is deferred until ManagedWorkload observed state reliably stores
external Deployment identity.

Labels and annotations help correlation but are not sufficient authentication
evidence by themselves.

### Integration diagnostics

The minimal M2.2 real-cluster contract test covers TokenReview authentication,
audience validation, Pod binding, provider failure, cancellation and redaction
without calling a configuration endpoint. The complete M6 delivery suite
verifies at least:

1. the projected token file exists without printing its contents;
2. a valid Pod obtains its pinned ConfigurationSnapshot;
3. a different ServiceAccount is rejected;
4. an audience mismatch is rejected;
5. a snapshot owned by another ManagedWorkload is rejected;
6. a deleted Pod's bound token no longer authenticates;
7. Kubernetes API unavailability produces a retryable provider failure;
8. a request after token rotation succeeds because the client rereads the file.

Redacted diagnostics may contain request ID, Provider instance ID, binding ID,
namespace, ServiceAccount, Pod UID, snapshot ID and stable outcome codes. They
must not contain the Authorization header, token, JWT payload, TokenReview body,
materialized secrets or returned document content.

## Runtime Identity mapping and authorization

Authentication output is not authorization. Haby core owns an explicit
`RuntimeIdentityBinding` from a verified external subject to an internal
ManagedWorkload identity.

For Kubernetes, a mapping can constrain:

- Kubernetes Provider instance or cluster identity;
- namespace;
- ServiceAccount name and UID where available;
- Pod-bound identity;
- optional owner-chain verification against the external Deployment UID stored
  in ManagedWorkload observed state.

Application, Component, document and revision IDs supplied by the client are
request data only. They cannot broaden the permissions derived from the verified
subject and Runtime Identity binding.

The pull flow requests one exact immutable ConfigurationSnapshot rather than an
unconstrained `latest` value. This prevents Pods in one rollout from starting
with different effective configuration.

## Failure model

The contract distinguishes at least:

- missing or malformed evidence;
- invalid or expired credential;
- audience mismatch;
- missing or revoked Runtime Identity binding;
- subject-constraint or bound-object mismatch;
- provider unavailable or timed out;
- internal adapter failure;
- mapping or authorization denial;
- unavailable or unauthorized ConfigurationSnapshot.

External validation errors may be recorded as redacted operational diagnostics.
An API adapter converts them into stable public error codes without exposing the
credential or Kubernetes response body.

Authentication does not silently fall back to an anonymous subject. A delivery
client fails closed unless an explicit, separately designed signed-cache policy
permits startup from a previously verified revision.

The first implementation performs no positive TokenReview cache and has no
automatic last-known-good configuration fallback. Provider unavailability and
timeouts are retryable; invalid credentials, audience mismatch, revoked bindings
and failed subject constraints are not. The client retries transient failures
with bounded exponential backoff and rereads the projected token file before
each attempt.

If Haby or the Kubernetes API is unavailable, a new Pod cannot obtain its
configuration and must not begin its primary work. Existing Pods continue with
the configuration already loaded in memory. Readiness and startup behavior must
make this dependency visible. Signed offline snapshots, publisher-based fallback
and other degraded-start policies require a separate delivery decision and must
never be enabled implicitly.

Authentication, mapping, authorization and delivery failures remain distinct
internal results even when a future API intentionally returns less specific
public errors to prevent information disclosure.

## M2 deliverables

M2 should implement only the stable seam needed by later delivery work:

1. transport- and persistence-independent context, evidence, subject, result and
   failure types;
2. `IRuntimeIdentityAuthenticator` with cancellation;
3. explicit build-time registration and selection by stable authenticator ID;
4. an in-memory fake for application tests;
5. contract tests covering success, invalid evidence, audience mismatch,
   provider failure, cancellation, credential redaction and the absence of
   authorization decisions in authenticator results;
6. a minimal Kubernetes TokenReview adapter and real-cluster integration test
   that validate the abstraction without introducing the complete delivery
   endpoint, RuntimeIdentityBinding persistence or Workload reconciliation.

The M2.2 pull request must not add publisher implementations, persist bearer
tokens or RuntimeInstances, dynamically load authentication assemblies, couple
domain contracts to Kubernetes client models or authorize Configuration access
inside an authenticator.

## Later delivery work

After the M2 domain and authentication boundary is stable:

- M4 can expose the authenticated pull endpoint, Runtime Identity bindings,
  authorization policies, auditing, rate limits and optional one-time Delivery
  grants;
- M4 can define user-delegated DeveloperDeliverySessions, their authorization
  and a separately disableable developer delivery endpoint;
- M6 can productionize the Kubernetes adapter, complete binding rotation,
  ServiceAccount and Deployment reconciliation, add application/init-container
  integration and add publishers such as HTTP, ConfigMap/Secret and Consul;
- M6 can add a CLI-assisted local development workflow and configuration client
  after delivery and secret-materialization semantics are stable;
- M6+ may append redacted configuration-issuance audit events associated with a
  normalized runtime subject without creating a durable RuntimeInstance
  inventory;
- sidecar refresh and persisted RuntimeInstance inventory remain deferred until
  reload, observability and consistency semantics are defined by a real use case.

## References

- [Kubernetes Service Accounts](https://kubernetes.io/docs/concepts/security/service-accounts/)
- [Kubernetes projected volumes](https://kubernetes.io/docs/concepts/storage/projected-volumes/)
- [Kubernetes TokenReview API](https://kubernetes.io/docs/reference/kubernetes-api/definitions/token-review-v1-authentication/)
- [Kubernetes security checklist](https://kubernetes.io/docs/concepts/security/security-checklist/)
