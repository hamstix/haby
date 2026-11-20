# PostgreSQL existing pair import

This bounded implementation of [#267](https://github.com/hamstix/haby/issues/267)
implements [ADR-062](../adr/0062-existing-resource-import-and-attachment.md)
through internal application services. It accepts one existing database/application
login pair and supplies the ordinary database and connection outputs. Public
operator RPC/CLI composition and operational rehearsal remain subsequent work.
Reference, Provider status and stronger lifecycle authority remain separate issues.

## Protected intake and planning

The trusted internal caller stages the password with
`IProtectedApplicationCredentialIntake.StageAsync`, an immutable request ID and
the exact Environment/Application/Resource owner. The returned opaque stage and
SecretVersion reference enter `ResourceImportIntent`; the material does not.
Use `PostgreSqlImportContracts.Encode` for the closed existing-pair input: exact
Resource IDs, database/login/schema names and their observed OIDs, including the
public schema OID. Plan and Check perform no external verification.

The same owner/request/material retry returns the same reference. Changed input
conflicts without exporting a password digest or comparison API. Material is
strict UTF-8, NUL-free, bounded to 65,536 bytes and never normalized. Pending
material persists privately for restart; it cannot resolve as an application
Secret or publish usable outputs. Lost responses never cause stage deletion.
This implementation exposes no public intake endpoint or automatic cleanup.

The current storage backend is PostgreSQL plaintext under ADR-006, including the
private stage. Protected intake describes the trusted access boundary; it does
not claim encryption at rest. Deployment protection and a future encrypted backend
remain independent requirements. Do not put passwords in manifests, typed import
payloads, Plans, logs, receipts or diagnostics.

## Read-only verification and acceptance

Execution uses the exact saved ProviderInstance/revision and the staged credential.
Bounded nonpooled read-only sessions query qualified PostgreSQL catalogs and
authenticate the supplied login against the exact database. The verifier checks
database/login/schema/public-schema identity, actual owners, safe login attributes,
effective CONNECT and CREATE on the database, and USAGE and CREATE on the declared
schema. The selected login must own the database. A public schema owned by postgres
or another observed role can qualify through effective permissions, including
existing PUBLIC grants. Existing object ownership is not scanned as a new contract;
unsupported declared requirements refuse rather than imply arbitrary migration
authority.

Verification issues no DDL, GRANT, ownership changes, password rotation or repair.
It observes again after authentication and refuses relevant drift. It cannot make
PostgreSQL observations and catalog acceptance a distributed atomic transaction.
OIDs identify the accepted observations within the logical ProviderInstance; they
are not eternal identities or proof of a physical cluster behind an endpoint.

One common catalog transaction confirms the entire pair, generic reservations,
Resource-owned ApplicationLogin SecretVersion with Imported provenance, both
ordinary outputs, actual dispatched attempts and one immutable receipt. No partial
pair becomes usable. Exact replay reads the original receipt. Reservations cover
both names and OIDs within a catalog/ProviderInstance, including aliases of that
instance; independent catalogs or duplicate logical instances are not a global
registry.

Imported credentials use the normal renderers and authorized configuration delivery.
The import output names come from the accepted typed identities. Fresh Provision
keeps deterministic naming, generated credential shape and its stricter ACL/owner
publication rules. No successful CREATE or generated credential is fabricated.

## Reuse and drift

Later generations consuming imported Resources require the exact original import
adapter's `IDurableResourceReuseVerifier`. Missing capability returns InputUnavailable.
Provider I/O runs outside catalog locks/transactions. A short catalog transaction
then rechecks the captured generation, authority, receipt, credential references,
Provider revision, operation/attempt state and cancellation before progress.
Changed captured inputs discard that verification.

The verification scope belongs to one invocation/attempt, including a generation
with zero new external nodes. Resume or a new attempt verifies again. Identity,
owner, required privilege or credential drift returns Conflict; connectivity has a
distinct retryable outcome, and cancellation remains cancellation. Irrelevant ACL
changes that preserve the contract do not conflict merely because raw ACL text
differs. Historical receipts remain unchanged. Completed-generation observation and
ordinary delivery do not acquire a continuous Provider health probe.

UseAndRetain still forbids repair, rotation, deletion and authority transfer.
Only unchanged pair reuse or supported retain-on-removal is admitted. A new manifest
does not turn import authority into Provision authority.

## Transport and catalog compatibility

Fresh PostgreSQL Provider declarations accept only `transportMode: verify-full`
(the default) or explicit `transportMode: disable`. Saved configuration shape and
payload version 2 freeze that mode. The Provider kind, shape identifier and manifest
API version are unchanged. All Provider administrator/preflight/effect/recovery and
import application authentication connections use the same typed policy. Binding
options cannot override it. There is no Prefer/Allow fallback. VerifyFull uses stock
driver chain, name and validity checks with OS trust; production configuration has
no custom CA-file or certificate-validation bypass. Disable is explicit plaintext,
not a certificate-validation claim. An internal test-only CA-file seam exercises
stock validation against owned TLS fixtures.

Older PostgreSQL payloads are unavailable to operations requiring their decoding;
metadata/history and Host startup remain available. No blanket Provider-revision
startup guard is used. The protected-input/receipt migration refuses catalogs with
prior import intent or imported authority before schema mutation: those intermediate
alpha imports need an explicitly initialized fresh catalog under ADR-033. Supported
unrelated Providers migrate normally. No reset, backfill or external deletion occurs.

## Evidence boundaries

`PostgreSqlPairImportTests` use owned disposable PostgreSQL databases/logins and
exercise read-only import, restart/lost responses, ordinary delivery, actual data
access, transport validation, conflicts, unchanged reuse, drift, captured-input
races and cancellation. Catalog tests separately cover private staging, atomic
publication, malformed evidence, receipt tampering and unrelated Provider startup.
The common connectivity retry test deliberately stubs the transport outcome; it is
not evidence of a real network outage. Fast executor tests prove reuse refusal
precedes a dependent handler, attempt and external effect. Focused runs are not a
substitute for exact-revision CI, installed-final evidence or operational rehearsal.
