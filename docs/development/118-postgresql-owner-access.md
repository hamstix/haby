# PostgreSQL initial ownership and application access

Issue #118 completes ADR-034's five real actions in explicitly opted-in test
composition. Production Host registrations remain unchanged: standalone database
Create only. Pair activation and the native CLI/operator rehearsal remain the
following integration slice. This output is internal evidence, not configuration
delivery to a microservice.

## Ownership and exact accepted inputs

Only the login's `privilegePolicy=database-owner/v1` and `databaseRef` select the
owner. There is no reverse database owner reference, `isOwner`, binding override
or declaration-order choice. The adapter checks the independent single-owner
invariant before its narrower exactly-one-database/one-login admission. Plan,
Check and atomic Commit use the same rule. The example's api/worker Components
share one login, generated slot and external role. Multiple logins/profiles and
replacement of an existing managed owner remain unsupported.

`PostgreSqlApplicationAccessAction` implements exactly
`postgresql.application-login.configure-access/v1alpha1`, format
`haby.postgresql.application-login-configure-access/v1`. It rederives the complete
five-node graph from the accepted immutable Application and pinned typed Provider
configuration, comparing Resource ownership, inputs, dependencies and selectors.
All four direct dependencies are required. Database and role evidence require
their exact successful dispatched producer attempts and mandatory outputs;
restriction requires its exact successfully closed dispatched attempt. Material
consumption revalidates owner, slot, profile, creation audit, exact SecretVersion
and saved payload. The final target reads the retained name reservation; a missing
reservation is not recreated. Current aliases and caller credentials are unused.

`PostgreSqlPairExecution` also retains the earlier four-real-action fixture.
Restrict/login admit a complete graph only when the final target is explicitly
composed. The common whole-graph capability barrier still requires every exact
executor before starting any effect. The direct standalone Create entry still
refuses multi-node graphs. No success placeholder is registered.

## External transaction and verification

Before dispatch the target checks PostgreSQL 17, the exact database name/OID,
non-template status, expected administrative SUPERUSER owner, pg_database_owner
public-schema ownership and revoked PUBLIC database/public privileges. It checks
the created role name/OID, LOGIN and all negative cluster flags, no memberships,
initial CONNECT-only access, no existing role settings/defaults, absent application
schema, retained reservation and actual saved-password authentication. A separate
wrong-password connection must receive invalid_password, so trust authentication
cannot manufacture success.

After the durable Dispatching marker is acknowledged, a dedicated non-pooled
administrative session repeats checks and executes one transaction:

1. Set an independently safe `pg_catalog` search_path.
2. Transfer only this newly created database to the exact application role.
3. Create the declared application-owned schema and revoke its PUBLIC privileges.
4. Set that role's database-local search_path to the quoted application schema.
5. Revoke future PUBLIC routine EXECUTE and type USAGE through database-wide
   creator defaults for this application role.

DDL is localized PostgreSQL adapter capability under ADR-026. Identifiers are
validated and quoted; catalog query values are parameters. No password/verifier
appears in this SQL. No REASSIGN OWNED, existing-schema adoption, arbitrary grants,
unrelated role/database/default changes, cluster/HBA changes or stored workflow
functions/triggers are introduced. Provider administration remains available.
Database ownership does not transfer administrator/template-owned contained
objects. PUBLIC access to public remains revoked, with pg_database_owner ownership.

An independent session verifies committed database/role/application-schema OIDs,
owners, role flags/memberships, PUBLIC ACLs, creator defaults and the exact
database-local setting. A fresh application session authenticates with the saved
material, verifies identity/current schema and application privileges; an
independent wrong password remains denied. Only confirmed verification permits
catalog publication.

## Typed output and common persistence

`PostgreSqlApplicationConnectionContract` publishes
`postgresql.application-login-output/v1alpha1`, key `connection`, strict typed
`{host, port, databaseName, schema, userName, password: {secretId, secretVersionId}}`.
The public adapter DTO holds a typed SecretRef. Endpoint comes from the pinned
configuration; password is exactly the generated slot's reference. Unknown/null
fields, invalid identifiers/endpoints and mutable-version spellings fail closed.
There is no material, verifier, connection string or new reveal/RPC/CLI endpoint.

The common `IExternalOperationOutputContract.ValidateAcceptedAsync` hook lets
adapters validate against their accepted context and immutable pinned inputs.
The PostgreSQL contract checks the exact graph, endpoint, owner-derived user,
schema/database and generated reference on publication and internal consumption.
Common storage never decodes those fields. Adapter validation runs outside the
catalog transaction to permit independent context reads with a one-connection
pool. The subsequent C#/EF transaction still conditionally owns the exact attempt,
checks dependencies and publishes output, producer success, successful attempt
closure and eligible aggregate advancement atomically. Immutable accepted inputs
are never rewritten. The typed test queue adapter also exercises context validation
and refusal without PostgreSQL Provider branches.

Reservations add only an exact generic read to the existing shared API; there are
no new Provider-specific tables, fields or EF model/migration changes. The current
catalog baseline remains #116. No additional reset, alpha compatibility bridge,
backfill or automatic external cleanup is introduced.

## Failure, restart and restore

Known PostgreSQL rejection becomes definitive only after acknowledged rollback.
Unknown commit/rollback, cancellation after the marker, independent verification
or authentication failure and catalog output/attempt/aggregate failure retain
NeedsAttention or Dispatching. Restart never sends ownership repair, resets a
password, recreates a missing external resource or republishes unconfirmed work.
Earlier successful effects, material and reservations remain retained. Existing
database-create manual assignment remains its standalone no-output exception;
it cannot authorize this workflow. No new recovery command exists.

Real current-build pg_dump/pg_restore tests compare the entire accepted generation,
outputs, operation/attempt history, deployment state, slots/audits and reservations,
plus exact internally resolved material. Restored success skips every effect;
restored uncertainty stops without replay. This is a catalog backup: external
roles/databases remain in the disposable target cluster. Restoring the catalog
does not recreate those external objects or establish that a cluster-role backup
was taken. Plaintext catalog/backup readers retain their existing secret exposure.

## Acceptance evidence and limits

The disposable PostgreSQL suite explicitly composes real Create, restrict,
generated material, login and final access handlers through import -> Plan ->
Check -> Commit -> exact dispatch. Planning/commit produce no credentials/effects;
AppliedGeneration stays unset until final output and all five successes/attempts
are committed. Repeat/restart preserve exact credentials, role/database OIDs and
outputs. Production Host/missing-handler gates remain regression tests.

Test-only `OwnerMigrationContext` uses EF Core's IMigrator/Database.MigrateAsync
against actual Npgsql as the generated login. Its first migration creates an
additional owned schema, migration history, table, sequence and unique index;
its second adds a structural column. Independent contexts then insert/read/update/
delete. EF owns test-only migration discovery; no Haby production reflection
discovery or new package is added. Independent SQL verifies owners and flags,
future routine/type defaults in two schemas, application schema creation, denied
cluster CREATE DATABASE/ROLE, denied access to hardened database B and unchanged
administrator objects. Unrelated PUBLIC CONNECT demonstrably remains possible.
A database owner can deliberately change its own ACLs; this is the configured
state, not cluster-wide confinement or a runtime least-privilege profile.

Tests include competing owners at Plan/Check/Commit; strict input/selector codecs;
missing/uncertain/wrong output, slot/version/audit/reservation and external drift;
real rollback after ownership/schema/default changes; unknown boundaries and
actual postcommit verification/authentication failures; real catalog constraint
failures at publication/attempt/aggregate; alias movement; single-connection
catalog pools; restart and real restore. Persisted arrange/act/assert contexts
are separate. Standalone recovery, generated material, typed Queue evidence and
existing logging/redaction tests remain required regression gates.

Deferred: production five-node activation, native operator rehearsal, confidential
rendering/delivery, new recovery, restricted administration, additional profiles,
rotation/deprovisioning, adoption/owner replacement and multi-Provider admission.
