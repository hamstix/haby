# ADR-034: Generated application credentials and PostgreSQL role policy

- Status: Proposed
- Date: 2026-11-02
- Scope: Issue [#106](https://github.com/hamstix/haby/issues/106), usable alpha pilot
- Inspected base: `43cd89ebdf67d30d3f70a64dea1b94dc3d0a5e18` (merged PR #105)

## Context and existing guarantees

Production currently emits only PostgreSQL database Create. The persisted graph
has frozen typed inputs and dependencies, but no typed operation-output store.
`ISecretStore.CreateAsync` has no owner/purpose argument; both plaintext creation
and named administrative bootstrap write Environment/ProviderAdministrator metadata.
Neither is an application-credential producer. See the exact
[source inventory and contract proposal](../development/106-application-credential-policy.md).

Already accepted requirements remain binding:

- [ADR-006](0006-secret-storage-and-encryption.md): immutable exact SecretRefs,
  separate material, scoped resolution and approved protection modes.
- [ADR-009](0009-application-manifest-domain-model.md) and
  [ADR-017](0017-structured-configuration-trees-and-document-projections.md):
  Application-owned Resources, shared bindings and typed secret placeholders.
- [ADR-024](0024-provider-instance-configuration-revisions.md),
  [ADR-029](0029-named-administrative-secret-bootstrap.md) and
  [ADR-030](0030-provider-declarations-and-logical-identity.md): pinned Provider
  inputs; administrative names/receipts do not define generated-secret ownership.
- [ADR-025](0025-pilot-durable-postgresql-create-recovery.md) and
  [ADR-032](0032-dependent-operation-plans-and-exact-dispatch.md): durable external
  dispatch barriers, immutable graph, sequential explicit execution, per-node
  attempts and atomic aggregate completion. Database manual assignment does not
  authorize role assignment.
- [ADR-026](0026-postgresql-persistence-and-server-side-sql-policy.md): C# workflow,
  declarative constraints and narrow adapter SQL.
- [ADR-033](0033-development-compatibility-and-catalog-resets.md): one current
  model; explicit fresh-catalog requirements are allowed, silent resets are not.

Everything below is a recommendation requiring maintainer acceptance. This PR
does not authorize implementation or claim the proposed contracts exist.

## Proposed decisions

### 1. Stable ownership and atomic generated slots

Define `ManagedResourceKey(EnvironmentRecordId, ApplicationRecordId, ResourceLocalId)`.
The first two fields are persisted internal UUIDs; the last is the stable manifest
declaration ID, not displayName. Changing that ID is replacement, not rename.
Database and login Resources are logically owned by their Application in that
Environment. The login Resource owns its generated credential, independently of
Components, Bindings, Plans, attempts, generations and Provider revision numbers.

Define `GeneratedCredentialSlot(ManagedResourceKey, SlotId)` with exact SlotId
`application-password/v1`. Do not expose it as an operator secret name. The
slot's typed metadata is owner = ManagedResourceKey, purpose = ApplicationLogin,
sensitivity = Credential, contentType = application/octet-stream, policy = Managed.
Administrative bootstrap explicitly supplies its existing typed Environment owner
and ProviderAdministrator purpose through the revised storage boundary.

An internal cancellation-aware `GetOrCreateGenerated(slot, generationProfile)`
returns Created/Existing with one exact SecretRef, or InvalidOwner,
ProfileConflict, MaterialUnavailable, StorageUnavailable. Profile
`random-password/v1` means 32 cryptographically random bytes encoded as unpadded
base64url ASCII (43 bytes); no caller-supplied password or implicit rotation.

Use a unique owner/slot constraint and one catalog transaction for Secret,
initial SecretVersion, protected payload, immutable slot association and creation
audit. Concurrent losers roll back their candidate and read the committed winner;
they never replace it. An ambiguous commit is resolved by rereading the same slot,
not by assigning another identity. A winner whose material is missing is an error,
not permission to regenerate. Validate owner, Environment, profile and exact
version association on every use. No external call is permitted until material
and its slot reference have committed and been read back successfully.

Keep failed/unused material and its exact reference with the owner and audit.
No automatic deletion, regeneration, password reset or retry-driven versioning.
Future cleanup/rotation/replacement needs another decision. Current-build backup
and restore include slots, payloads, outputs, journals and audits together.
Keep the selected approved storage mode; Plaintext remains supported with its
database/backup reader exposure. No encrypted backend is added here.

### 2. Immutable graph with write-once typed outputs

Freeze a typed `OperationOutputRef(producerOperationId, outputContract, outputKey)`
in consumer inputs. Its deployment and generation are implicit in the containing
accepted graph, never inferred from the current deployment. This is a reference
to future evidence, not a mutable SecretRef slot in Plan JSON.

Add a write-once result association keyed by deployment + generation + producer
OperationId + output contract + key. The local material node publishes exactly
`GeneratedCredentialOutput(SecretRef)` under `haby.generated-credential/v1`, key
`password`. Publish that output, node success and attempt closure in one catalog
transaction after resolving the stable slot. If that transaction fails, replay
reads the same slot and publishes the same reference. It never generates again.

Check/Commit validate output types, producer existence and dependency edges;
execution requires producer success, exact output presence, owner/Environment
integrity and the pinned SecretVersion. Missing or corrupt output fails closed.
No graph amendment, alias lookup, manifest replanning, latest-version resolution,
plaintext result or mutable JSON substitution is allowed. The graph fingerprint
stays constant. Slot association and operation output have different scopes:
future generations reuse the slot and publish their own exact output association;
this does not authorize generation updates currently refused by the pilot.

Use the existing exact PostgreSQL admission keys for this Resource's local node;
the node delegates to a Provider-neutral secret service without network work.
Do not invent a secret Provider or put PostgreSQL fields in shared storage/CLI.
Typed input/output codecs use generated JSON metadata. Output references and
write-once associations are new model work, not capabilities proven by PR #105.

### 3. PostgreSQL ownership and minimal privilege policy

Recommend `schema-migrator/v1`: one application login, one new database and one
explicit application schema. PostgreSQL database and schema owner remain the
pinned Provider administrator; the login owns only objects it creates in that
schema. PostgreSQL roles have no object-owner field; Haby records logical role
ownership, confirmed external OID and originating attempt separately.

The login has LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOREPLICATION,
NOBYPASSRLS, NOINHERIT and no membership in another role. Give CONNECT to its
database, USAGE/CREATE on the application schema, no database CREATE or TEMPORARY.
It can migrate and perform DML on its own current and future tables/sequences;
it cannot manage administrator-created objects without separately planned grants.
No blanket grant on all current/future administrator objects is proposed.

Runtime-only would grant schema USAGE, table SELECT/INSERT/UPDATE/DELETE and
sequence USAGE, with no schema CREATE or ownership. It requires a separate
migration identity plus creator-specific default grants for future objects.
Defer that extra identity for the pilot. The recommended login can drop its own
objects and grant access to them; this is not a runtime least-privilege identity.
Two Components sharing it intentionally share that authority.

Harden only the newly created database: revoke PUBLIC database privileges and
PUBLIC USAGE/CREATE on public schema before creating the login; create the
application schema without PUBLIC access. For application-created routines/types,
revoke PUBLIC EXECUTE/USAGE in that creator's database-wide default privileges
(not only per-schema). Existing/future application tables and sequences need no
extra self-grant. No other object creators are supported by this policy.
Set the role's database-local search_path to the application schema, with
pg_catalog searched implicitly first. Administrative SQL uses qualified names
and does not trust an application-writable search_path.

Isolation means role A cannot connect to separately hardened Haby database B,
and cannot access B's data. It does not mean role A cannot connect to every
unrelated cluster database: default PUBLIC CONNECT/TEMP and explicit grants may
allow that. Do not modify unrelated databases. CREATE DATABASE cannot be grouped
atomically with its ACL restriction; there is an initial PUBLIC window. Require
an operator-prepared isolated target with no untrusted clients during bootstrap.
Stronger cluster-wide isolation/HBA changes are a separate operator policy.
See the [privilege matrix and PostgreSQL sources](../development/106-application-credential-policy.md#privileges-and-primary-evidence).

Derive the external role name as `haby_a_` plus lowercase SHA-256 hex truncated
to 48 characters over the canonical owner tuple (UTF-8 JSON array of lowercase
UUID strings and ResourceLocalId, no whitespace). Persist the resulting name
and uniqueness reservation per ProviderInstance. Collisions, existing names or
unmanaged databases/schemas fail; never adopt, pick a random suffix or overwrite
an existing password. Provider transfer/replacement and Resource ID reuse remain
unsupported until an explicit lifecycle policy exists. No additional owner role,
cluster-wide grants or application elevation is introduced.

### 4. Transactions, dispatch and action-specific recovery

Use five mandatory graph nodes: database Create, restrict database, ensure local
material, create login, configure schema/grants. Their exact inputs, outputs,
ordering and evidence are in the [operation table](../development/106-application-credential-policy.md#operation-dependencies-and-outcomes).
Role creation plus initial password and CONNECT grant use one external transaction.
Schema creation, remaining grants/defaults/search_path use another. Neither is
atomic with Haby's journal. COMMIT acknowledgement and independent verification
are required before recording success; cancellation/lost COMMIT/finalization
failure leaves uncertainty even if every external statement was transactional.

Before each external transaction, validate pinned inputs and expected ownership,
then confirm the durable Dispatching marker before sending mutating SQL. Definitive
rejection requires a known server rejection and confirmed transaction rollback;
an unacknowledged rollback or commit is uncertain. Restarted Dispatching becomes
NeedsAttention without redispatch. Local material work instead uses catalog
atomicity and same-slot replay; it has no external dispatch uncertainty.

Recommend verify-and-assign only for uncertain role/grant work. Recovery requires
explicit consent, trusted actor, exact attempt/Provider/owner/SecretRef, external
OID and independently checked role attributes, memberships, database/schema owner
and ACL/default/search_path evidence. For role recovery require fresh SCRAM
authentication with the exact stored password, identity/database queries and a
negative wrong-password control under a verified password-authentication route.
That proves credential usability, not ownership: an operator must also attest
provenance from a controlled change window or independently retained external
audit. A matching name, comment, OID or successful login alone is insufficient.

Failed authentication, absent role, inconsistent privileges or unavailable
provenance leaves NeedsAttention. Do not reset a password, adopt a role or issue
CREATE again. Alternative: an explicitly consented repair/retry workflow could
recover more cases but needs absent-effect/exclusive-access proof and a separate
reviewed policy; it is not recommended for this slice. Password rotation and
standalone ALTER ROLE PASSWORD are not admitted actions.

Recovery performs read-only evidence gathering outside the catalog transaction,
then conditionally appends one immutable decision and resolves the exact node.
Receipt replay performs no external calls. Failed/stale consent leaves state
unchanged. Partial successes and recovery never advance AppliedGeneration until
all mandatory nodes succeed through the existing aggregate completion helper.
No external compensation or implicit cleanup follows failure.

### 5. Password transport requires an explicit narrow exception

PostgreSQL password DDL accepts a password string, not a normal bind parameter.
Recommend client-side SCRAM verifier construction and transmission of only that
verifier in dedicated adapter DDL, never the cleartext password. Verifiers are
still confidential: suppress command/parameter/activity tracing in Haby, bound
driver diagnostics and validate server logging exposure in disposable tests.
Privileged server activity readers may observe the verifier; the Provider
server/admin is inside the credential trust boundary. Do not claim that client
log suppression hides SQL from the server. Refuse unsupported logging exposure
rather than silently changing server-wide audit settings.

This requires explicit maintainer approval of a narrow exception to ADR-026's
ban on secret interpolation for the safely quoted SCRAM verifier only; it does
not allow interpolated cleartext, arbitrary SQL or a stored procedure. No native
libpq dependency is selected. The coding slice must choose a reviewed managed
implementation and demonstrate verifier interoperability and redaction before
enabling the action. Rejecting this exception blocks password DDL, not the first
secret-store slice. See PostgreSQL's [client password guidance](https://www.postgresql.org/docs/17/libpq-misc.html#LIBPQ-PQENCRYPTPASSWORDCONN).

## Maintainer decision packet and delivery

All five choices above remain unapproved. Recommended answers and costs:

1. **Stable Resource slot with atomic get-or-create and retained orphans.**
   Keeps retry/restart material exact; retains unused credentials until cleanup
   has a policy. Reject Plan/attempt-scoped material and administrative names.
2. **Frozen typed output references plus write-once output rows.** Keeps Plan
   immutable; requires a new current graph/result model and retention checks.
3. **Schema-migrator with administrator-owned database/schema and bounded
   isolation.** Gives one usable migration identity; accepts object-owner power
   and the isolated-bootstrap requirement. Runtime-only costs a second identity.
4. **Manual evidence-based verify-and-assign; no automatic repair/reset.**
   Avoids password overwrite/adoption; some interrupted work remains blocked
   until a separately approved repair or disposable-environment cleanup.
5. **Narrow SCRAM-verifier DDL exception.** Avoids cleartext password SQL;
   requires confidential verifier handling and managed-client proof. Without
   approval, implement only non-external slices.

After the selected decisions become Accepted, start with typed owner/purpose and
atomic generated-slot storage only. Follow the ordered
[coding slices and observable scenarios](../development/106-application-credential-policy.md#ordered-implementation-slices).
They end in a real generated-role login. Confidential Configuration rendering/API
delivery needs separate approval and is not a prerequisite for credential-store
tests. No Kubernetes, HA, Vault, general rotation, deprovisioning, plugin framework,
obsolete alpha readers or historical upgrade matrix belongs in these slices.
Any breaking implementation must declare fresh-catalog requirements and preserve
current-build immutability, redaction, restart/retry, audit and backup/restore.
