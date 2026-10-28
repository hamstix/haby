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

`AddPostgreSqlDurableDeploymentPlanner()` registers the internal asynchronous
`IDurableDeploymentPlanner` boundary. It stores one exact Manifest revision and
an `ExpectedAbsence` precondition per ChangeSet. `ExpectedGeneration` is
explicitly unsupported until durable ApplicationDeployments exist. Planning
accepts only one `postgresql.database/v1alpha1` Resource with a validated
database name. It resolves an Environment-local Provider alias to one sealed,
decodable configuration revision and registered implementation, then stores
only the exact identities and a versioned fingerprint. No credential or
configuration payload is copied into a Plan.

Every Plan expires 24 hours after creation. `ClosePlanAsync` records an
independent, immutable closure with its technical actor. Closed and expired
Plans remain readable for audit but `CheckPlanAsync` rejects them. Expiry wins
when both conditions hold; closing an expired Plan still records the actor.
No retention deletion runs in this
slice. `CheckPlanAsync` compares the exact typed inputs and fingerprint; an
alias switch makes the prior Plan stale and a new Plan can be
created for the same ChangeSet. The application boundary exposes no desired
state commit or Provider execution.

Planning reads several durable records through separate contexts, so it is not
snapshot-isolated across a concurrent alias switch. A Plan may therefore be
stale immediately after creation; callers must check it again before any
future desired-state acceptance. In this slice no durable deployment exists,
so only expected absence is stored and a ChangeSet-stale mismatch has no
reachable producer. A later deployment/commit slice must add the actual
generation mismatch detail required by ADR-016. Corrupt historical Plan rows
fail closed with `InvalidDataException`; a future operator transport must map
that to a redacted typed error. `ImplementationId` remains a string to match
the current Provider alias resolver; these contracts are internal pilot APIs.

EF migration tooling uses `ManifestCatalogDesignTimeFactory` to inspect or add
migrations without a database connection. Supply a connection explicitly when
running a database operation through the CLI; normal startup migrations use
the registered context and its configured connection.

## First durable desired generation

`IDurableDesiredStateCommitter` accepts only a persisted one-Application
`ExpectedAbsence` Plan. The additive migration stores one deployment, immutable
generation-1 snapshot and one ChangeSetCommit. The snapshot contains exact
Manifest and Provider identities, explicit `Present` presence, alias provenance
and the Plan fingerprint;
it has no Provider configuration payload or credential. AppliedGeneration stays
null. Provider execution and operator transport remain separate.

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

The first desired-generation transaction also inserts one `durable_create_operations`
row for the exact local database Resource ID. Its composite primary key is the
deployment, generation, Resource ID and `Create` action. `Pending` means no
attempt has crossed the dispatch barrier. The operation references the immutable
generation for its exact ProviderInstance, configuration revision and
ImplementationId; reads do not resolve the current alias. The journal stores no
Provider configuration payload, SecretVersion material or connection string.
The pilot journal row rejects deletion. ADR-025 state transitions update the
retained row; see the next section.
This commit transaction does not dispatch SQL or advance `AppliedGeneration`.

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

## Durable create dispatch barrier (ADR-025)

`AddPostgreSqlDurableCreateOperationJournal()` registers the typed operation
journal, and `AddPostgreSqlDurableCreateOperationExecution()` (in
`Haby.PostgreSql`) registers the internal single-instance executor over it.
`PostgreSqlDurableCreateOperationExecutor.ExecuteAsync` runs one exact
`DurableCreateOperationKey`: it re-reads the accepted generation's pinned
Provider instance, configuration revision and implementation, revalidates the
database name from the pinned Manifest revision, and rejects bounded invalid
inputs before any SQL. It is not exposed through the operator host; there is no
operator endpoint or scheduler. The separate internal recovery boundary below
can assign a verified existing database from `NeedsAttention` with explicit consent.

State transitions (all compare-and-transition updates, never read-modify-write):

- `Pending -> Preparing`: `TryBeginAttemptAsync` opens a new attempt row in
  `durable_create_operation_attempts` with its own attempt identity and number;
  an earlier attempt that never reached the barrier is closed as
  `AbandonedBeforeDispatch`. Preparing is resumable after a restart.
- `Preparing -> Dispatching`: `TryCommitDispatchMarkerAsync` commits the unique
  attempt's `Dispatching` marker in Haby PostgreSQL immediately before Npgsql
  `CREATE DATABASE` is sent. If the marker commit outcome is uncertain, no SQL
  is sent and the operation stays recoverable.
- `Preparing -> Preparing`: an unavailable Provider input, SecretStore read or
  target connection closes only the attempt with a bounded reason. A later call
  opens a new attempt against the same pinned generation and clears that reason;
  no marker or Create was sent for the closed attempt.
- `Preparing -> Failed`: a definitive pre-dispatch rejection (present database,
  invalid name, unsupported or corrupt immutable Manifest shape) records a safe
  reason code and closes the operation.
- `Dispatching -> Succeeded`: requires the confirmed `CREATE` response, an
  independent existence read and `AppliedGeneration` advanced from null to the
  generation in the same transaction. A preexisting database is a conflict,
  never an adoption.
- `Dispatching -> Failed`: a definitive server rejection including
  `duplicate_database` records `Conflict` or `ServerRejected` without adoption.
- `Dispatching -> NeedsAttention`: after a committed marker, cancellation,
  connection loss or any uncertainty records `UncertainAfterDispatch`. Nothing
  is retried automatically and no false success is recorded, even when the
  database exists.
- Restart over `Dispatching`: `TryRecoverDispatchingAsync` moves the operation
  to `NeedsAttention` without touching the Provider, even if the database is
  present.

Terminal rows and closed attempt outcomes are protected through database
guards: update triggers reject identity changes and terminal rewrites except
the audited `NeedsAttention -> Succeeded` recovery transition, attempt
history is append-only, and check constraints bind `Dispatching` to
`dispatched_at` without `finalized_at`, attempt numbering to the current
attempt identity, and `UncertainAfterDispatch` to `NeedsAttention`.

`AppliedGeneration` is never advanced for an uncertain or interrupted window.
If recording a confirmed success fails, the operation remains `Dispatching`
(recoverable) instead of leaving an applied generation that was not durably
recorded and independently verified.

Restart procedure for a `NeedsAttention` create operation: stop the pilot
writer and inspect the exact operation through `IPostgreSqlDatabaseRecovery.InspectAsync`
before deciding. The internal `AssignExistingDatabaseAsync` command requires the
same Environment and uncertain attempt, affirmative acknowledgment, the fixed
`OperatorAcceptedExistingDatabase` reason, a required note of at most 512
characters and a trusted technical actor. It checks database existence through
the pinned Provider revision and SecretRef without issuing `CREATE DATABASE`.
The database's presence does not prove that Haby created it. The operator accepts
that provenance risk; if unsure, leave the operation in `NeedsAttention`.

The note is protected audit data, available through the internal journal's
`FindDecisionAsync` only. Do not include credentials, connection strings,
Provider payloads or driver errors. Input validation rejects obvious patterns
but cannot prove the text is secret-free. The technical actor records the
allowed caller's claim; it does not authenticate a person. The single-instance,
network-restricted operator boundary remains necessary. A successful decision,
`Succeeded` and `AppliedGeneration` commit together; the uncertain attempt stays
unchanged. A lost-response retry returns the original receipt without a new
Provider check. The [alpha operator recovery commands](../../docs/development/alpha-operator-local.md#explicit-recovery-of-one-uncertain-operation)
adapt this command without changing those decisions. Absent databases have no
authorized retry path in this slice.

Under ADR-026, the recovery journal uses an EF Core transaction and conditional
updates for the assignment. Declarative checks and deferred foreign keys tie a
committed decision to the exact current `Uncertain` attempt, `Succeeded` state,
and immutable generation history. The transaction advances `AppliedGeneration`
before the guarded operation transition; the terminal guard requires that
pointer to match at transition time. Later generations may advance the pointer
without invalidating the retained decision. The only new trigger prevents
UPDATE/DELETE of retained decisions; the existing terminal-operation guard gains
only the audited recovery transition. These guards prevent rewriting retained
rows, but do not authenticate
a privileged database writer or make an inserted decision proof of a human's
identity. PostgreSQL-specific deferred foreign-key DDL and the exact row lock
remain because EF Core cannot express those semantics directly.
