# Kubernetes runtime configuration mechanism

This document explains the accepted P3 design in
[ADR-050](../adr/0050-kubernetes-runtime-identity-and-configuration-publication.md).
It is a basis for program/operator instructions, not an implemented command guide.
Current static-token delivery remains documented in the
[P2 delivery runbook](../development/148-authorized-configuration-delivery.md).

## How a Pod obtains configuration

Each ManagedWorkload has one stable ServiceAccount and RuntimeIdentityBinding.
All replicas and subsequent generations share that identity. Haby prepares exact
configuration documents and separately publishes explicit runtime permissions.
A document existing in the catalog does not itself make it accessible.

The Pod receives a reachable delivery endpoint, binding ID, exact document revision
IDs, TLS trust and the path to a read-only projected token. Kubernetes issues and
rotates the token. The client rereads that file for each request, submits the
binding ID and exact document ID, and never calls the operator service.

Haby resolves the binding to trusted Kubernetes Provider/audience/subject settings,
performs bounded TokenReview, checks Pod-bound identity and ServiceAccount UID,
checks binding status and exact document publication, then runs the existing exact
secret-resolution/deferred-formatting delivery path. No separate login or static
token exchange is required. Secrets remain transient; ordinary preview stays redacted.

## Audience, backup and independent clones

The intended audience is:

```text
urn:haby:<installation-id>:<provider-instance-id>
```

Installation ID is durable catalog identity, not the Host endpoint or pod name.
Ordinary in-place restore preserves it, as it preserves ownership and bindings.
An independently active clone needs an explicit new installation identity and
binding rollout. Copying a catalog does not establish a second independent owner
of the same external resources. Never silently regenerate identity at Host startup.
The binding ID is non-secret routing information, not authority to choose an
arbitrary TokenReview endpoint or trust configuration.

## What the boundary permits

service-account-bound does not verify Pod -> ReplicaSet -> Deployment or the
Pod's generation. An old Pod can request a newer published exact document for its
Workload. It cannot retrieve a merely materialized or foreign document. New
publication does not change old document IDs and provides no latest/hot reload.
Anyone authorized to run a Pod as this ServiceAccount or execute within one can
exercise that identity's runtime permissions. Stronger isolation is future work.

Revoking a document permission blocks later requests through that binding.
Revoking the binding blocks all its runtime permissions. Already authorized
in-flight requests may complete and already loaded process memory is not erased.
Execute/restart/retry does not reactivate revoked permissions. P2 static delivery
grants have a separate lifecycle and are not implicitly revoked or expanded.

## Mass startup and failure behavior

New processes begin primary work only after obtaining all mandatory documents.
No positive TokenReview cache or offline last-known-good fallback is promised.
Haby bounds TokenReview rate/concurrency and queue size. Overload returns temporary
failures; clients use backoff/jitter within a total startup deadline. Invalid
identity/revocation is not an overload retry. Existing processes continue with
already loaded configuration during service outages.

The supported mass-start scenario requires a real >=150-simultaneous-client
rehearsal with documented limits/deadline, bounded server/API work and successful
completion under the admitted healthy load profile. Concrete configuration keys,
limits, RPC errors and executable recovery procedures accompany implementation;
do not infer them from this design document.
