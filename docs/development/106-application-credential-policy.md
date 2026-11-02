# Issue 106: application credential policy proposal

Status: design evidence for **Proposed** [ADR-034](../adr/0034-application-credentials-and-postgresql-role-policy.md).
No Resource/action/output below is implemented or authorized by this document.
Base: `43cd89ebdf67d30d3f70a64dea1b94dc3d0a5e18`, after merged PR #105.
Readiness is for maintainer policy decisions, not runtime readiness.

## Current source inventory

Paths refer to that exact base; symbols are search anchors, not historical API promises.

| Source | Observed boundary / required change |
| --- | --- |
| [SecretStore.cs](../../src/Haby.Application/SecretStore.cs), `ISecretStore.CreateAsync` | Environment, reference and bytes only; lacks typed owner/purpose and generated-slot get-or-create. |
| [PostgreSqlPlaintextSecretStore.cs](../../src/Haby.Persistence.PostgreSql/PostgreSqlPlaintextSecretStore.cs), `CreateAsync` | Writes Owner=Environment, Purpose=ProviderAdministrator. Keeps payload separate and records protection format. |
| [PostgreSqlAdministrativeSecretCatalog.cs](../../src/Haby.Persistence.PostgreSql/PostgreSqlAdministrativeSecretCatalog.cs), `CreateAsync` | Named administrative ownership/receipts; not a generated application slot. |
| [ApplicationManifest.cs](../../src/Haby.Manifest/ApplicationManifest.cs), `ResourceDeclaration`, `BindingDeclaration` | Explicit properties/options extension boundaries; no implicit generated password fields. |
| [PostgreSqlDatabaseContract.cs](../../src/Haby.Manifest/PostgreSqlDatabaseContract.cs) | Closed `postgresql.database/v1alpha1` properties contain only name. |
| [PostgreSqlDatabaseCreateAction.cs](../../src/Haby.PostgreSql/PostgreSqlDatabaseCreateAction.cs) | Frozen database-name input, exact registration and database Create only. |
| [DurableOperationGraph.cs](../../src/Haby.Application/DurableOperationGraph.cs) | Canonical graph v1, dependencies and pinned references; no operation-output addressing. |
| [DurableActionAdmission.cs](../../src/Haby.Application/DurableActionAdmission.cs) | Provider-neutral exact admission/execution keys and typed codecs. |
| [PostgreSqlProviderConfiguration.cs](../../src/Haby.PostgreSql/PostgreSqlProviderConfiguration.cs) | Adapter-owned connection settings and exact administrator SecretRef. |
| [PostgreSqlDurableOperationExecutor.cs](../../src/Haby.PostgreSql/PostgreSqlDurableOperationExecutor.cs) | Preparing/Dispatching barrier, independent database verification and no restarted dispatch replay. |
| [PostgreSqlDatabaseGateway.cs](../../src/Haby.PostgreSql/PostgreSqlDatabaseGateway.cs), `CreateDatabaseAsync` | Standalone quoted CREATE DATABASE; no explicit OWNER, so executing administrator owns it. |
| [DurableGenerationRunner.cs](../../src/Haby.Application/DurableGenerationRunner.cs) | Explicit sequential passes, dependency checks, stop/resume. |
| [DurableAggregateCompletion.cs](../../src/Haby.Persistence.PostgreSql/DurableAggregateCompletion.cs), `TryAdvanceAsync` | Locks deployment and checks every required node before AppliedGeneration. |
| [PostgreSqlDatabaseRecoveryJournal.cs](../../src/Haby.Persistence.PostgreSql/PostgreSqlDatabaseRecoveryJournal.cs) | Existing specialized database recovery is not role/password recovery. |
| [104-durable-operation-graph.md](104-durable-operation-graph.md) | Delivered graph evidence and current-model boundary. |

## Concrete manifest and exact proposed contracts

The [manifest example](examples/106-shared-application-login.json) uses the current
`haby.dev/v1alpha1` envelope and existing binding target syntax. Its new login
Resource is deliberately unsupported on the inspected build: import/planning
must not pretend that registering these words in documentation enables execution.
The example declares one database and one login shared by api and worker, both
Application-owned, with retain lifecycle. No generated material/reference exists
in the portable source manifest. No configuration document or delivery endpoint
is needed to test this ownership/consumption example.

Closed typed contract definitions proposed for approval:

| Contract | Exact fields and constraints |
| --- | --- |
| `postgresql.database/v1alpha1` | Existing properties `{name: string}`; `[a-z][a-z0-9_]{0,62}`. Existing shape is unchanged. |
| `postgresql.application-login/v1alpha1` | Properties `{databaseRef: ManifestLocalId, schema: string, privilegePolicy: string}`. All required; schema follows database-name syntax but cannot start `pg_` or equal `public`/`information_schema`; policy is exactly `schema-migrator/v1`. No unknown fields/password/userName. |
| Login binding options `postgresql.application-login.binding/v1alpha1` | Exactly `{}`. Resource selects privileges; a Component cannot broaden them. Both example bindings consume the same login outputs. |
| Database binding options | Existing database-only shape is not expanded by the example. |
| Database output `postgresql.database-output/v1alpha1` | `{databaseName: string, databaseOid: uint32}` from successful exact database action, output key `database`; OID is internal evidence. |
| Login output `postgresql.application-login-output/v1alpha1` | `{host: string, port: integer, databaseName: string, schema: string, userName: string, password: SecretRef}`. Host/port come from pinned typed Provider configuration; password is exactly `(SecretId, SecretVersionId)`, never text or latest. Output key `connection`; publish only after all login nodes succeed. |
| Internal `haby.generated-credential/v1` | `{reference: SecretRef}`, output key `password`; write-once, scoped to producer node. |
| Internal role evidence `postgresql.role-evidence/v1` | `{roleName: string, roleOid: uint32}`; key `role`. Scope includes the node's exact ProviderInstance/configuration, never a cluster-global OID claim. |

`databaseRef` is an adapter-declared same-Application Resource dependency, not a
Component binding or free-form string lookup at execution. Require exactly one
referenced database, one login for it and the same Provider alias/resolved exact
Provider configuration/implementation for both Resources in this pilot. Reject
cross-Application references, ambiguous/multiple login policies, schema sharing,
Provider mismatch, unsupported options and unknown versions before acceptance.
This extends the current one-Resource admission limit for this exact pair only.

Common code owns typed identities, dependency/output addressing and secret-store
contracts. The PostgreSQL adapter owns dependency extraction from these properties,
SQL, role names, input DTOs and privilege checks. Register exact Resource +
`postgresql/v1` + `haby.postgresql.connection/v1` + implementation + action keys;
no prefix dispatch, reflection discovery or generic host/password columns.

## Operation dependencies and outcomes

All rows are mandatory. Each includes the existing pinned input references.
Here `db` and `app-login` are stable ResourceLocalIds; owner/slot means ADR-034's
typed tuple, not a display name. All displayed payloads are closed typed DTOs.
Exact input codecs are `haby.postgresql.database-create/v1`,
`haby.postgresql.database-restrict/v1`, `haby.generated-slot/v1`,
`haby.postgresql.application-login-create/v1` and
`haby.postgresql.application-login-configure-access/v1`, respectively.
Unknown versions/fields fail closed; output codecs use their exact output contract IDs.

| OperationId / exact action contract | Frozen non-secret input | Depends on | Effect, durable output and success evidence |
| --- | --- | --- | --- |
| `db-create` / `postgresql.database.create/v1alpha1` | `{databaseName}` | none | Existing standalone CREATE; confirmed response and independent existence read. Proposed output publication additionally records databaseName and observed OID with success. Database ownership still needs confirmation before following actions. |
| `db-restrict` / `postgresql.database.restrict/v1alpha1` | `{databaseName, databaseSource: OperationOutputRef, expectedOwnerUserName}` | `db-create` | Confirm assigned database/provenance and administrator ownership; one transaction revokes all PUBLIC database privileges and PUBLIC USAGE/CREATE on public schema. Independent owner/ACL read confirms. No unrelated database touched. |
| `app-login-material` / `haby.secret.ensure-generated/v1` | `{owner, slotId, generationProfile}` | `db-restrict` | Local atomic same-slot get-or-create; publish exact generated-credential/password output with node success. No Provider call. |
| `app-login-create` / `postgresql.application-login.create/v1alpha1` | `{owner, databaseName, databaseSource: OperationOutputRef, roleName, passwordSource: OperationOutputRef}` | `db-create`, `db-restrict`, `app-login-material` | One transaction CREATE ROLE with exact password and fixed flags, plus CONNECT on this database. No memberships granted to login. Confirm COMMIT, independently inspect flags/memberships/OID and authenticate as exact login/database using the stored version; publish role evidence with success. |
| `app-login-grants` / `postgresql.application-login.configure-access/v1alpha1` | `{databaseName, databaseSource: OperationOutputRef, schema, policy, roleSource: OperationOutputRef, passwordSource: OperationOutputRef}` | `db-create`, `db-restrict`, `app-login-create`, `app-login-material` | Require same owned role OID and exact password. One transaction creates administrator-owned schema, grants login USAGE/CREATE, applies creator defaults and database-local search_path. Confirm COMMIT plus independent catalog checks and authenticated read-only session; publish final typed Resource output with node success. |

`expectedOwnerUserName` is resolved from the pinned configuration at Plan time;
the adapter validates it against that exact revision at execution. No Provider
payload is copied into common fields. The derived roleName and owner key are
frozen before acceptance. Material input needs no SecretRef yet: consumers freeze
`OperationOutputRef(app-login-material, haby.generated-credential/v1, password)`.
The roleSource selects `app-login-create`, `postgresql.role-evidence/v1`, `role`.
Every databaseSource selects `db-create`, `postgresql.database-output/v1alpha1`,
`database`; require the same observed OID before each subsequent mutation. OIDs
are consistency evidence, not ownership proof against privileged external changes.
Dependencies and output selectors are both fingerprinted and validated before
Commit. The slot association supplies the durable exact reference at runtime;
outputs never amend the graph or the accepted generation snapshot.

The local operation uses the login Resource's exact admission context, not a
separate remote Provider. It must validate the referenced Application/Environment
ownership and serialize all slot writes transactionally even with direct calls.
Repeated material execution after output-finalization rollback reuses the winner.
Slot commit precedes role/password dispatch. Ordinary progress/CLI results expose
safe IDs, references and reasons only; no password, verifier, connection string,
hash of material or raw driver detail. Internal outputs contain exact SecretRefs,
not a copy of the payload. Missing handlers/output codecs fail closed while safe
metadata/history reads remain available.

| Interruption/outcome | Allowed next behavior |
| --- | --- |
| Before local slot commit | Roll back; explicit retry may create one winning slot. |
| Lost slot response / output publication rollback | Read same slot; return/publish the exact original SecretRef; only scoped material resolution reads its original bytes. If material unavailable, refuse. |
| External preparation cannot resolve pinned input, output or material | Close attempt before dispatch with safe category; restore exact input before explicit retry. Invalid immutable input/name collision is definitive. |
| Durable marker commit not acknowledged | Send no external SQL. Read journal; if Dispatching survived, NeedsAttention. |
| Server rejects statement and transaction rollback is confirmed | Record definitive Conflict/ServerRejected; no auto-retry/adoption. Failure to confirm rollback is uncertain. CREATE DATABASE keeps its current standalone rejection policy. |
| Lost COMMIT, timeout, cancellation after marker, verification failure or Haby success write lost | NeedsAttention, even when external state looks complete. No mutating redispatch. |
| Restart after some successful nodes | Keep attempts/outputs; never repeat successful nodes. Stop pass at unresolved failure/uncertainty, including ready siblings. |
| Manual resolution of one node | Immutable consent/evidence receipt and node transition; aggregate completion checks all siblings. Replay never rechecks PostgreSQL. |

For restrict recovery, recheck exact database provenance/owner and both database
and public-schema ACLs. For role recovery, recheck provenance, OID, fixed flags,
memberships, CONNECT and exact-password login. For grants recovery, additionally
check schema ownership/ACL, creator default ACL and search_path. A disposable
acceptance test executes permitted/denied DDL/DML; production verification/recovery
does not create probe objects. Use fresh non-pooled password sessions and
`current_user`, `session_user`, `current_database()` checks; verify SCRAM HBA and
a wrong-password rejection so trust/peer/certificate routes cannot fake evidence.
Evidence gathering cannot reserve the external target against an administrator;
operator consent requires a controlled no-concurrent-change window. Persist safe
fact categories, identities and actor/time, never password/verifier content.

## Privileges and primary evidence

Checked against PostgreSQL **17** documentation on 2026-11-02, matching the
repository's `postgres:17.6-alpine` disposable fixture. These are documentation
and static-source findings, not experiments run for this PR.

| Topic / primary source | Design implication |
| --- | --- |
| [CREATE DATABASE, Description and Notes](https://www.postgresql.org/docs/17/sql-createdatabase.html) | Default owner is executing role; CREATE DATABASE cannot run in a transaction block. Database ACLs are not copied from its template. Restrict is a separate effect with an unavoidable bootstrap window. |
| [Privileges, ownership and default PUBLIC grants](https://www.postgresql.org/docs/17/ddl-priv.html) | Ownership conveys alter/drop authority, while database CONNECT/TEMP and routine EXECUTE default to PUBLIC. Login flags alone cannot establish cluster isolation. |
| [Schemas, sections 5.10.3-5.10.4](https://www.postgresql.org/docs/17/ddl-schemas.html) and [CREATE SCHEMA](https://www.postgresql.org/docs/17/sql-createschema.html) | Schema USAGE and CREATE differ; schema owner need not own contained objects. public defaults vary with database history. Check/revoke explicitly; never trust app schema in administrator search_path. |
| [ALTER DEFAULT PRIVILEGES, Description](https://www.postgresql.org/docs/17/sql-alterdefaultprivileges.html) | Only future objects of the creating current role are affected; defaults do not inherit from role memberships. Per-schema revokes cannot remove global PUBLIC defaults. Application routine/type default revokes are database-wide for this creator. |
| [CREATE ROLE, parameters and Notes](https://www.postgresql.org/docs/17/sql-createrole.html) and [GRANT](https://www.postgresql.org/docs/17/sql-grant.html) | Roles are cluster-scoped; flags, membership and object grants are separate. Supply fixed negative flags and check memberships. No implicit database ownership or extension permission follows. |
| [Protocol message flow, simple queries and transaction status](https://www.postgresql.org/docs/17/protocol-flow.html#PROTOCOL-FLOW-SIMPLE-QUERY) and [COMMIT](https://www.postgresql.org/docs/17/sql-commit.html) | Transaction-capable role/schema/grant DDL may be grouped under explicit BEGIN/COMMIT. Statement CommandComplete alone does not prove transaction commit; lost external COMMIT cannot be reconciled atomically with the Haby journal. Disposable rollback/commit-loss tests remain required. |
| [Password authentication](https://www.postgresql.org/docs/17/auth-password.html) and [client password encryption](https://www.postgresql.org/docs/17/libpq-misc.html#LIBPQ-PQENCRYPTPASSWORDCONN) | Require password-authenticated probe route. Use client-generated SCRAM verifier to avoid cleartext DDL; verifier transport/logging is still confidential and needs the proposed narrow ADR-026 exception. The C API documents semantics, not a selected .NET dependency. |

Recommended `schema-migrator/v1` matrix after **all** nodes succeed:

| Action/object | Generated application login | PUBLIC / another application login | Provider administrator |
| --- | --- | --- | --- |
| Authenticate / CONNECT to own managed database | Yes, exact stored password, explicit CONNECT | No database CONNECT after restriction, absent separate explicit grants | Owner access |
| CONNECT to separately hardened Haby database B | No | Only B's expressly authorized login | As authorized by operator |
| CONNECT to unrelated default database | Possibly via PUBLIC; no guarantee of denial | Possibly | Not modified by these actions |
| Database ownership, DROP/ALTER DATABASE, CREATE SCHEMA, TEMP tables | No; no ownership, database CREATE or TEMP | No grant from this policy | Database owner handles administration |
| Application schema | USAGE/CREATE; no schema ownership/drop | No USAGE/CREATE | Owns schema |
| public schema creation/access | No USAGE/CREATE after restriction | No USAGE/CREATE from PUBLIC | Retained administration |
| App-created current/future tables | Owner: DML, REFERENCES/TRIGGER, ALTER/DROP and grant authority | No default table grants; app owner could deliberately grant | According to actual admin authority |
| App-created current/future sequences | Owner: usage/read/update/alter/drop | No default sequence grants | According to actual admin authority |
| App-created routines/types | Owner use; future PUBLIC EXECUTE/USAGE defaults revoked | No app-schema USAGE; no new PUBLIC defaults | According to actual admin authority |
| Administrator-created tables/sequences in app schema | No automatic access; unsupported creator path | No new grants | Creator owns them; separate grants would be required |
| CREATE ROLE/DATABASE, replication, bypass RLS, superuser actions | Denied; no memberships/elevation | No grants from this policy | Only operator-prepared privileges |

An application login can change its own password under PostgreSQL role rules.
Stable stored material is not a promise against external drift: a mismatch stops
verification/recovery and never triggers a reset. See [ALTER ROLE, Description](https://www.postgresql.org/docs/17/sql-alterrole.html).

The administrator must already be allowed to create databases/roles, administer
their ACLs, configure the new role and its defaults, and inspect required evidence.
CREATEDB/preflight success alone proves none of the other capabilities. Fail
before dispatch where capability can be determined; otherwise report confirmed
rejection/uncertainty honestly. This policy never grants administrator elevation.
Disposable tests may use their isolated container superuser. No promise that
non-superuser role administration implies unrestricted access to app-owned data.
Extensions requiring database CREATE/superuser and migrations outside the chosen
schema are unsupported. No global changes to template databases or HBA are made.

## Ordered implementation slices

Start only after corresponding ADR-034 choices become Accepted. Each slice stays
off the production admission path until its complete prerequisites are delivered.
Use separate arrange/act/assert DbContexts for persisted tests and a fresh
disposable PostgreSQL instance; never a developer/customer database.

1. **Typed secret metadata and stable generated slots, no Provider calls.**
   Replace hardcoded producer metadata with typed owner/purpose; retain explicit
   administrative behavior. Persist unique slots and atomic get-or-create. Prove
   concurrent callers converge on one reference/value, distinct owners isolate,
   conflicting profile refuses, missing payload never regenerates, rollback leaves
   no partial Secret, and commit-response loss/restart returns the exact winner.
   Include current-build pg_dump/pg_restore of slot/material. No confidential API,
   renderer or external role is needed for this first slice.
2. **Typed durable output references and pair admission.** Add closed input/output
   definitions, graph validation and write-once output persistence; test through
   real Plan/Check/Commit with typed fake effects. Prove changed fingerprint on
   selector change, missing/wrong/cross-generation output refusal, output-finalize
   rollback, unchanged graph bytes on restart and same-slot reuse. Validate sample
   parsing and two bindings consume one Resource, not two generated slots. Reject
   unsupported multi-database/provider combinations. Keep external actions gated.
3. **Restrict new database action.** Add exact ownership checks, transaction,
   marker, verification and consented recovery. Disposable tests prove PUBLIC ACL
   removal only on this database, unrelated ACL digest unchanged, rollback, crash
   before send/after commit, no uncertain redispatch and incomplete aggregate.
   Retain existing database conflict/manual recovery behavior.
4. **Role creation with exact password, narrowly approved DDL transport.** Use
   the generated slot/output and fixed flags; transaction includes CONNECT only
   on owned database. Prove role/password/grant rollback together, deterministic
   name collision never changes pre-existing password, lost COMMIT/success write
   stays NeedsAttention, wrong-password route fails, and affirmative consent plus
   provenance/authentication resolves only the exact node. Capture client logs,
   diagnostics and configured server logs to prove no cleartext/verifier leakage.
   Prove approved managed SCRAM construction against real PostgreSQL before use.
5. **Schema/grants and complete real generated-role path.** Deliver defaults,
   search_path, independent evidence and specialized recovery. Enable only the
   complete supported graph. In disposable PostgreSQL, import the concrete pair,
   Plan/Check/Commit/execute through Host and API-only published Native AOT CLI;
   a test-only authorized secret consumer opens a fresh connection with the exact
   generated version, checks identity, creates/alters/drops its own table, inserts,
   selects/updates/deletes rows and uses an identity sequence. It creates a second
   future table/sequence and routine/type to prove creator/default rules. Check
   failure of CREATE ROLE/DATABASE/SCHEMA, TEMP, public-schema writes, schema drop,
   admin-object access and CONNECT to hardened database B. Demonstrate separately
   that an unrelated database with PUBLIC CONNECT may remain connectable.
   Restart/retry preserves exact refs and skips successes; recover one node with
   a required sibling incomplete and AppliedGeneration stays unset. Backup/restore
   partial state using pg_dump/pg_restore; read/receipt replay has zero Provider
   effects, explicit resume completes remaining nodes without credential rotation.
6. **Separately approve confidential configuration delivery.** Only then define
   document selection/scope, permitted consumer, transport and audited just-in-time
   materialization. A future pure renderer may project the typed login output to
   a SecretReference-bearing tree under ADR-017. No unrestricted secret API, generic
   reveal or plaintext revision storage follows from this proposal. The full
   ROADMAP usable-alpha exit still requires this separately approved client path.

For each coding slice run its focused tests, applicable solution restore/build/
test/format and disposable PostgreSQL checks; use Native AOT gates when transport
changes. Document explicit fresh-catalog requirements under ADR-033, not legacy
adapters, numeric aliases or old-alpha migration tests. This documentation PR
changes no schema, code, tests, dependencies or infrastructure and runs no
PostgreSQL experiments. Its checks are links, JSON syntax, CRLF, scope and diff.
