# Explicit PostgreSQL public schema

Issue [#247](https://github.com/hamstix/haby/issues/247) supports explicit
`schema: public` in `postgresql.application-login/v1alpha1` with the existing
`database-owner/v1` policy. See the [complete example](examples/247-public-application-login.json).
The ordinary [five-step operator path](120-five-step-operator-path.md) and
[configuration delivery](148-authorized-configuration-delivery.md) apply.

Use this only for a newly Haby-created dedicated database and confirmed Haby
application login. It does not migrate or adopt an existing installation, move
data/tables, or transfer ownership of preexisting contained objects. Optional
metadata from an existing installation does not authorize database access.
Custom schema `app` retains its existing behavior. There is no new default;
`pg_*` and `information_schema` remain prohibited.

The final access transaction transfers database ownership to the exact confirmed
application role. For a custom schema it creates an application-owned schema.
For `public` it uses the original standard schema without `CREATE SCHEMA` or
direct schema-ownership reassignment. Its owner remains `pg_database_owner`;
database ownership supplies the application's effective schema-owner authority.
Application-created tables, indexes and sequences belong directly to that login;
administrator-created objects retain their owners.

The database-create receipt records database and public-schema OIDs. Restriction,
login and final access check those exact identities, expected standard schema
owner and restricted ACLs. Final public access also refuses foreign schema ACL
entries. Missing/replaced/foreign state refuses without adoption, recreation or
repair. Haby never grants to `PUBLIC`. The application has database-local
`search_path=public`, no cluster administration powers, and restricted creator
default privileges for future routines/types. This is a database owner policy,
not a runtime least-privilege policy; an owner can subsequently alter its database.

The existing dispatch marker, external transaction and independent confirmation
remain mandatory. A lost response or failed confirmation retains uncertainty;
restart/repeat cannot regenerate credentials or redispatch uncertain writes.
Successful nodes and immutable outputs are reused without touching existing data.
No role/access recovery, deletion or retention policy changes are included.

## Capability boundary and tested venue

Provisioning admission validates required capabilities and exact current state;
it does not impose a PostgreSQL-17-only version allowlist. PostgreSQL 16 is not
rejected merely by version. The existing pinned PostgreSQL 17 integration venue
is unchanged; this slice neither certifies every version nor promises PostgreSQL
12+ compatibility. Standard public ownership changed in PostgreSQL 15, and this
policy still requires `pg_database_owner` and its expected schema ownership.
Nullable role lookups refuse a missing role safely; incompatible catalog/privilege
semantics retain existing bounded refusal/uncertainty outcomes. This does not alter
Haby catalog-server/dependency guarantees. No multi-version matrix or additional
compatibility rehearsal is introduced. User-reported trials are not independently
verified whole-path evidence and do not authorize access to an installation.

## Development catalog boundary

The adapter-owned database output is now `postgresql.database-output/v2` with
required nonzero `databaseOid` and `publicSchemaOid`. The source-generated codec
refuses the prior contract or missing identity. Resource/login/policy and final
connection formats otherwise retain their existing shapes; the selected schema
survives accepted inputs, restart, rendering and output decoding unchanged.
Shared persistence has no Provider-specific column/table changes.

Use an explicitly initialized fresh development catalog under ADR-033 for this
build. There is no backfill, legacy reader or automatic reset. Old accepted graph
selectors and receipts do not satisfy the current adapter; resetting the catalog
never removes external databases, roles or data. Decide separately how retained
disposable resources are handled before another rehearsal.

## Verification scope

Focused tests cover explicit selection, the complete five actions, real new
application-password connections, database-local search_path, schema-qualified and
unqualified tables, real EF migrations/DML, separately asserted database/schema/
object owners, restricted ACL/defaults and denied cluster actions. Public-specific
identity/ACL refusal and restart/lost-response tests supplement the existing
custom-schema provenance, rollback, recovery and restore coverage. The test ledger
retains full method/case multiplicity and all mandatory gates. Declared tests are
not executed success; the PR reports actual source-bound results and limitations.
