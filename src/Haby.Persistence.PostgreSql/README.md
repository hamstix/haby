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
