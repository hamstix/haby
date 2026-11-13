# Kubernetes Provider administration

Kubernetes Provider preparation uses the authenticated operator Host and the
existing API-only `habyctl provider` commands. It prepares immutable,
Environment-owned access configuration. It does not enable public Workload
Plan/Commit/execute, Deployment submission, readiness or AppliedGeneration.

## Prepare exact inputs

Create the Environment and named administrative secrets through the existing
[secret administration workflow](named-administrative-secrets.md). Use the
exact positive version of every secret; there is no implicit latest, inline
credential, kubeconfig, ambient context or cloud credential discovery. Secret
names resolve only within the declaration's Environment with
`ProviderAdministrator` purpose. Import resolves references without reading
credential material or contacting Kubernetes.

The [bearer example](examples/204-kubernetes-bearer.json) selects `bearer-token`.
The [mTLS example](examples/204-kubernetes-mtls.json) selects both
`client-certificate` and `client-private-key`. These alternatives are mutually
exclusive; empty, incomplete, mixed and unknown slot sets refuse. Properties
contain required `endpoint` and optional `serverCaPem` only. Unknown properties,
explicit null, malformed PEM, credential properties and namespace refuse.

`endpoint` is an explicit HTTPS origin, with no user information, query,
fragment or path beyond `/`. Omit `serverCaPem` to use system roots, or provide
one or more PEM certificates for explicit custom root trust. The CA is public
trust material, never a client private key. Use an identity/hostname matching
the server certificate. Neither trust mode disables hostname, validity, server
authentication purpose or revocation checks. Production uses Online revocation
with fail-closed chain validation; deploy certificates whose revocation
information is available to the Host. Unavailable revocation information causes
check failure. No operator option selects NoCheck or insecure transport.

```text
habyctl provider apply --file kubernetes-provider.json --environment staging
habyctl provider revision get cluster --revision 1 --environment staging
habyctl provider check cluster --revision 1 --environment staging
habyctl provider alias set kubernetes.primary --provider cluster --revision 1 --expect-absent --environment staging
```

The operator must authenticate to Haby and have administrative authority under
the existing operator access policy. Credential material remains in SecretStore;
ordinary Provider reads return safe metadata and exact reference identities.
The CLI sends requests only to Haby and has no Kubernetes SDK dependency.

## What the check establishes

The Host resolves the exact saved kind, configuration contract and implementation
through the registered codec/preflight registry. It resolves only those exact
Environment-scoped SecretRefs through the bounded secret store, then submits
`authentication.k8s.io/v1` SelfSubjectReview. A nonempty authenticated username is
required; `system:anonymous` and `system:unauthenticated` are refused even if the
HTTP request succeeds. The check never returns the username, raw review response,
SDK exception or credential. Results are safe existing preflight categories.

The Kubernetes credential needs `create` on `selfsubjectreviews` in
`authentication.k8s.io`. The standard `system:basic-user` role normally grants
this to authenticated users. The review is discarded by the API server after
the request; it is not a persisted probe object. See the official
[authentication information API](https://kubernetes.io/docs/reference/access-authn-authz/authentication/#api-access-to-authentication-information-for-a-client).
Whole-cluster administrator rights are not required. A forbidden review returns
InsufficientPrivilege; invalid authentication returns AuthenticationFailed.
Trust/transport failures return ServerUnavailable, malformed configuration or
credential format returns ConfigurationUnavailable, and missing exact material
returns SecretUnavailable. The ten-second preflight budget includes catalog,
secret and network work; caller cancellation propagates without a success result.

Provider access checks have no selected Placement or namespace. Ready establishes
point-in-time access/authentication compatibility only. It proves no Deployment,
namespace or other capability permission. Later executable admission must check
the exact selected Placement namespace and permissions for each requested
capability under ADR-055. Registration alone does not enable that admission.

## Retry, transfer and selection

Environment + Provider name + explicit revision is the import identity.
Equivalent validated content returns ExistingEquivalent with the original
instance/revision; different content returns RevisionContentConflict. JSON member
ordering and endpoint URI normalization do not create a different revision.
Credentials and trust roots remain immutable data of the exact revision.

A new endpoint requires explicit logical identity acknowledgement against the
highest accepted revision, irrespective of the current alias:

```text
habyctl provider apply --file revision-2.json --confirm-logical-identity --expected-previous-revision 1
habyctl provider alias set kubernetes.primary --provider cluster --revision 2 --expected-provider cluster --expected-revision 1 --environment staging
```

The acknowledgement is audited and checked atomically; a stale baseline refuses.
It is the operator's assertion of logical continuity, not physical cluster proof.
Alias set always repeats successful exact preflight outside its transaction,
then checks the expected binding and exact target atomically. Failed check or
stale expectation leaves the previous binding/audit intact. An equivalent repeat
adds no change audit. Restart reloads through the same registered codec; accepted
Plans/generations keep their frozen configuration and credential identities.

Missing stored implementation support permits startup and metadata reads but
returns HandlerUnavailable for checks; there is no PostgreSQL fallback.
Ambiguous or inconsistent current registrations fail startup. PostgreSQL's
existing administration and execution registrations remain present.

## Evidence boundary

Fast tests cover closed declaration parsing, both exact credential alternatives,
registry identity, authenticated-review outcomes and cancellation. Real PostgreSQL
Host/generated-client tests cover import/retry/conflict, reference scope,
endpoint acknowledgement, mandatory preflight/CAS/audit and restart, with separate
contexts for persisted assertions. Published Native AOT CLI exercises the same
Host path in the normal PostgreSQL gate. Its deterministic review transport is
an isolated catalog/Host fixture, not production authentication evidence.

The existing disposable Kubernetes qualification additionally exercises actual
bearer and mTLS identities, invalid bearer, untrusted server, mismatched key,
unavailable endpoint and caller cancellation through the production preflight
algorithm. Its positive API cases use the existing friend-only fixture TLS
factory because k3s fixture certificates lack a revocation service. A separate
production-factory check must refuse that certificate with Online revocation.
Thus these positive cases establish credential/API behavior, not successful
production revocation. No fixture handler or NoCheck factory is registered in
production Host. The Provider checks themselves create no application objects;
credential setup and cleanup reuse the existing owned qualification contour.

No catalog migration or reset is required by this addition. Public Workload
execution, namespace/capability admission, runtime delivery and lifecycle
completion remain separate P3 slices.
