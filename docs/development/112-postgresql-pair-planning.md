# PostgreSQL pair planning

Issue #112 implements the planning/admission half of ADR-034 step 2 on PR #111.
It introduces no new external effect, output publication or credential generation.

## Admission and frozen graph

The PostgreSQL adapter owns the exact two-Resource shape: one database and one
application login, same local Application and Provider alias, one exact pinned
Provider instance/configuration/implementation. Login properties are strictly
`databaseRef`, `schema`, `privilegePolicy`; the only policy is `database-owner/v1`.
Schema names follow the database-name syntax, excluding `pg_*`, `public` and
`information_schema`. Source-generated DTO metadata rejects unknown properties.
Bindings target the login and have exactly empty options; multiple Components
share its owner and `application-password` slot. Documents, workloads, exports,
additional Resources and policies are not admitted by this adapter slice.

The opt-in `PostgreSqlPairResourceAdmission` registrations compose the five nodes
specified by [the accepted policy](106-application-credential-policy.md):
`db-create`, `db-restrict`, `app-login-material`, `app-login-create`,
`app-login-access`. Each registration owns only its declared Resource's nodes.
Full dependency sets, action/input contracts, exact Provider references and typed
future-output selectors are frozen in the graph and fingerprint. The restrict
input pins `expectedOwnerUserName` from the exact configuration's administrator.
Database/role evidence is never a materialized value during planning.

`IDurableResourcePlanComposer` is the narrow common seam for adapter-owned
validation, planning and saved-graph validation. Plan, Check and atomic Commit
use the same pure adapter rules and exact Resource lookup. Saved-graph validation
checks all mandatory nodes, typed current input bytes, full dependencies and
selectors against that immutable declaration and pinned configuration; it neither
rewrites a saved graph nor follows a changed alias. Existing single-Resource
registrations retain their typed execution-codec checks without regenerating
saved inputs from mutable planner code.

The generic graph validates producer existence, explicit dependency edges and
exact references. Generated-password selectors additionally require the exact
generated producer, contract/key, same Resource and admission key; its input
owner must match the containing Application/Environment/Resource. Consumption
independently rederives the consumer's owner and checks the stable slot/version.
Allowing database evidence across Resources therefore does not authorize
cross-login passwords. Non-secret selector relationships and exact registered
producer/action/output contracts are checked by the adapter's graph validator;
legacy single-Resource registrations cannot admit arbitrary non-secret selectors.
There is no common PostgreSQL output-contract switch.

The current persisted planning input still selects exactly one Provider alias.
Distinct aliases are rejected, even if they happen to resolve to the same target.
Each node must match its actual Resource contract and the exact Provider envelope.
This does not support combined independent PostgreSQL/RabbitMQ Applications.
The existing non-database generated queue fixture continues through real
Plan/Check/Commit, generated output publication/consumption and backup/restore
without database fields, pair Resources or PostgreSQL action contracts.

## Stable role name

Canonical owner bytes are UTF-8 compact JSON of the ordered array
`[environmentRecordId, applicationRecordId, resourceLocalId]`. UUID strings use
lowercase canonical D form; the stable local ID is unchanged. Use default
System.Text.Json string escaping, no whitespace or trailing newline. These
identities contain only ASCII characters and require no normalization beyond
their existing typed validation. The name is `haby_a_` followed by the first 48
lowercase hexadecimal SHA-256 characters. An independent fixed vector tests it.
Display names, endpoints, generation and randomness do not enter the hash.
Planning makes no external name-availability assertion. Durable per-Provider
name reservation and external collision enforcement remain in the role-effect
slice; a derived name is not proof that a role is free or owned by Haby.

## Production gate

Host registers login contract metadata so the documented example validates and
imports. Host still registers only `PostgreSqlDatabaseCreateAdmission` and the
real standalone `PostgreSqlDatabaseCreateAction`. It does not register pair
admission, generated actions for PostgreSQL, restrict, login or access executors.
Pair admission is explicitly composed only in tests. Missing executors do not
prevent metadata-only pair Plan/Check/Commit in that composition and are never
represented by success-only handlers.

The common exact executor checks all mandatory nodes for exact handler and input
support before any pending node can dispatch. Thus a saved test-composed pair
cannot execute `db-create` through production merely because that handler exists.
The direct PostgreSQL adapter entry remains limited to one standalone node;
multi-node work must pass the common whole-graph barrier. Generation execution
returns HandlerUnavailable, leaves attempts empty and AppliedGeneration unset.
Existing terminal observation and database-create recovery remain unchanged.

Standalone database import, Plan/Check/Commit, execution and recovery keep their
existing production registrations and behavior. The single-action plan description
continues to describe the standalone database only; test-composed pairs expose
their graph, not a misleading single-action summary or production capability.

## Persistence, validation and deferred work

No schema/model/migration or graph-format change is required: the existing
version-2 graph already stores owners, exact keys, typed payloads and selectors.
There is no new reset requirement beyond the current catalog baseline from #110.
Current-build restart and real `pg_dump`/`pg_restore` retain the exact pair graph,
fingerprint and five Pending nodes. No startup reset, historical compatibility
reader or persistent developer-resource cleanup is introduced.

Tests cover the shared-login example, strict metadata and adapter relationships,
exact graph shape, independent hash vector, semantic ordering, tampered graph
refusal even after recomputing its fingerprint, atomic refusal with no desired
state, production registrations and direct/common/generation execution barriers.
Independent contexts and external catalog reads assert no generated slot/output/
audit/attempt or database/role change during planning. Existing standalone and
non-database generated-material suites remain regression gates.

Deferred: restrict/PUBLIC ACL effects, role/SCRAM creation, durable name reservation,
external ownership/access configuration, database/role/final connection output
publication, pair production activation, confidential rendering/delivery, new
recovery, rotation, deprovisioning and combined multi-Provider planning. No
CREATE ROLE, ACL or ownership SQL, new stored workflow or fake executor is added.
