# ADR-034: Generated application credentials and PostgreSQL database-owner policy

- Status: Accepted
- Date: 2026-11-02
- Scope: Issue [#106](https://github.com/hamstix/haby/issues/106), usable alpha pilot
- Inspected base: `43cd89ebdf67d30d3f70a64dea1b94dc3d0a5e18` (merged PR #105)

## Context

The maintainer accepted the decision packet with `database-owner/v1` replacing
the proposed `schema-migrator/v1`, and deferred new role/access recovery.
The existing graph supports exact frozen inputs and durable execution, but
generated slots and typed outputs remain implementation work.
See the [contracts and implementation slices](../development/106-application-credential-policy.md).

Accepted ADR-006/009/017/024/025/026/029/030/032/033 continue to apply: secrets
remain separate from rendered content, references pin exact versions, ownership
is explicit, graph/attempt/audit invariants hold, SQL workflow stays in C# and
development-era compatibility is not required. This ADR permits the specified
effects through scoped implementation slices; it does not claim they exist.

## Decision

### Provider-neutral boundary

Stable Resource ownership, generated credential slots, typed secret metadata,
atomic get-or-create, exact SecretRefs, typed operation outputs, dependency
execution, uncertainty and aggregate completion are common Haby contracts.
They must not require a database Resource, database name/schema, PostgreSQL
role, SQL transaction, database-owner policy or PostgreSQL password verifier.

The #116 implementation clarification requires one shared reservation persistence
model with ProviderInstanceId, EnvironmentRecordId, ApplicationRecordId,
ResourceLocalId, Namespace and Value. Common API/EF code treats Namespace/Value
as opaque and preserves owner and Provider-scoped uniqueness. Adapters compute
and validate external identities; no Provider-specific table, column, enum or
conditional branch is introduced. This narrow common association does not
introduce a general reservation workflow, EAV catalog or arbitrary JSON model.
Other catalog entities and the existing specialized checkpoint are not rewritten
by this clarification.

The database/login pair, database-owner/v1, PostgreSQL role-name policy,
PUBLIC/schema privileges, SQL/SCRAM transport and database recovery are specific
to the PostgreSQL adapter. Its five-node graph is one concrete plan, not a
mandatory common provisioning sequence. External naming and password transport
belong to each action contract; do not apply PostgreSQL rules to RabbitMQ.

A future RabbitMQ adapter can reuse generated material and output references
with a graph such as ensure material -> create user -> configure access, without
creating a database. That trace defines an extension boundary, not implemented
RabbitMQ support or an approved RabbitMQ permission/recovery policy.

Current durable planning still has one exact Provider selection and one-Resource
admission. Extending admission for the PostgreSQL pair must keep Resource-specific
validation in the adapter rather than replace the common gate with a mandatory
PostgreSQL pair. Before admitting an Application that combines independent
Providers, represent exact resolutions per Resource/node explicitly and validate
cross-Resource dependencies. Do not force RabbitMQ onto a database-shaped input,
single global PostgreSQL selection or a different Application just to evade this
limitation. Test the selected generic seams with a typed credential-bearing
non-database action; adding a second production Provider remains separate work.

### 1. Stable owners and generated slots

Use `ManagedResourceKey(EnvironmentRecordId, ApplicationRecordId, ResourceLocalId)`.
The first two identities are internal persisted UUIDs. ResourceLocalId is a stable
declaration identity, not displayName; changing it is replacement, not rename.
The login Resource owns its application credential. Components share it through
bindings rather than receive separately generated passwords.

Use `GeneratedCredentialSlot(ManagedResourceKey, SlotId)`, with stable SlotId
`application-password`. Algorithm/profile version belongs in
`generationProfile = random-password/v1`, not the slot identity.
Typed metadata records Resource ownership, ApplicationLogin purpose, Credential
sensitivity, application/octet-stream content type and Managed ownership policy.
Administrative creation explicitly supplies Environment/ProviderAdministrator
metadata through the revised storage boundary.

The internal cancellation-aware get-or-create returns Created/Existing with one
exact SecretRef, or a bounded invalid-owner/profile/material/storage refusal.
The initial profile generates 32 cryptographically random bytes and stores their
unpadded base64url ASCII representation (43 bytes). Profile mismatch on an
existing slot is ProfileConflict, never another slot or implicit rotation.

Uniqueness plus one catalog transaction covers Secret, initial SecretVersion,
protected payload, immutable slot association and creation audit. Concurrent
losers discard candidates and read the committed winner. Resolve ambiguous
commit by rereading the same slot, not generating another accepted identity.
A missing winner payload is an error, not permission to regenerate.
Validate scope, owner, profile and exact version on use. Commit and confirm
material/reference before external role/password work.

Retain failed/unused material with its owner and audit. No automatic deletion,
reset, retry-driven versioning or cleanup. Replacement/rotation needs a later
decision. Keep the existing approved storage mode, including Plaintext with its
catalog/backup-reader exposure. Current-build restore includes slots and material.

### 2. Frozen typed output references

Consumers freeze `OperationOutputRef(producerOperationId, outputContract, outputKey)`.
Deployment and generation are those of the containing accepted graph.
A write-once association is keyed by deployment + generation + producer operation
+ output contract + key. The material node publishes
`GeneratedCredentialOutput(SecretRef)` under `haby.generated-credential/v1`,
key `password`.

Publish output, producer success and attempt closure atomically after resolving
the stable slot. Publication retry reads the same slot/reference. Validate
producer, output type and dependency at Check/Commit. Consumption requires
producer success, exact output, correct owner/Environment and pinned version.
No mutable graph placeholder, latest lookup, plaintext output or replan.
Future generations may publish their own output association for the same slot;
generation updates remain outside the pilot.

Use typed generated codecs and common output/secret abstractions.
The Resource's local material action may share its exact Provider admission key
but delegates to a Provider-neutral service without network work. Do not invent
a secret Provider or add PostgreSQL fields to common persistence/CLI.

### 3. First-pilot profile: database-owner/v1

One generated application login owns its dedicated database. Two Components may
share that identity intentionally. The login has LOGIN, NOSUPERUSER, NOCREATEDB,
NOCREATEROLE, NOREPLICATION, NOBYPASSRLS and no granted administrative memberships.
Do not give cluster-level powers merely because it owns one database.

Provider creates the database initially under administrative ownership, creates
the application role and transfers database ownership in the final configuration
step. Provider must retain administrative capability. A SUPERUSER Provider is
supported for initial deployment; support for a restricted administrative account
is deferred and is not a pilot acceptance condition. Never grant Provider
elevation automatically.

Make schema/object ownership explicit:
- The configured application schema is created with the application login as
  owner; its application-created objects are owned by that login.
- On the PostgreSQL 17 pilot, public schema ownership follows pg_database_owner;
  verify that ownership and remove PUBLIC access as specified below.
- Database ownership does not transfer ownership of pre-existing tables, schemas
  or other objects. Administrator/template-created objects are not silently
  reassigned, and blanket access to unrelated objects is not inferred.
- The application can create schemas and manage its own objects. Database-owner
  authority includes potentially destructive operations on its database; do not
  describe this profile as a runtime least-privilege account.

The restricted schema-migrator profile, separate migration identities and
general privilege-policy selection are deferred. Only database-owner/v1 is
implemented for this pilot.

### 4. ACLs, isolation and naming

Harden only the new Haby database: remove PUBLIC database privileges and PUBLIC
USAGE/CREATE on public, and do not grant PUBLIC access to the application schema.
Set creator defaults for the application role to remove future PUBLIC routine
EXECUTE/type USAGE at database-wide scope. Defaults affect that creator's future
objects, not every existing object. Use the application schema as database-local
search_path; administrator SQL uses qualified names and an independently safe
search_path.

Database ownership itself authorizes owner operations; the application may later
alter ACLs, grant access or create objects. Isolation assertions describe the
state Haby configured, not protection against a database owner deliberately
changing its own access policy.

Verify that login A cannot connect to separately hardened managed database B.
Unrelated databases may allow PUBLIC CONNECT/TEMP; do not modify them or promise
cluster-wide isolation. CREATE DATABASE and subsequent ACL restriction have an
initial window; the pilot requires an isolated target without untrusted clients
during bootstrap. Stronger cluster/HBA policy is separate.

Derive roleName as haby_a_ plus 48 lowercase SHA-256 hex characters over canonical
UTF-8 JSON of the stable owner tuple (lowercase UUIDs and ResourceLocalId).
Persist name and uniqueness reservation per ProviderInstance through the shared
Provider-neutral model in [ADR-035](0035-provider-neutral-persistence-and-name-reservations.md),
not a PostgreSQL-specific table or column. A collision or an
unmanaged existing role/database/schema is a conflict. Never choose a random
replacement name or overwrite an existing password.

Registering/adopting already-existing databases/roles is separate future work.
No automatic recreation, password change or ownership transfer is authorized
for such resources. Provider transfer and Resource identity reuse also require
a later lifecycle decision.

### 5. Five mandatory operations

Use the following sequential dependencies; exact contracts and payloads are in
the [operation table](../development/106-application-credential-policy.md#operation-dependencies-and-outcomes):

1. Create the new database under Provider administration.
2. Restrict PUBLIC access in that database.
3. Ensure local generated material and publish its exact reference.
4. Create the fixed-attribute login with its initial password and CONNECT access.
5. Transfer database ownership, configure the application-owned schema,
   creator defaults and search_path, and verify the completed profile.

The final external transaction includes ownership/access changes so rollback
does not leave a partially acknowledged configuration. Confirm external commit
and verify ownership/attributes/ACLs before recording node success. Explicitly
verify the application's actual role/database and permitted operations.
Frozen output references connect the steps; secret material is never copied
into the Plan. No AppliedGeneration until all mandatory work succeeds.

### 6. Uncertainty is retained; new recovery is deferred

Role and access DDL can use external PostgreSQL transactions, but their commit
is not atomic with Haby's journal. Before mutating SQL confirm the durable
Dispatching marker. A known server rejection with confirmed rollback is definitive;
unacknowledged commit/rollback, cancellation after the marker, verification failure
or lost Haby success persistence is uncertain.

Uncertain new restrict/login/ownership/access operations remain NeedsAttention.
No automatic redispatch, password reset, name-based adoption, compensation or
cleanup. Restart and observation must preserve this state without external
effects. Safe pre-dispatch retries and successful-node no-repeat still apply.

Do not implement new verify-and-assign/repair/retry recovery commands, OID evidence
frameworks or provenance workflows for these new actions in the first pilot.
Existing ADR-025 database-create recovery remains available for its exact action.
It does not imply recovery support for other nodes. Missing new recovery is an
explicit pilot limitation: an operator must stop and await a separately approved
resolution or explicitly manage disposable test resources outside this workflow.

Any later new recovery requires action-specific evidence, exact attempt and
material, explicit operator consent and immutable audit; database-style existence
alone is insufficient. Deferral is not permission to mark uncertain work successful.

### 7. Narrow SCRAM-verifier DDL exception

Use client-generated SCRAM verifier transport for dedicated password DDL so the
original cleartext password is not interpolated into SQL. Approve the ADR-026
exception solely for the confidential verifier with validated format and safe
quoting. This permits neither arbitrary secret interpolation nor stored workflow.

Treat verifier as confidential in commands, diagnostics, tracing and logs.
The PostgreSQL server/admin remains within the credential trust boundary;
client-side suppression cannot hide a command from a privileged server reader.
Document supported logging configuration and validate captured client/configured
server logs. Do not claim universal detection of external logging systems or
silently change server-wide auditing.

The coding slice must select/review a managed implementation and prove real
PostgreSQL interoperability, correct/wrong-password behavior and redaction before
enabling password DDL. No native dependency or particular package is selected.
A failure of these implementation gates blocks external password work, not the
initial generated-slot slice.

## Required evidence and delivery

Validate database-owner/v1 through real EF Core migrations using the generated
login: create schema and migration history, tables, indexes and sequences;
apply a later migration changing structure; perform DML. Verify owner identity
and supported ALTER/DROP/CREATE operations within its own database. Do not retain
the former denial of CREATE SCHEMA or owner powers from schema-migrator.

Also verify denied cluster-level role/database creation and access to separately
hardened database B; ownership of the database must not be treated as ownership
of administrator-created contained objects. Correct attributes, existing-name
conflict, unchanged unrelated ACLs, deterministic name/ref, no uncertain replay,
partial aggregate state, restart and current-build restore are mandatory tests.

Implement [ordered narrow slices](../development/106-application-credential-policy.md#ordered-implementation-slices),
starting with typed secret metadata and atomic generated slots without Provider
calls. New action recovery is deferred. Confidential rendering/API delivery,
Kubernetes, HA, Vault, general rotation and existing-resource registration remain
separate work. ADR-033 allows explicit fresh catalogs and one current model;
current-build immutability, audit and safety continue to apply.
