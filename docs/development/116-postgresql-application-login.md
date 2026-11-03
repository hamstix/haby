# PostgreSQL application login creation

Subsequent [issue #118](118-postgresql-owner-access.md) implements the final real
ownership/access action and complete five-node test composition. Production Host
pair activation remains deferred. The scope below records the earlier #116 slice.

Issue #116 implements the login-create slice of accepted ADR-006/025/026/032/033/034.
It does not enable the production database/login pair. The Host registrations,
common whole-graph capability gate and direct standalone database entry remain
unchanged. The final ownership/access action has no handler. A real five-node
generation therefore cannot start, partially execute or advance AppliedGeneration.

The private integration fixture removes only the final node from the exact pair
plan. It exercises four real actions through common dispatch, without a fake
final handler. Existing common aggregate semantics apply to that shortened graph:
all four successes apply the fixture generation only. This is not a production
pair policy or evidence of final ownership/access. Separate full five-node tests
require zero attempts/effects and unset AppliedGeneration, including direct calls.

## Exact execution and credentials

`AddPostgreSqlApplicationLogin` explicitly registers
`postgresql.application-login.create/v1alpha1`, input
`haby.postgresql.application-login-create/v1`, and mandatory
`postgresql.role-evidence/v1`, key `role`. No success-only final-access handler is
provided. Composition also requires the real generated-material service and
adapter reservation store; none is registered by the production Host.

The handler rereads the accepted generation, fingerprint, containing Application,
Environment and Resource declarations. It compares the complete internal prefix
against nodes derived from the accepted pair policy and pinned typed Provider
configuration. The frozen owner, role name, database selector, password selector,
producer nodes and all direct dependencies must match exactly. The administrator
and its exact SecretRef come from the pinned Provider revision, never the current
alias. No caller-supplied password or latest SecretVersion is accepted.

Database evidence requires exact successful dispatched CREATE output and nonzero
OID. Local material consumption uses `IGeneratedCredentialOperations`: successful
producer/attempt, stable slot, owner, profile, creation audit, pinned version and
payload all remain required. The target separately resolves that exact SecretRef
with Resource/ApplicationLogin scope. Temporary byte buffers and SecretMaterial
are cleared/disposed. Retained generated material is never regenerated or rotated.

## Provider-neutral reservations and catalog

The common `IProviderResourceReservations` API accepts a typed
`ProviderResourceReservation` with exactly these fields:
`ProviderInstanceId`, `EnvironmentRecordId`, `ApplicationRecordId`,
`ResourceLocalId`, `Namespace` and `Value`. `provider_resource_reservations`
is part of the common catalog EF model in `Haby.Persistence.PostgreSql`, the
PostgreSQL storage backend shared by all adapters. It is not a PostgreSQL Provider
store. No separate adapter database context, migration history or role table exists.

Namespace and Value are opaque, non-secret strings to common code. The API and
model do not know PostgreSQL roles, databases, queue users or naming algorithms.
The PostgreSQL login adapter computes the accepted canonical name from #112 and
passes it as Value under its stable namespace `postgresql.role`. Different
adapters use the same API and table with their own namespaces and opaque values.
This is one narrow immutable association, not a generic workflow/plugin framework,
EAV redesign or arbitrary JSON catalog. Existing common entities retain their
typed fields and adapter configuration remains adapter-interpreted.

The owner key is (ProviderInstanceId, EnvironmentRecordId, ApplicationRecordId,
ResourceLocalId, Namespace). The unique external key is (ProviderInstanceId,
Namespace, Value). Identical repeat acceptance converges on the retained value;
a different value for that owner/namespace or an identity held by another owner
is a conflict, never reassignment. Namespace and Value comparisons are ordinal
and case-sensitive; common code applies storage bounds, not adapter validation.
Foreign keys retain the actual Provider/Environment and Application identities.
The internal caller remains responsible for authorization and binding the owner
to its committed Resource. Acceptance never changes external permissions.

Unique constraints arbitrate races. A lost write response is resolved through a
fresh read of the exact owner association or competing unique key. EF write guards
refuse reservation modification/deletion. Direct privileged SQL corruption is
outside that boundary. Failed/unused reservations and generated material remain
retained; lifecycle reuse and cleanup are deferred.

The common `AddProviderResourceReservations` migration creates only this generic
table and its constraints. The rejected draft-only `AddPostgreSqlRoleNames`
migrations and separate project have been removed. This revision requires an
explicitly initialized fresh catalog under ADR-033: the normal initializer rejects
earlier migration sets and never resets, drops, backfills or adopts them. No
compatibility bridge for the discarded draft schema is provided. Resetting Haby
never removes external roles/databases; an existing external role remains a conflict.

Full-database pg_dump/pg_restore retains reservations alongside the graph, secret
material/refs, outputs and successful/uncertain attempts. The restore tests also
retain an independent opaque namespace/value through the same common store.

## Managed SCRAM implementation and redistribution gate

The implementation is original, scoped Haby code using platform
`Rfc2898DeriveBytes.Pbkdf2`, `HMACSHA256`, `SHA256` and
`RandomNumberGenerator`. No third-party authentication framework, copied crypto
implementation, native library or Npgsql internal helper is introduced. Only the
43-byte ASCII base64url `random-password/v1` profile is accepted: it needs no
general Unicode/SASLprep implementation. Do not reuse this helper for arbitrary
human passwords or another generation profile.

The reviewed algorithm is PBKDF2-HMAC-SHA-256 with 4096 iterations and a fresh
16-byte salt; ClientKey = HMAC(SaltedPassword, "Client Key"), StoredKey =
SHA256(ClientKey), ServerKey = HMAC(SaltedPassword, "Server Key"). The PostgreSQL
serialization is `SCRAM-SHA-256$4096:salt$StoredKey:ServerKey` with standard base64.
The exact emitted format is validated before the dedicated quoted password DDL.
Only the confidential verifier is interpolated. Cleartext password interpolation
is never allowed. This is ADR-034's narrow exception, not a general SQL-secret API.

Primary evidence reviewed for this choice:

- [PostgreSQL 17 scram-common.c](https://github.com/postgres/postgres/blob/REL_17_STABLE/src/common/scram-common.c)
  documents and implements the verifier algorithm and serialization.
- [PostgreSQL 17 SCRAM authentication](https://www.postgresql.org/docs/17/sasl-authentication.html)
  and [password authentication](https://www.postgresql.org/docs/17/auth-password.html)
  describe the server exchange and password preparation.
- [Npgsql public data-source builder](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlDataSourceBuilder.cs)
  and [tracing options](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlTracingOptionsBuilder.cs)
  expose the logging/filter controls; no internal SCRAM API is used.
- [.NET runtime MIT license](https://github.com/dotnet/runtime/blob/v10.0.0/LICENSE.TXT)
  permits commercial distribution subject to its notice requirements.
- [Npgsql PostgreSQL-style license](https://github.com/npgsql/npgsql/blob/v10.0.3/LICENSE)
  permits redistribution for any purpose with required notices. Haby's original
  code remains Apache-2.0. Existing package/runtime notices must accompany a
  downstream distribution; no new restrictive license is introduced.

No relevant Npgsql/SCRAM source was found in the requested local library directory.
Context7 resolution succeeded but its documentation query failed with a transport
error; the public primary source above supplied API evidence instead.

Real PostgreSQL 17 tests execute the actual material/create/restrict/login path.
The exact saved password authenticates, current_user/current_database match and
an independently chosen wrong password receives `invalid_password`. The target
itself requires both outcomes before publication, so trust authentication cannot
manufacture success. This is interoperability evidence, not a verifier-string-only
assertion.

## Transaction, publication and uncertainty

After preflight and local reservation acceptance, the durable Dispatching barrier
must be acknowledged before external mutation. A dedicated non-pooled connection
rechecks name/OID, administrative database owner, pg_database_owner public-schema
ownership, restricted PUBLIC privileges and role absence. PostgreSQL 17 SUPERUSER
administration is supported; restricted administration remains deferred.

One transaction uses `search_path = pg_catalog`, quoted identifiers, CREATE ROLE
with LOGIN/NOSUPERUSER/NOCREATEDB/NOCREATEROLE/NOREPLICATION/NOBYPASSRLS and the
validated verifier, then GRANT CONNECT to the dedicated database. A concurrent
name collision cannot ALTER/reset/adopt the winner. Existing roles are conflicts
even when their attributes/password appear compatible. No memberships, ownership,
schema, default privileges, role search_path or final connection output are added.

After acknowledged commit an independent session verifies the exact role OID,
fixed flags, absence of memberships, explicit nongrantable CONNECT and absence of
CREATE/TEMP, plus unchanged expected database/ACL evidence. Independent exact-
password authentication follows. Only then can `IExternalOperationOutputs`
atomically publish roleName/roleOid, close the exact dispatched attempt and record
producer success/eligible aggregate completion. OIDs establish consistency, not
provenance or adoption authority. Ordinary journal success cannot omit evidence.

Acknowledged rollback after a known PostgreSQL rejection is definitive. Unknown
commit/rollback, cancellation after the marker, failed verification/authentication
or failed catalog publication retain NeedsAttention or Dispatching. Restart and
explicit retry never send another CREATE/ALTER/password change. No compensation,
drop/reset/adoption or new recovery command exists. Database-create manual
assignment remains its exact standalone exception and cannot resolve this action.

## Supported logging boundary and limitations

Password DDL uses its own NpgsqlDataSource with NullLoggerFactory, parameter
logging disabled and command/batch/copy/physical-open tracing disabled. The verifier statement uses public NpgsqlBatch, not NpgsqlCommand: pinned Npgsql
10.0.3 emits no batch SQL through its separate Npgsql.Sql EventSource. This is a
version-specific, source-inspected property gated by captured EventListener tests,
not a promise for arbitrary future Npgsql versions. Changing this transport or
upgrading Npgsql must pass those tests again. A direct-command prototype failed
that gate, with value-free assertion evidence; it is not the implemented transport.
Batch command text is cleared after execution and exceptions never become ordinary diagnostics.
These controls do not change logging for catalog, restrict or unrelated SQL.

Dedicated administrative connections explicitly set connection-local startup
options: log_statement=none, log_min_error_statement=panic, log_min_messages=panic,
log_min_duration_statement=-1, log_min_duration_sample=-1,
log_transaction_sample_rate=0, log_duration=off,
log_parameter_max_length_on_error=0, log_error_verbosity=terse; debug parse/rewrite/
plan and parser/planner/executor/statement statistics logging are off. These are
part of opting into this adapter, never ALTER SYSTEM/ALTER ROLE changes. The
operator must permit these session controls. The adapter checks PostgreSQL 17,
logging settings, empty shared/session/local preload libraries and only plpgsql
extensions before password SQL. Other audit/extension configurations are
unsupported and refuse before dispatch or retain uncertainty after the marker.

Tests strengthen ordinary disposable-server statement logging, check a visible
control statement and capture both success and failure logs plus global client
logging, Activity traces and Npgsql.Sql EventSource payloads. They assert no generated password or SCRAM verifier;
assertions never echo confidential fixture values. Test changes to disposable
server logging are restored. No production cluster audit policy is modified.

This is not protection against privileged server/catalog/backup readers, process
memory inspection, proxies, external audit systems or extensions loaded by a
privileged concurrent administrator. Npgsql/ADO.NET require immutable managed
strings for passwords and SQL; their contents cannot be reliably zeroed. Buffers
are cleared and connection lifetimes bounded, but GC/string-copy lifetime is a
limitation. Plaintext secret storage and backups retain their existing exposure.
An operator requiring immutable auditing of password statements cannot use this
opt-in until a separately approved confidential transport/logging design exists.

The login is not yet database owner. No EF migration readiness, completed
database-owner isolation or configuration delivery is claimed. Final access,
full production activation, rotation, deprovisioning, restricted administration,
new recovery and confidential delivery remain separate work.
