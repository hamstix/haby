# Confirmed database evidence and PUBLIC restriction

Issue #114 implements the next external-effect slice under ADR-025/026/032/033/034.
The production Host still admits and executes only standalone database Create.
The complete five-node database/login pair remains unavailable: common execution
checks every mandatory executor before any partial effect, and the direct Create
entry refuses multi-node graphs. No placeholder login/access executor is supplied.

## Exact CREATE evidence

After an acknowledged CREATE DATABASE, a separate non-pooled session reads the
new non-template database's OID. The adapter encodes the strict generated payload
`{databaseName: string, databaseOid: uint32}` under
`postgresql.database-output/v1alpha1`, key `database`. Zero OIDs, invalid names,
unknown fields and incompatible payloads are refused. An OID is consistency
evidence; it does not authenticate provenance or ownership against privileged
external replacement.

The common catalog envelope contains no PostgreSQL fields. One C#/EF transaction
conditionally owns the exact Dispatching/current-attempt row, publishes write-once
evidence, records producer success, closes that dispatched attempt successfully
and advances AppliedGeneration only when all mandatory nodes succeeded. Ordinary
journal success cannot bypass a registered mandatory-output producer. Identical
publication for the same successful attempt is idempotent; another attempt or
conflicting payload cannot overwrite it. A lost catalog commit response may be
confirmed only by an independent read of that exact output, operation and attempt.

Failed output/attempt/aggregate publication rolls back the whole catalog change.
Externally created databases are then uncertain effects: retained Dispatching
becomes NeedsAttention on explicit execution without another CREATE. Reads,
existence checks and retries never synthesize missing evidence.

### Explicit standalone manual-recovery exception

The maintainer confirmed this exception while implementing #114:

- Existing AssignExistingDatabase retains its prior standalone semantics: the
  operation becomes Succeeded, the original attempt remains Uncertain and the
  immutable recovery decision records the operator's consent.
- Assignment publishes no database output. Current existence or a newly read OID
  cannot manufacture a confirmed CREATE result.
- Mandatory atomic evidence publication applies to ordinary confirmed CREATE
  with a successfully closed dispatched attempt. Missing output after explicit
  standalone assignment is permitted, and observation/retry/restart do not add
  output, rewrite history or repeat CREATE.
- Restrict requires both exact database output and a confirmed successful producer
  attempt. Succeeded alone, including recovered Create, never authorizes it.
- Database-create recovery is not a restrict or complete database/login recovery
  workflow. Uncertain new effects have no assignment, repair or retry command.

## Restrict execution and SQL boundary

`AddPostgreSqlDatabaseRestrict` is an opt-in adapter effect registration. It neither
registers pair admission nor activates production pair execution. The executable
internal fixture contains only real Create -> restrict effects on one declared
database Resource. The current restrict handler refuses other graph shapes;
complete pair activation belongs to the later login/access slice.

The exact action is `postgresql.database.restrict/v1alpha1`, input format
`haby.postgresql.database-restrict/v1`. Its frozen databaseName, databaseSource and
expectedOwnerUserName select db-create/database with the exact database contract.
Consumption rederives committed deployment/generation, graph fingerprint,
manifest Resource and pinned Environment/Provider/revision/implementation.
The adapter validates the producer action, direct dependency, shared Resource,
exact input name and selector, and the expected administrator from the pinned
configuration. Generated passwords retain their separate owner/slot/version
consumption rules; allowing database evidence does not grant cross-owner secrets.

Before dispatch the target resolves the saved administrator SecretRef and checks
the exact database name/OID, non-template status, administrative owner and
`public` schema owner `pg_database_owner`. It never resolves a current alias or
adopts a similarly named database. A fresh dedicated connection to that database
starts one external transaction, sets `search_path = pg_catalog`, rechecks identity
and executes quoted `REVOKE ALL PRIVILEGES ON DATABASE ... FROM PUBLIC` and
`REVOKE USAGE, CREATE ON SCHEMA public FROM PUBLIC`. Values in catalog queries are
parameters. These statements are localized PostgreSQL adapter capabilities under
ADR-026; there are no new stored functions, procedures, triggers, role creation,
ownership transfer, HBA changes or mandatory catalog/runtime principal split.

After acknowledged commit an independent session checks name/OID, database owner,
public schema owner and PUBLIC ACL entries. It checks PUBLIC entries rather than
mistaking owner/superuser bypass for permission denial. SUPERUSER administration
is allowed; restricted administrator support is deferred.

## Failure, exposure and limitations

A committed durable Dispatching marker precedes mutating SQL. Known server
rejection is definitive only with acknowledged external rollback. Unknown commit
or rollback, post-marker cancellation, failed verification and failed catalog
finalization remain NeedsAttention or DispatchUncertain. Repeated execution and
restart never redispatch uncertain restrict SQL. Earlier Create evidence remains
retained, and pending/failed/uncertain mandatory work prevents AppliedGeneration.
No automatic adoption, credential repair, ownership repair or external cleanup
exists. Ordinary results contain bounded reasons, never credentials or driver text.

CREATE and restrict cannot share a PostgreSQL transaction. The initial PUBLIC
access window therefore remains: use an isolated bootstrap target without
untrusted clients. Unrelated databases/templates retain their ACLs, owners and
objects. This is not cluster-wide isolation, nor protection against a privileged
administrator replacing resources or deliberately changing ACLs.

## Catalog reset and validation

This slice requires an explicitly initialized fresh catalog under ADR-033. The
empty `RequireConfirmedDatabaseEvidence` migration is a catalog-baseline marker:
earlier successful Create histories lack mandatory evidence and are not backfilled
or accepted as current-build confirmed producers. No table/column/model change is
needed. Startup rejects earlier migration sets instead of automatically resetting
or upgrading them. A catalog reset does not remove previously created external
databases; operators must explicitly decide how disposable resources are handled.

Current-build restart and real pg_dump/pg_restore retain the accepted graph,
database output and operation/attempt states. Restored uncertain nodes do not
dispatch automatically. Real PostgreSQL tests cover PUBLIC denial using a
non-superuser probe, unchanged unrelated/template state, same-name/OID conflict,
external rollback and uncertain boundaries, exact selectors and attempts,
catalog atomicity, checked alias switching and the standalone recovery exception.
Persisted arrange/act/assert use independent contexts.

Deferred: login/SCRAM, ownership/application schema/default privileges/search_path,
final connection output, complete pair production activation, new recovery,
restricted-admin support, confidential delivery, rotation/deletion, multi-Provider
admission, RabbitMQ effects, authentication and exposure changes.
