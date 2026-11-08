# Exact authorized configuration delivery

Issue #148 implements [ADR-042](../adr/0042-authorized-configuration-delivery.md).
The operator materializes an unresolved artifact, creates a reusable credential
for that exact revision, transfers it durably, and starts a .NET runtime client.
The generated unary ConfigurationDelivery RPC authenticates, authorizes and
prepares the whole document in one request. There is no login exchange or CLI
command to dump resolved configuration.

Delivery is declared independently in `src/Haby.Configuration.Contracts/configuration_delivery.proto`,
with package `haby.configuration.v1alpha1` and C# namespace `Haby.Configuration.Contracts`.
It imports no operator contract and references no Domain/Application/Host/persistence
assembly. The dedicated `ConfigurationDeliveryService` implements this service;
ManifestOperator retains only trusted access-grant administration and safe operator
reads. Both services are mapped explicitly on the same Host endpoint.

## Shared authenticated Host

One Host/listener serves both ManifestOperator and ConfigurationDelivery.
`Haby:ListenAddress` defaults to `127.0.0.1`; `Haby:Port` selects its port.
`Haby:Tls:Enabled` defaults to true; certificate path/password configure TLS once.
Explicit TLS disable is for isolated pilot contours.

Issue #150 replaces the interim anonymous operator boundary with mandatory
operator-admin authentication, including on loopback. Complete
[local first issuance and registry credential setup](150-operator-access-tokens.md)
before the administrative commands below. The runtime still uses its exact,
independent document-scoped credential. Remote habyctl endpoints require HTTPS,
or an explicit isolated HTTP instance setting; no implicit certificate bypass.

## Issue and retain a credential

Complete the [five-step provisioning path](120-five-step-operator-path.md) with
the [Profile setup](144-postgresql-connection-rendering.md), then materialize:

```powershell
habyctl configuration materialize processor-service --environment pilot --component api --document settings --output json
habyctl configuration access create processor-service --environment pilot --component api --document settings --token-output-file D:/protected-runtime/processor.token --output json
```

Use your own protected durable directory outside Git. The destination must be
new; the CLI preopens it with exclusive CreateNew before requesting issuance and
flushes successful bytes to disk. On Unix new files have user-only permissions;
on Windows configure restrictive directory ACLs. The CLI never overwrites an
existing path or automatically saves tokens in `.haby/config.yaml`.

Name selection uses the selected registry/context/default Environment as usual.
`--generation` optionally pins an explicit historical generation. Omission
resolves the fully applied desired generation once on the server. A stored
artifact is required; grant creation never materializes, renders or resolves
secrets. Returned safe metadata includes grantId and document revisionId, complete
target, source identity and original audit.

Default expiry is absent: the credential remains valid until revoked. Optional
`--expires-at 2027-01-01T00:00:00Z` sets a future UTC instant, without a duration
cap or sliding expiry. Catalog audit/expiry instants use PostgreSQL microsecond
precision. `--token-stdout` is the alternative explicit sensitive destination:
text stdout is only the credential (metadata goes to stderr), suitable for piping
into a secure durable transport; JSON includes a credential only for that explicitly
requested one-time creation response. Do not capture that response in ordinary logs.

Tokens contain 256 random secret bits and a purpose-specific prefix plus grant
identity. Only SHA-256 verifiers and safe immutable scope/audit enter the catalog.
There is no recovery/reveal. Response loss is uncertain issuance; another create
produces a distinct grant. A failed file/output transfer after issuance reports a
known grant identity when available and safe get/revoke commands; it does not claim
rollback. An empty preopened file can remain after failure and must be inspected
before reusing that path. Never automatically retry issuance.

## Run the .NET application example

Build `examples/Haby.Configuration.Client` as part of the solution. Transfer its
token file to a protected persistent directory/volume owned by the application.
Retain that volume across process/container replacement; a container's disposable
writable layer does not provide credential durability. Supply only endpoint,
exact revision and file path as settings:

```powershell
$env:HABY_RUNTIME_ENDPOINT = 'https://localhost:7443'
$env:HABY_RUNTIME_REVISION = '<document revisionId from grant metadata>'
$env:HABY_RUNTIME_TOKEN_FILE = 'D:/protected-runtime/processor.token'
dotnet run --project examples/Haby.Configuration.Client --no-build
```

The example bounds its credential file to 512 bytes, reads it anew on startup,
sends Authorization Bearer metadata on the generated RPC, loads JSON as in-memory
.NET configuration and opens PostgreSQL using the delivered application login.
It creates/inserts/reads a small `public.haby_runtime_probe` table and prints only
revision/current_user/current_database/success. Errors omit exception/driver text.
It performs no automatic RPC retry and writes no configuration file.
Its transport is `src/Haby.Configuration.Client`, which references only the
independent delivery contracts and gRPC runtime. Configuration loading and Npgsql
remain in the executable example (`Haby.Configuration.Example`); they are not
transport-client dependencies. Neither client project references operator contracts
or server layers, and no SDK package publication is introduced.

For an explicitly isolated HTTP pilot, loopback HTTP works; non-loopback HTTP in
the example additionally requires `HABY_RUNTIME_ALLOW_INSECURE_HTTP=1`. TLS uses
the platform trust store; there is no client certificate-validation bypass.
Do not put the credential itself in environment settings, URLs or command arguments.

## Rotation and revocation

```powershell
habyctl configuration access get <grant-id> --output json
habyctl configuration access create processor-service --environment pilot --component api --document settings --token-output-file D:/protected-runtime/processor-next.token --output json
# Durably transfer the new file, start/verify the application, then:
habyctl configuration access revoke <old-grant-id> --output json
```

Get/revoke expose safe metadata only. Repeated revoke preserves original actor/time.
Every new authorization reads grant state; there is no positive-authentication
cache. Revocation prevents newly authorized calls. Already authorized in-flight
calls may finish; responses cannot be recalled. Expiry is checked at authorization.

## Resolution and failure boundaries

The runtime request contains only exact revisionId. Invalid, expired, revoked or
foreign-scope credentials receive the same bounded denial before artifact/material
access, without checking foreign artifact existence. Storage unavailability has a
distinct bounded transport failure. Runtime metadata cannot supply an owner,
purpose, formatter, argument override, generation or alternate SecretRef.

The saved frozen snapshot identifies each exact receiving projection and formatter
implementation. Saved confirmed output associations identify the producer output.
Pure adapter binding metadata validates the complete deferred argument object
against that output and frozen options. Common code admits exactly one reference
per supported deferred node. Saved output associations follow the frozen request
order, with one occurrence per participating render request. Repeated consumption
must agree on the exact producer/contract/key, confirmed attempt and output payload;
missing, extra or conflicting evidence refuses before material reads. This permits
two direct projections or a slot plus direct projection to reuse one output without
rewriting existing artifacts. Common code derives the Resource owner from the
persisted deployment and frozen Resource local ID, and permits ApplicationLogin
only. The scoped store checks owner/purpose/version and server-side payload length before
fetching bytes. The PostgreSQL binding also validates random-password/v1 material.
No Provider/default/alias discovery, renderer invocation, replan or external effect
occurs during delivery. Ordinary magic-looking JSON is data.

Standalone SecretReference nodes, Profile secret projections, unknown/ambiguous
bindings and unsupported layouts refuse the complete document. Missing material,
wrong version/scope, invalid UTF-8/NUL, invalid generated password, missing exact
formatter and formatter exceptions return no document. No empty/redacted fallback
or password regeneration exists.

Owned secret buffers are cleared/disposed on completion, cancellation and failure.
Identical authorized owner/purpose/ref tuples resolve once per request. There is
no cross-request material cache. Strict UTF-8 source bytes are limited to 64 KiB,
expanded JSON to 4 MiB and 64 semantic containers, complete protobuf reply to 8 MiB.
The pinned serializer preserves exact numbers and JSON kinds. The complete response
is buffered and checked before publication; no streaming or partial success.

The stored artifact remains byte-identical and unresolved. No completed string,
delivery body, material-derived digest/ETag or delivery history is persisted.
Framework gRPC/ASP.NET, EF and Npgsql diagnostics are excluded from the Host log
surface; sensitive generated responses have redacted diagnostic representations.
The source fingerprint identifies only the unresolved source artifact. Managed
strings, protobuf/library copies and client configuration cannot all be erased;
buffer disposal is not a guarantee of process-memory erasure.

## Catalog and evidence

`AddConfigurationAccess` adds one Provider-neutral grant table with exact artifact
foreign key, metadata-only scope, verifier and creation/optional-expiry/revocation
audit. It is additive for an admitted ADR-040/041 catalog. No artifact format change,
authority backfill or reset is required. Earlier incompatible catalogs retain the
existing explicit ADR-033 admission rules. Never silently reset persistent data.
The ADR-042 contract clarification changes the runtime RPC identity to
`haby.configuration.v1alpha1.ConfigurationDelivery/Deliver`; rebuild runtime clients
against the independent contract. There is no alias for the superseded operator
package route. Listener settings and persisted grants/artifacts are unchanged.

Current-build restart and real pg_dump/pg_restore retain verifier, exact scope,
audit, expiry/revocation and unresolved artifacts. Restored validation requires the
client/operator's retained credential file; the database cannot recover raw tokens.
Catalog backups still contain plaintext SecretStore material under ADR-006 and
must remain protected.

Tests exercise actual provisioning/shared Host/generated clients, api/worker
settings and shared credential, actual .NET process restart/application operations,
native CLI administration, revoke/rotation, clock advancement and restore. Fast
tests cover a second synthetic typed binding family, preauthorization traps,
tampering, refusal, disposal, deduplication and independent size/depth oracles.
The existing unfiltered measured PostgreSQL, fault, race, restore and Windows/Linux
Native AOT gates remain mandatory. Actual counts/CI and unperformed checks belong
in the PR report, not inferred from the existence of these tests.
