# Single-instance shadow-pilot rehearsal

For durable operations, attempts and manual recovery history, continue with
the [active-pilot restore rehearsal](active-pilot-restore-rehearsal.md).

This procedure uses one Haby host and one developer console on the same
machine. The alpha gRPC endpoint has no user authentication. Keep it bound to
IPv4 loopback; do not forward the port or publish it through a proxy. The
catalog must be an isolated PostgreSQL database. This rehearsal never executes
a Provider or contacts the hypothetical target database.

## Prepare and start

1. Use the .NET 10 SDK, PostgreSQL 17 client utilities (`pg_dump`,
   `pg_restore`, `createdb`, `psql`), and an isolated catalog database. Grant
   the catalog principal read/write and the schema DDL rights needed for the
   reviewed startup migration. Check `USAGE` and `CREATE` on `public` as
   described in the [catalog guide](../../src/Haby.Persistence.PostgreSql/README.md).
2. Create and trust a localhost certificate and keep its PFX outside the
   repository. See [local alpha operator setup](alpha-operator-local.md) for
   `dotnet dev-certs` commands. Supply the PFX password and PostgreSQL
   connection string through the operator's local secret-management method;
   keep them out of command history, logs and checked-in settings.
3. Set `Haby__PostgreSql__ConnectionString`, `Haby__Tls__CertificatePath`,
   `Haby__Tls__CertificatePassword`, and `Haby__Port=7443` in the host process
   environment. Leave `Haby__Tls__Enabled` unset so TLS remains enabled. Start
   the only host instance from the repository root:

   ```powershell
   dotnet run --project .\src\Haby.Operator.Host
   ```

   Startup must finish its reviewed migrations before listening. Stop on any
   migration error. Verify that the listening address is `https://127.0.0.1:7443`
   and that no wildcard or non-loopback listener exists (`Get-NetTCPConnection
   -LocalPort 7443`). A missing PFX, invalid TLS setting or unreadable PFX must
   prevent startup. Do not disable TLS for this rehearsal.

4. In the console session, use `HABY_OPERATOR_URL=https://localhost:7443`.
   Validate and import the UTF-8 database envelope in the **Hypothetical
   database preview** section of
   [local alpha operator setup](alpha-operator-local.md):

   ```powershell
   dotnet run --project .\src\habyctl -- manifest validate .\manifest-envelope.json
   dotnet run --project .\src\habyctl -- manifest import .\manifest-envelope.json
   ```

   Record the returned Application record ID and revision number. The CLI
   rejects import files over 1,047,552 bytes; the host caps gRPC receive
   messages at 1 MiB. Confirm an oversized file is rejected without an import.

5. Stop the host with Ctrl+C and wait for it to exit. Restart with the same
   catalog and inspect the exact revision and its historical warnings:

   ```powershell
   dotnet run --project .\src\Haby.Operator.Host
   dotnet run --project .\src\habyctl -- manifest revision get '<application-record-id>' 1
   dotnet run --project .\src\habyctl -- manifest revision preview '<application-record-id>' 1 .\preview-context.json
   ```

   Use the non-secret hypothetical context in the local alpha guide. Compare
   the revision ID and warnings with the import result. Repeat preview and
   compare its database name and Provider metadata. Preview says what **would**
   happen; it cannot be applied and does not contact or create the target
   database. It creates no ChangeSet, Plan, deployment, generation or operation.

## Back up and restore the isolated catalog

Stop the host and all catalog writes. Keep the backup outside the repository on
operator-controlled storage. The following commands assume PostgreSQL client
connection parameters, including authentication, are already supplied by the
operator's secret-management method. `$env:PGDATABASE` names the source
catalog. Choose a new, empty restore database name and a private dump path:

```powershell
$restoreDatabase = 'haby_catalog_restore_check'
$dumpPath = Join-Path $env:TEMP 'haby-catalog-rehearsal.dump'
pg_dump --format=custom --file=$dumpPath --dbname=$env:PGDATABASE
if ($LASTEXITCODE -ne 0) { throw 'Catalog backup failed; keep the host stopped.' }
pg_restore --list $dumpPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Backup archive verification failed.' }
createdb $restoreDatabase
if ($LASTEXITCODE -ne 0) { throw 'Empty restore database creation failed.' }
pg_restore --exit-on-error --no-owner --no-privileges --dbname=$restoreDatabase $dumpPath
if ($LASTEXITCODE -ne 0) { throw 'Catalog restore failed; do not use the restored database.' }
```

Run `createdb` and `pg_restore` as the catalog principal where possible. If an
administrator performs the restore, `--no-privileges` does not recreate that
principal's grants: assign it ownership or equivalent DDL rights on restored
catalog objects, plus the step-1 schema and read/write privileges, before
starting Haby. Verify those rights on the restored database with the actual
host principal, including its ability to apply reviewed migrations.

Run these read-only queries against **each** database with `psql` and compare
the exact results, including every migration ID:

```sql
SELECT count(*) FROM applications;
SELECT count(*) FROM manifest_revisions;
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
```

Start a fresh Haby process with its catalog connection set to the restored
database. Startup migration must succeed. Run `manifest revision get` with the
record ID and revision number from the source and compare the historical
warnings. Run the same hypothetical `manifest revision preview` and compare
its result. Verify independently that the named target database is absent.
The restored database must remain separate from the source throughout the
drill. Stop the fresh host when finished.

If backup, restore, migration, count comparison, revision read or preview
fails, keep catalog writes stopped. Preserve the failure evidence without
credentials or dump contents; investigate and repeat into another empty
database. Do not replace the source catalog with an unverified restore. Remove
the test dump and restore database through the operator's approved cleanup
procedure after verification. Never commit a dump, PFX or credentials.
