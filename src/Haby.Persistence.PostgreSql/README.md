# PostgreSQL manifest catalog

This project is the EF Core persistence adapter for accepted Applications and
immutable ManifestRevisions. It does not persist deployments or execute
Providers. `IManifestImportStore` retains the existing import use case;
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
Provider instance/revision store separately from the shadow-preview path. Its
closed PostgreSQL shape has version 1, host, port, maintenance database,
administrator user name, exact administrator-password `SecretRef` and exact
implementation ID. Instance endpoint and Environment ownership are immutable;
revisions are append-only and start at 1. The store verifies SecretVersion
ownership through metadata without selecting payload bytes. It checks the
registered checkpoint capability before accepting a revision. No alias,
connection test or live Provider execution is involved.

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
