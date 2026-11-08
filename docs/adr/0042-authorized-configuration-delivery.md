# ADR-042: Authorized configuration delivery with scoped static tokens

- Status: Accepted
- Date: 2026-11-07
- Scope: P2 exact-document delivery, transient secret resolution and access administration
- Extends: ADR-006/012/026/027/033/035/036/039/040/041

## Decision

### One request, one exact document revision

Add a separate generated ConfigurationDelivery service/RPC on the existing Host.
Its independent contract lives in src/Haby.Configuration.Contracts with
configuration_delivery.proto, protobuf package haby.configuration.v1alpha1 and
C# namespace Haby.Configuration.Contracts. It does not import operator.proto or
reference Haby.Operator.Contracts, Host, persistence, Domain or Application.
Runtime consumers reference only the delivery contracts and their gRPC runtime.
Use a dedicated service implementation, not a new method on ManifestOperator or
its partial implementation. The Host explicitly composes/maps both services.
Access-grant administration stays in the operator contract. Shared hosting/address
is a deployment choice, not shared service/message identity or authorization.
This slice requires no public NuGet SDK packaging or package publication.
One call authenticates a static bearer credential, authorizes its exact document
revision and returns fully prepared JSON. No login/exchange request is required.
Operator configuration get/preview keep their existing safe behavior and never
return resolved material. Public REST delivery and Kubernetes Runtime Identity
are later adapters to this delivery use case, not prerequisites for this pilot.

The client requests an exact ConfigurationDocumentRevisionId. A trusted
administrator first creates a grant using the ADR-041 name selector and optional
--generation. Resolve that selector once on the server and require an already
materialized artifact; do not implicitly materialize. Pin the exact artifact,
deployment/generation/Component/document and source identity in grant metadata.
No latest selection or following subsequent generation changes during delivery.

```text
habyctl configuration access create processor-service --environment production --component api --document settings --token-output-file client.token
```

Creation returns safe exact metadata and the sensitive credential once. The
runtime needs the endpoint, exact revision identity and credential-file path.
No UUID is required to select the document during operator grant creation.

### Static delivery credentials and lifecycle

Generate cryptographically random tokens with at least 256 bits of secret entropy.
They are reusable and by default valid until explicit revocation. An optional
explicit future UTC expiry is supported; no implicit one-hour expiry or mandatory
24-hour maximum. Malformed/past expiry refuses. Expiry does not extend on use.

Store only a one-way cryptographic token hash, exact scope, original trusted actor/
creation time, optional expiry and revocation metadata. Raw tokens must not enter
catalog snapshots, logs, tracing or ordinary metadata. Use safe constant-time
verification. Token hash storage is authentication credential verification, not
a material-derived configuration fingerprint. Do not make tokens JWTs or introduce
OAuth, external identity servers or a general RBAC system.

No recovery of raw token is available. Rotation is explicit: create a new grant,
persistently transfer its credential, verify the client, then revoke the old grant.
Grant metadata read and idempotent revoke expose no token/hash. No automatic
renewal, token replacement or client retry loop. Creation after response loss may
produce another grant; it is not an idempotent raw-token recovery operation.

The application must retain its credential across restarts through a durable
credential file/volume or the deployment platform's credential mechanism. Haby
issuing a token does not itself persist it in the application's runtime. Do not
automatically place delivery tokens in .haby/config.yaml, URLs or command-line
arguments. Client reads a bounded credential file and supplies Authorization:
Bearer metadata. Token file/data must remain outside Git.

CLI creation requires exactly one explicit sensitive-output destination:
--token-output-file <new-path> or --token-stdout. Never overwrite an existing file.
Validate destination before issuance where possible; later file/output failures
must explain safe inspection/revocation using a known grant identity, never claim
the grant was rolled back or expose the token in error text. Text --token-stdout
supports token piping; JSON may include the credential only for this explicitly
requested one-time creation result. Ordinary get/revoke results remain metadata.
Do not reuse the normal metadata envelope to log or implicitly display credentials.

### Authorization and revocation

Delivery validates token/hash, revocation, expiry and exact requested artifact
scope before accessing document content or SecretStore. No administrative power,
other revision/Component, raw SecretRef lookup or artifact-selection override is
granted. Client-supplied token fields cannot select a different credential kind.
Use bounded safe authentication/authorization failures that reveal neither foreign
artifact existence nor protected content. Infrastructure errors remain safe and
distinct from a bad credential, without reproducing driver/exception fragments.

Check authorization on every request without a positive-authentication cache.
Revocation blocks subsequently authorized requests; an already authorized in-flight
request may complete. Expiry is checked at authorization. Do not promise recall
of responses, erasure from client memory or immediate interruption of in-flight
delivery. Authorization context is produced by the trusted server boundary,
not supplied directly by caller-controlled JSON.

### Exact secret resolution and pure formatting

Read the saved unresolved document revision, not current Profiles/aliases/defaults.
Do not call materialization, renderer, replan or external Provider APIs. Resolve
only its exact immutable SecretRefs; a later secret version or named-secret
rotation does not change this artifact. Missing material never causes regeneration.

Build and validate a closed allow-list of resolution bindings from frozen pins,
projection provenance and saved confirmed output associations. Each entry binds
an exact confidential node/reference to the declared Resource owner/purpose and
compatible formatter. Adapter-owned pure metadata may identify argument references
and supported purposes; common code must not infer PostgreSQL password semantics
from field names or introduce Provider-kind branches. No caller-supplied owner,
purpose, references or formatter arguments are accepted.

The allow-list must prove that each SecretRef agrees with the original confirmed
output and receiving projection. Merely finding a SecretReference in an arbitrary
tree is insufficient. If current artifact data cannot establish this unambiguously,
extend its closed current format with explicit validated resolution bindings;
never weaken owner/purpose checking or silently trust a caller/decoder. Any change
must preserve current-build safety under ADR-033; no silent backfill/reset.

First admit ADR-040's deferred PostgreSQL connection-string scenario, using the
exact generated ApplicationLogin credential with its Resource owner. Validate
exact formatter/compatible implementation and arguments before resolving material.
The pure formatter receives only supplied transient material; it has no I/O or
discovery capability. Common traversal/resolution/format dispatch stays Provider-
neutral. Multiple unsupported references in a deferred value must fail explicitly
rather than choose a first match or request unrelated material.

Direct standalone SecretReference delivery, Profile secretProjections/json-secret
insertion and other secret formats remain explicitly unsupported in this first
slice. Never omit an unsupported node, substitute redaction/empty password or
return a partially successful configuration. Kubernetes workload mapping and
general runtime authentication remain later work.

### Transient response and budgets

Within one request, the same authorized exact SecretRef may be resolved once and
reused. No cross-request secret/materialized-document cache. Strictly decode UTF-8;
no replacement characters. Dispose/clear owned byte/char buffers. Acknowledge
managed string/library copies cannot all be guaranteed erased.

Prepare the complete resolved document and response before publishing any body.
Any authorization/binding/secret/decode/formatter/encoding/size failure refuses
the whole response. Use unary buffered delivery, no partial streaming. The stored
revision stays unresolved and immutable; no ready string/plaintext/verifier or
secret-derived content hash is persisted. No secret-bearing payload, Authorization
metadata or sensitive response body in application/framework tracing/logging.

Return JSON bytes with safe immutable source identity. Do not calculate an ETag,
digest or fingerprint from the materialized secret-bearing result. Safe artifact
RevisionId/input fingerprint identifies the source. Sensitive response wrappers
must not leak through ToString/exception diagnostics or ordinary operator output.

Limits before publication: each resolved secret <= 64 KiB source bytes, final
JSON <= 4 MiB UTF-8, final semantic depth <= 64 containers, full RPC response <=
8 MiB. Unresolved preview size is not proof that expanded output fits. Reuse the
accepted serializer profile/exact-number policy and bounded encoders; preserve
JSON types. No double serialization, floating-point coercion or reinterpreting
ordinary JSON as commands. Cancellation propagates through resolution and response
preparation; failure leaves no saved resolved state.

### One Host, configurable shared endpoint

Operator and delivery services share one Host and one configurable address. Default
listen address remains loopback. A trusted operator may explicitly select a network
interface for a closed test contour. TLS is configured once for the common endpoint;
retain the current secure default and explicit pilot transport settings. Do not
introduce a separate listener/host or require Kubernetes for this delivery slice.

Delivery RPC requires the scoped credential from its first implementation, on
loopback as well as a configured network interface. No unauthenticated fallback.
Do not change the authorization behavior of existing operator methods implicitly.
Until the immediately following slice, operator RPC remain a trusted administrative
surface without authentication; explicitly binding a closed network interface
extends that existing pilot boundary. A delivery token is not an operator grant;
in this interim phase administrative reachability is controlled by the contour,
not by validating delivery credentials on operator methods.

Immediately next, before broader real use, implement static operator tokens with
one operator-admin permission: creation/revocation/hash storage, no OAuth or fine
RBAC. habyctl will use credentials of the selected registry/context, with delivery
and operator credentials validated as different purposes. Define first issuance
and transition/bootstrap rules before implementing that slice.
[ADR-043](0043-operator-access-tokens-and-bootstrap.md) now accepts those rules,
including explicit local issuance/recovery and operator-protected reflection.
Its implementation in issue #150 replaces the interim anonymous operator boundary;
accepting the ADR alone does not change current runtime behavior.

### Audit and delivery evidence

Persist grant creation/revocation and exact safe scope/audit. Do not persist a
runtime instance or detailed event for every delivery in this first slice;
per-request historical runtime audit is deferred. No token/material/document in
audit. Backup/current-build restore preserve grant hash/scope/expiry/revocation
and artifact identity without recreating or logging raw tokens.

The acceptance path is provisioning -> materialization -> scoped grant -> one
authenticated delivery RPC -> .NET client loads returned configuration -> real
PostgreSQL connection using generated application credentials. Test api/worker
scope separation/options, wrong/revoked/expired/mismatched tokens, exact owner/
SecretRef checks, complete failure on missing/invalid material, no secret reads
before authorization, no partial response and unchanged unresolved storage.

Use a small runtime example/test client instead of adding a universal CLI command
to print completed appsettings. CLI administers access only. Preserve complete
PostgreSQL/fault/restore/Native AOT gates, Provider neutrality and generated metadata.
Passing this checkpoint does not claim operator authentication, Kubernetes delivery,
automatic credential transport or general production readiness.
