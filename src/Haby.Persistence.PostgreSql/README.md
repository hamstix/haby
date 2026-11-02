# PostgreSQL manifest catalog

This project is the EF Core persistence adapter for accepted Applications,
immutable ManifestRevisions and Provider configuration revisions. It does not
persist deployments or execute Providers. `IManifestImportStore` retains the
existing import use case;
`IManifestRevisionQuery` provides asynchronous inspection without changing the
checkpoint planner's synchronous in-memory reader.

`AddPostgreSqlPlaintextSecretStore()` explicitly registers the internal
PostgreSQL `Plaintext` implementation. It is not registered by the host or by
`AddPostgreSqlManifestCatalog`, so this slice does not select a global write
mode or expose a credential endpoint. Callers must supply a trusted, already
authorized `EnvironmentRecordId`; this storage contract does not authenticate
or authorize operators. It accepts only Environment-owned, managed Provider
administrator credentials. Each `SecretRef` names one immutable version.
The payload is stored as `bytea` in `secret_payloads`, apart from Secret and
version metadata. Reads verify both Secret/version and Secret/Environment
membership and reject unknown protection formats or format versions.

`AddPostgreSqlProviderConfigurationStore()` registers the internal asynchronous
Provider instance/revision store separately from the shadow-preview path. The
generic instance stores an Environment, a versioned Provider kind and a
versioned opaque target identity. Revisions store an exact implementation ID,
shape ID, JSON payload-format version and immutable text snapshot. Stable-slot
SecretRef association rows are inserted in the same transaction and have
Environment and exact SecretVersion foreign keys. The store verifies ownership
using metadata without selecting payload bytes. Registered typed codecs
validate snapshots, derive target identity and compare exact associations when
reading a revision. A deferred seal guard rejects incomplete revision commits;
after sealing, association inserts and all revision edits are rejected by the
database. `Haby.PostgreSql` owns the first closed PostgreSQL shape,
source-generated JSON codec and host/port target rule; another Provider shape
does not alter these tables. No alias, connection test or live Provider
execution is involved.
Internal PostgreSQL callers register `AddPostgreSqlProviderConfigurationCodec()`
and the checkpoint capability alongside the store.

**Plaintext is not encrypted.** Readers of `secret_payloads` and database
backups can recover the credential bytes. Restrict database roles, backup
readers, storage and retention accordingly, and prefer an encrypted mode when
those readers are in the threat model. `pg_dump` archives of this catalog are
secret-bearing assets. The disposable restore test verifies exact bytes after
restore and removes its test archive; operators must protect their own backups.

Register the adapter with `AddPostgreSqlManifestCatalog(connectionString)`.
Before a single-instance pilot host accepts requests, await
`ManifestCatalogMigrationInitializer.MigrateAsync(cancellationToken)`. A failed
migration must fail startup. Do not call `EnsureCreated`. The database principal
needs DDL rights for EF's migration history table and catalog tables,
indexes and constraints, plus normal read/write rights. The pilot deployment
must restrict schema migration to one Haby instance.

## Durable one-Application planning

`AddPostgreSqlDurableDeploymentPlanner()` registers the asynchronous
`IDurableDeploymentPlanner`. A ChangeSet pins one exact Manifest revision and
ExpectedAbsence. Updates and generation 2 remain unsupported. Production planning
accepts one PostgreSQL database Resource without Components/Workloads, exports or
parameters. Exact registered planners emit the immutable operation graph and
versioned non-secret action inputs; no credentials or Provider configuration
payload enter the Plan. Generic code validates identity/dependencies and exact
references without interpreting database properties.

Plans expire after 24 hours. Closure is an independent immutable record. Closed
and expired Plans remain readable but cannot be committed. Check re-evaluates
freshness, exact support and input references without changing/replanning the
stored graph. An alias switch makes the prior Plan stale; recalculation creates a
new Plan. Durable deployments make competing ExpectedAbsence intent stale.
Planning and checking perform no Provider I/O or secret-material reads.

The committer independently checks freshness while holding the locks described
below. Unsupported current snapshot/graph formats fail closed. Development
catalogs from before the graph revision require explicit reinitialization under
ADR-033; startup never rewrites or silently resets them. EF tooling uses
`ManifestCatalogDesignTimeFactory` without opening a database connection.

## First durable desired generation

`IDurableDesiredStateCommitter` accepts only a persisted one-Application
`ExpectedAbsence` Plan. Acceptance stores one deployment, immutable
generation-1 snapshot and one ChangeSetCommit. The snapshot contains exact
Manifest and Provider identities, explicit `Present` presence, alias provenance
and the Plan fingerprint plus frozen graph and action inputs. It has no Provider
configuration payload or credential. AppliedGeneration stays null at acceptance.

Commit uses a PostgreSQL transaction at READ COMMITTED. It locks the ChangeSet
row, then takes a transaction-scoped advisory lock keyed by Environment and
Application to serialize competing expected-absence commits before a deployment
row exists. A unique deployment index backs up that ordering. It reads an
existing ChangeSetCommit before validating the requested Plan, so retries return
the original Plan and generation. The Plan row is locked against closure, and
the alias row is locked `FOR SHARE` through exact-input comparison and all
writes. Alias compare-and-swap updates therefore serialize with commit: a
switch ordered first makes the old Plan stale; one ordered later preserves the
old pinned revision. The generation and commit rows reject update and delete.
The alias store retains its existing two-second database lock timeout: a switch
that cannot acquire the alias row lock returns `StoreUnavailable`, and the
operator may retry it after the commit finishes. This is not a reservation or
an external Provider effect.

The desired-generation transaction also inserts every initial Pending operation
in `durable_create_operations`. Its composite key is deployment + generation +
OperationId; the immutable graph separately records owning Resource, action
contract and dependencies. Production emits one database Create, while typed
tests compose multiple steps for one Resource. No operation starts before the
entire graph commits. Journal reads never resolve a current alias, and this
acceptance transaction never dispatches SQL or advances AppliedGeneration.

Before applying a reviewed migration, stop catalog writes and take a verified
database backup. With PostgreSQL client environment variables supplied by the
operator's secret management, a PowerShell procedure is:

```powershell
pg_dump --format=custom --file=manifest-catalog.dump --dbname=$env:PGDATABASE
createdb haby_catalog_restore_check
pg_restore --no-owner --no-privileges --dbname=haby_catalog_restore_check manifest-catalog.dump
```

Use a separate empty restore-check database and compare Application/revision,
Environment, Secret, version and payload row counts plus
`__EFMigrationsHistory` to the source. Confirm the restored revision and exact
SecretVersion can be read before relying on the backup. The configured migration principal
must have `USAGE` and `CREATE` on the target schema and ownership or equivalent
DDL rights on existing catalog objects. Check schema privileges with
`SELECT has_schema_privilege(current_user, 'public', 'USAGE'),
has_schema_privilege(current_user, 'public', 'CREATE');` and exercise migrations
against an isolated copy with the actual pilot principal. Do not store
connection strings or backups in this repository.

Migration `InitialManifestCatalog` creates Applications and revisions together
with database uniqueness on logical Application ID, Application ID plus version,
and Application record ID plus local revision number. The adapter serializes
imports of one Application through a transaction-scoped PostgreSQL advisory
lock, then commits the first Application and revision in one transaction.
Database constraints remain the final integrity guard. A failed database write
propagates as an infrastructure error; it is not converted into a content
conflict.

The declaration snapshot is internal JSON **format version 1**. It records the
complete typed normalized graph under **normalized model version 1**, with
exact numbers encoded as canonical decimal coefficient and exponent text.
Input API version, historical warnings and first-import actor/time are retained
alongside it. Rehydration checks the format and model versions and fails on
unsupported or corrupt snapshots. No original manifest parser or current
extension catalog is consulted. Any encoding change requires a new format
version and a migration or compatibility reader. The snapshot is not the
public manifest contract or an equality shortcut.

`imported_at` is stored as PostgreSQL `timestamptz` with microsecond precision.
Sub-microsecond .NET ticks may be rounded during a database round trip, so the
read-back time need not match the original `DateTimeOffset` tick for tick.

The real PostgreSQL integration tests use the existing disposable container
suite and Windows WSLc helper documented in
[`docs/development/postgresql-integration-tests.md`](../../docs/development/postgresql-integration-tests.md).

## Persisted operation graph and dispatch barrier

The graph revision under ADR-032/033 requires a fresh catalog. The migration
initializer rejects pre-graph/unknown migration histories without modifying them.
Never reset a developer catalog automatically; external databases/roles survive
catalog reinitialization and do not become owned by the new catalog.

`DurablePlan.Graph` is a canonical immutable graph, encoded with generated JSON
metadata on both Plan and accepted generation. `OperationId` identifies a step
within deployment + generation; Resource ownership and exact versioned action
contract remain separate node fields. Explicit dependencies and frozen typed,
versioned non-secret payloads participate in the deterministic fingerprint.
Execution decodes those payloads without consulting current aliases, manifest
properties or current planning behavior. Production still plans exactly one
PostgreSQL database Create. Multi-step actions are test-only.

`AddPostgreSqlDurableOperationJournal()` registers the per-node journal.
`AddPostgreSqlDurableOperationExecution()` registers exact dispatch and the internal
`DurableGenerationRunner`. Execution is explicit and sequential. One singleton
generation gate covers direct execution, the runner and manual assignment; reads
do not wait for the gate or need handlers/secrets/connectivity. Ready nodes run in
ordinal OperationId order. Every dependency must have Succeeded, including on
direct calls. Any failure, refusal, uncertainty or cancellation stops the pass.
An explicit later pass skips successful nodes and resumes only safe work.

Each attempt retains the Preparing -> confirmed durable Dispatching -> external
effect barrier. Restarted Dispatching becomes NeedsAttention before action
selection and never redispatches, including when support is missing. Missing
Pending/Preparing handlers or unknown input formats return value-free refusals
without new attempts or fabricated journal reasons. State, reason, attempt and
current-attempt guards live in C# compare-and-transition transactions. Declarative
checks/FKs enforce attributable history and consistent states; retained audit
rows and immutable Plan/generation rows retain their existing mutation guards.
Valid privileged catalog edits remain within ADR-023's trust boundary.

`DurableAggregateCompletion` runs inside the finalization/recovery transaction and
locks the deployment row. A node success and its attempt close persist without
advancing AppliedGeneration while siblings remain incomplete. Final-node success
and the eligible pointer advance commit together or roll back together. Partial
external successes remain visible; no rollback of external effects is implied.

Specialized PostgreSQL assignment requires the exact uncertain attempt,
affirmative consent, a bounded protected note and independent existence evidence
against the frozen pinned target. Existence does not prove provenance. Assignment
never sends CREATE. Its immutable receipt identifies resolved OperationId and
target generation and records nullable actual AppliedGeneration at decision time.
Receipt replay performs no new external read or aggregate advancement. Later
sibling completion never rewrites the original audit/receipt.

The internal read-only progress query reports completed/total, node identities,
states, blocking dependencies, last-attempt outcome, stopped work and desired/applied
generation. Host/CLI expose exact operation IDs and action/dependency summaries
without raw action payloads. Current-build pg_dump/pg_restore must preserve the
entire graph, payloads, partial state, attempts and recovery decisions; restored
startup and inspection send no effects. See the
[implementation and evidence design](../../docs/development/104-durable-operation-graph.md)
and [operator commands](../../docs/development/alpha-operator-local.md).
