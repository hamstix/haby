# Workload identity authentication

- Status: Proposed for M2
- First implementation: Kubernetes TokenReview
- Related proposal: [Application manifest](application-manifest.md)

## Decision

Haby will introduce a transport-neutral workload identity authentication
abstraction in M2. Kubernetes TokenReview is the first intended implementation.

Configuration publishers remain a separate, later capability. M2 does not need
HTTP push, ConfigMap/Secret materialization, Consul publication, a public
configuration-delivery endpoint or one-time delivery grants in order to define
and test workload identity correctly.

The separation is intentional:

- an authenticator proves which external workload presented an identity;
- Haby core maps that verified subject to a persisted Workload and decides what
  the subject may access;
- a delivery use case selects and returns an exact Configuration-document
  revision;
- a publisher pushes or materializes that revision when a pull protocol is not
  used.

An authenticator must never decide which Application, Component or document the
caller may access.

## M2 contract boundary

The public abstraction should express the following conceptual operation:

```csharp
public interface IWorkloadIdentityAuthenticator
{
    ValueTask<WorkloadAuthenticationResult> AuthenticateAsync(
        WorkloadIdentityEvidence evidence,
        CancellationToken cancellationToken);
}
```

The final type names may change during implementation, but the contract has
these invariants:

- it does not reference ASP.NET Core, gRPC, EF Core or Kubernetes types;
- the credential is transient input and cannot be serialized or persisted by
  the contract;
- the caller selects a configured authenticator/provider instance; untrusted
  evidence cannot select arbitrary server credentials or endpoints;
- cancellation is mandatory for all external validation;
- success returns a typed authenticated subject, not application permissions;
- failures use stable reason codes and sanitized diagnostics;
- raw credentials, reviewed tokens and confidential claims never appear in
  logs, traces, exceptions or audit records.

`WorkloadIdentityEvidence` conceptually contains:

- the authentication scheme selected by the configured delivery target;
- a provider-instance reference resolved by trusted server configuration;
- the opaque bearer evidence;
- the audience expected by Haby;
- optional non-secret transport context needed for replay controls.

`AuthenticatedWorkloadSubject` conceptually contains:

- authenticator and provider-instance IDs;
- issuer and stable external subject ID;
- verified audiences;
- issued-at and expiry timestamps when supplied by the identity provider;
- a typed bound-object identity when supported, such as Pod name and UID;
- normalized platform attributes required by an authorization mapping, such as
  Kubernetes namespace and ServiceAccount name.

Plugin-specific response objects must be normalized inside the adapter. Haby
application and domain code must not parse Kubernetes JWT claims or depend on a
TokenReview response.

## Kubernetes implementation

The Kubernetes adapter validates a projected ServiceAccount token by submitting
it to the configured cluster's TokenReview API. It supplies the expected Haby
audience and requires both an authenticated result and a compatible returned
audience.

The preferred workload shape is:

```yaml
spec:
  serviceAccountName: example-api
  automountServiceAccountToken: false
  volumes:
    - name: haby-identity
      projected:
        defaultMode: 0400
        sources:
          - serviceAccountToken:
              path: token
              audience: haby-config
              expirationSeconds: 600
```

Only the Haby init container or another explicitly authorized configuration
client mounts this volume. The main application container does not receive the
identity token unless it implements the delivery client itself.

Kubernetes requires `expirationSeconds` to be at least 600 seconds. If a later
delivery protocol needs a credential valid for only one minute, Haby may
exchange the validated Kubernetes identity for a hashed, one-time Delivery
grant. That grant belongs to the delivery protocol, not to
`IWorkloadIdentityAuthenticator`.

### Required validation

The adapter must:

1. Resolve cluster access from a trusted Provider instance.
2. Submit TokenReview with the configured audience.
3. Require `status.authenticated` and the expected audience intersection.
4. Normalize namespace and ServiceAccount identity.
5. In strict mode, require a Pod-bound identity and normalize Pod name and UID.
6. Return an authentication failure if required bound-object information is not
   available from a supported cluster version.
7. Preserve cancellation and classify cluster unavailability separately from
   an invalid credential.
8. Dispose of token material after the request and never include it in
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
be used for each independently authorized workload boundary.

## Authorization mapping

Authentication output is not authorization. Haby core owns an explicit mapping
from a verified external subject to an internal Workload identity.

For Kubernetes, a mapping can constrain:

- Kubernetes Provider instance or cluster identity;
- namespace;
- ServiceAccount name and UID where available;
- Pod-bound identity;
- Haby Workload ID reconciled into protected Pod metadata.

Application, Component, document and revision IDs supplied by the client are
request data only. They cannot broaden the permissions derived from the verified
subject and Delivery binding.

The eventual pull flow must request an exact immutable Configuration-document
revision or digest rather than an unconstrained `latest` value. This prevents
Pods in one rollout from starting with different effective configuration.

## Failure model

The contract distinguishes at least:

- missing or malformed evidence;
- invalid or expired credential;
- audience mismatch;
- unsupported or missing workload binding;
- provider unavailable or timed out;
- internal adapter failure.

External validation errors may be recorded as redacted operational diagnostics.
An API adapter converts them into stable public error codes without exposing the
credential or Kubernetes response body.

Authentication does not silently fall back to an anonymous subject. A delivery
client fails closed unless an explicit, separately designed signed-cache policy
permits startup from a previously verified revision.

## M2 deliverables

M2 should implement only the stable seam needed by later delivery work:

1. transport- and persistence-independent evidence, subject, result and failure
   types;
2. `IWorkloadIdentityAuthenticator` with cancellation;
3. explicit build-time registration and selection by stable authenticator ID;
4. an in-memory fake for application tests;
5. contract tests covering success, invalid evidence, audience mismatch,
   provider failure, cancellation and credential redaction;
6. a Kubernetes TokenReview adapter design or thin implementation without
   introducing the complete delivery endpoint.

The M2 pull request must not add publisher implementations, persist bearer
tokens, dynamically load authentication assemblies or couple domain contracts to
Kubernetes client models.

## Later delivery work

After the M2 domain and authentication boundary is stable:

- M4 can expose the authenticated pull endpoint, authorization policies,
  auditing, rate limits and optional one-time Delivery grants;
- M6 can complete the Kubernetes init-container integration and add publishers
  such as HTTP, ConfigMap/Secret and Consul;
- sidecar refresh remains deferred until reload and consistency semantics are
  defined by a real use case.

## References

- [Kubernetes Service Accounts](https://kubernetes.io/docs/concepts/security/service-accounts/)
- [Kubernetes projected volumes](https://kubernetes.io/docs/concepts/storage/projected-volumes/)
- [Kubernetes TokenReview API](https://kubernetes.io/docs/reference/kubernetes-api/definitions/token-review-v1-authentication/)
- [Kubernetes security checklist](https://kubernetes.io/docs/concepts/security/security-checklist/)
