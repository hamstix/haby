# Issue 106: accepted application credential policy

Status: accepted policy under [ADR-034](../adr/0034-application-credentials-and-postgresql-role-policy.md).
The maintainer selected database-owner/v1 and deferred new role/access recovery.
Contracts below require implementation; the documentation does not claim they exist.
Inspected implementation base: 43cd89ebdf67d30d3f70a64dea1b94dc3d0a5e18.

## Source inventory

| Source | Current boundary |
| --- | --- |
| [SecretStore.cs](../../src/Haby.Application/SecretStore.cs) | CreateAsync lacks typed owner/purpose and generated-slot get-or-create. |
| [PostgreSqlPlaintextSecretStore.cs](../../src/Haby.Persistence.PostgreSql/PostgreSqlPlaintextSecretStore.cs) | Assigns Environment/ProviderAdministrator metadata; application generation needs explicit typed metadata. |
| [PostgreSqlAdministrativeSecretCatalog.cs](../../src/Haby.Persistence.PostgreSql/PostgreSqlAdministrativeSecretCatalog.cs) | Named administrative creation/receipts remain a distinct producer. |
| [DurableOperationGraph.cs](../../src/Haby.Application/DurableOperationGraph.cs) | Frozen nodes/dependencies and inputs; typed future-output references need implementation. |
| [DurableActionAdmission.cs](../../src/Haby.Application/DurableActionAdmission.cs) | Exact Provider-neutral planning/execution keys and typed codecs. |
| [PostgreSqlDatabaseCreateAction.cs](../../src/Haby.PostgreSql/PostgreSqlDatabaseCreateAction.cs) | Database Create only; no application-login policy yet. |
| [DurableAggregateCompletion.cs](../../src/Haby.Persistence.PostgreSql/DurableAggregateCompletion.cs) | Transactional all-required-nodes completion. |
| [PostgreSqlDatabaseRecoveryJournal.cs](../../src/Haby.Persistence.PostgreSql/PostgreSqlDatabaseRecoveryJournal.cs) | Specialized database-create recovery; do not extend it to new actions by analogy. |

## Concrete manifest and exact contracts

The [example](examples/106-shared-application-login.json) declares one database
and one login shared through bindings by api and worker. It uses
database-owner/v1. Import/planning support must be implemented before the example
is executable; core/extension metadata alone does not enable effects.

| Contract | Accepted fields and constraints |
| --- | --- |
| postgresql.database/v1alpha1 | Existing properties {name}; database-name syntax remains [a-z][a-z0-9_]{0,62}. |
| postgresql.application-login/v1alpha1 | Required {databaseRef: ManifestLocalId, schema: string, privilegePolicy: string}. Same-Application databaseRef; schema follows database-name syntax and cannot start pg_ or equal public/information_schema; policy is exactly database-owner/v1. No unknown fields, password or userName. |
| Login binding options | Exactly {}; bindings do not independently choose credentials or broaden the declared policy. |
| postgresql.database-output/v1alpha1 | {databaseName: string, databaseOid: uint32}, key database; output from the exact confirmed database action. OID is consistency evidence, not ownership proof. |
| postgresql.application-login-output/v1alpha1 | {host, port, databaseName, schema, userName, password: SecretRef}, key connection; publish only after all mandatory login work succeeds. Connection settings come from pinned typed configuration; password is an exact SecretId/SecretVersionId pair. |
| haby.generated-credential/v1 | {reference: SecretRef}, key password; producer output, never plaintext. |
| postgresql.role-evidence/v1 | {roleName: string, roleOid: uint32}, key role; normal success consistency evidence, not an implemented manual recovery capability. |

The PostgreSQL pilot adapter requires exactly one declared database/login pair
and the same Provider alias and exact resolved Provider configuration/implementation.
Reject cross-Application references, additional policies, mismatched Providers
and unsupported options. Two Components consume the same login Resource.
Workload provisioning and confidential document delivery are outside these slices.

This pair is an adapter policy, not a common Application admission shape.
Generated slots, owner/purpose, SecretRefs and operation outputs must work for
credential-bearing Resources without any database. A future RabbitMQ user action
reuses those contracts and emits its own dependencies/permissions rather than
the PostgreSQL five-step graph. PostgreSQL naming, database-owner/SCRAM and SQL
rules must not enter the shared secret/output contracts.

The current one-Provider Plan input is a known limitation, not proof of combined
PostgreSQL/RabbitMQ support. Resource/node-specific exact Provider resolutions
and dependency validation are prerequisites for admitting that combined case.
The pair-admission design must account for this boundary; use a distinct typed
non-database credential action as evidence before claiming the seam is neutral.

The common model owns typed identities, outputs and secret contracts.
The adapter owns dependency extraction, PostgreSQL DTOs, names, SQL and privilege
checks. Keep exact Resource/kind/configuration/implementation/action dispatch;
no reflection, prefix dispatch, database fields in common tables or direct CLI SQL.

## Operation dependencies and outcomes

All five nodes are mandatory and use pinned references. The stable slot name is
application-password; generationProfile is random-password/v1.

| OperationId / action | Frozen non-secret input | Dependencies | Effect and success evidence |
| --- | --- | --- | --- |
| db-create / postgresql.database.create/v1alpha1 | databaseName | none | Existing standalone Create under Provider administration; confirmed response and independent existence/OID read. |
| db-restrict / postgresql.database.restrict/v1alpha1 | databaseName, databaseSource, expectedOwnerUserName | db-create | Verify exact new database, restrict PUBLIC database/public-schema ACLs in one external transaction, confirm commit and owner/ACL evidence. |
| app-login-material / haby.secret.ensure-generated/v1 | owner, slotId, generationProfile | db-restrict | Local same-slot get-or-create; publish exact output with node success/attempt closure. No Provider call. |
| app-login-create / postgresql.application-login.create/v1alpha1 | owner, databaseName, databaseSource, roleName, passwordSource | db-create, db-restrict, app-login-material | Transactional role/password creation with fixed flags and CONNECT; confirm commit, role attributes and exact-password authentication; publish role consistency evidence. |
| app-login-access / postgresql.application-login.configure-access/v1alpha1 | databaseName, databaseSource, schema, policy, roleSource, passwordSource | db-create, db-restrict, app-login-create, app-login-material | One external transaction transfers database ownership to the application login and configures application-owned schema, defaults/search_path. Confirm commit and independent ownership/access evidence; publish final connection output. |

Input codecs remain exact versioned identifiers owned by the action implementation:
haby.postgresql.database-create/v1, haby.postgresql.database-restrict/v1,
haby.generated-slot/v1, haby.postgresql.application-login-create/v1 and
haby.postgresql.application-login-configure-access/v1. Unknown formats/fields
fail safely. Typed output selectors are frozen in the graph:
databaseSource selects db-create/database; passwordSource selects
app-login-material/password; roleSource selects app-login-create/role, each with
its exact output contract. Validate matching producer, generation, owner, Provider
references and dependency edges. No current alias or latest SecretVersion lookup.

Publish output, producer success and attempt closure atomically. Local material
may already be committed when output publication fails: retry resolves the same
slot/reference. It never rotates. External nodes retain the existing durable
Dispatching barrier and aggregate completion. Observe external OIDs only as
consistency evidence; privileged external changes are not authenticated by OIDs.

## Privileges and primary evidence

Policy is based on PostgreSQL 17, matching the current disposable fixture.
These references establish semantics; implementation needs real tests.

- [CREATE DATABASE](https://www.postgresql.org/docs/17/sql-createdatabase.html):
  initial owner defaults to executing role; creation cannot be grouped into
  the later external transaction.
- [Privileges](https://www.postgresql.org/docs/17/ddl-priv.html):
  ownership provides ALTER/DROP authority; database and contained-object
  ownership are different; PUBLIC defaults require explicit consideration.
- [Schemas](https://www.postgresql.org/docs/17/ddl-schemas.html):
  application schema ownership and pg_database_owner/public behavior must be
  explicit; administrator SQL must not trust an application-writable search_path.
- [ALTER DEFAULT PRIVILEGES](https://www.postgresql.org/docs/17/sql-alterdefaultprivileges.html):
  defaults apply to the creating role's future objects. Per-schema revokes
  cannot remove globally granted PUBLIC defaults.
- [CREATE ROLE](https://www.postgresql.org/docs/17/sql-createrole.html):
  role attributes and memberships are separate from database ownership.
- [COMMIT](https://www.postgresql.org/docs/17/sql-commit.html):
  external transaction completion is not atomic with the Haby journal.
- [Password authentication](https://www.postgresql.org/docs/17/auth-password.html)
  and [client password preparation](https://www.postgresql.org/docs/17/libpq-misc.html#LIBPQ-PQENCRYPTPASSWORDCONN):
  verify actual password authentication; client-generated SCRAM avoids original
  cleartext in password DDL but the verifier remains confidential.

| Action/object after all nodes succeed | Application login | Provider |
| --- | --- | --- |
| Own database | Owner, CONNECT and owner administration; may alter/drop its dedicated database | Retains administration; SUPERUSER supported for the pilot |
| Schemas and own tables/indexes/sequences | May create schemas, own/manage them and its own objects, run migrations and DML | Can administer using configured privileges |
| public schema | pg_database_owner ownership on PostgreSQL 17; PUBLIC USAGE/CREATE revoked; owner capabilities remain | Explicitly verifies/configures ownership and ACLs |
| Administrator/template-created contained objects | Not automatically their owner; no blanket reassignment or grants | Keeps their original ownership; unsupported migration paths fail clearly |
| CREATE ROLE/DATABASE, replication, bypass RLS | Denied by fixed negative cluster flags; no administrative memberships | Uses preconfigured authority, no automatic elevation |
| Hardened other Haby database B | CONNECT denied in the configured policy absent later owner/admin changes | As operator-authorized |
| Unrelated database with PUBLIC privileges | May remain connectable; not a cluster-wide isolation promise | Unrelated ACLs unchanged |

A database owner can deliberately change its database ACLs and grant access.
Isolation tests describe the configured state, not confinement of a hostile owner.
The login can also change its own password; drift never causes automatic reset.
Provider SUPERUSER is allowed, and restricted Provider support is deferred.
No registration/adoption of existing resources is enabled by these decisions.

## Uncertainty and deferred recovery

For new restrict/login/ownership/access effects, a lost commit/rollback,
post-marker cancellation, failed success verification or Haby finalization failure
leaves NeedsAttention. Known rejection requires confirmed rollback. No restarted
dispatch, automatic adoption, repair, password reset or ownership transfer.
Existing database-create manual recovery remains exact-action-specific.

New role/access recovery implementation is deferred. Do not add verify-and-assign
commands, provenance attestations or repair workflows in these slices.
Inspection preserves uncertainty and partial progress; no success is fabricated.
Any future resolution needs separate approval. Operators explicitly manage
disposable test cleanup outside Haby; catalog reset is not external rollback.

SCRAM password DDL has a narrowly approved ADR-026 verifier-only exception.
Choose/review the managed implementation and prove interoperability/redaction
before enabling it. Validate client logs and supported configured server logging.
Do not claim universal detection of external logging, hide verifier visibility
from privileged administrators, or silently disable server-wide audit policy.

## Ordered implementation slices

1. **Typed metadata and stable generated slots, no Provider calls.**
   Implement Resource/administrative owner/purpose and atomic get-or-create.
   Prove concurrency, unchanged ref/value on retry/restart, profile conflict,
   missing-payload refusal, rollback, lost-response resolution and current-build
   pg_dump/pg_restore. No renderer or confidential API is needed.
2. **Typed outputs and pair admission.**
   Add output selectors/write-once persistence and the supported pair/bindings.
   Prove missing/wrong/cross-generation output refusal, publication rollback,
   same-slot reuse and unchanged graph fingerprint. Keep external actions gated.
   Keep pair rules adapter-owned and specify typed exact resolutions by Resource
   or node; a credential-bearing non-database fake must not need a database input.
   Do not claim combined multi-Provider admission while a single global selection
   remains its only persisted planning input.
3. **Restrict new database.**
   Add exact target checks, transactional PUBLIC ACL changes and independent
   verification. Test unchanged unrelated ACLs, rollback and uncertain-stop.
   Do not add new recovery. Preserve existing database-create recovery.
4. **Create login with exact persisted password.**
   Add fixed attributes, SCRAM verifier transport, role/CONNECT transaction,
   name-collision refusal and password authentication checks. Prove rollback,
   no secret/verifier leakage in supported logs, and NeedsAttention/no replay
   after ambiguous commit. No password overwrite or role adoption.
5. **Database ownership/access and real application operations.**
   Transfer ownership, configure explicit schemas/defaults/search_path and enable
   the complete supported graph. Use the actual generated login with EF Core:
   create schema and migration history, tables, indexes and sequences; apply a
   second structural migration and DML. Independently verify owner/flags, deny
   cluster privileges and CONNECT to managed B, and demonstrate that unrelated
   PUBLIC CONNECT may remain. Do not preserve the previous CREATE SCHEMA/TEMP/
   DROP-own-database denial expectations. Current-build retry/restart/restore
   retain exact material and skip success; uncertain new actions stay blocked.
6. **Later decisions.**
   Confidential Configuration rendering/API delivery remains separate. New
   role/access recovery, schema-migrator, restricted Provider administration,
   registration of existing resources, rotation and deprovisioning are deferred.

Run each slice's focused and applicable solution/PostgreSQL/format/diff gates;
Native AOT where transport changes. ADR-033 permits explicit fresh catalogs,
not alpha compatibility bridges. No implementation is delivered by this docs PR.
